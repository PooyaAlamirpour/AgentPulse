using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed class PrepareSessionRun(
    IProjectRepository projectRepository,
    ISessionRepository sessionRepository,
    IMessageRepository messageRepository,
    IRunLeaseRepository runLeaseRepository,
    IUnitOfWork unitOfWork,
    IClock clock,
    SessionRunOptions options) : IPrepareSessionRun
{
    private const string RecoveryFailureReason =
        "Recovered after the previous run lease expired before completion.";

    public async Task<PrepareSessionRunResult> ExecuteAsync(
        PrepareSessionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProjectContext);

        if (string.IsNullOrWhiteSpace(request.UserPrompt))
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidUserPrompt,
                "The user prompt cannot be empty.");
        }

        options.Validate();
        var utcNow = SessionRunTime.GetUtcNow(clock);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var project = await UpsertProjectAsync(request.ProjectContext, utcNow, cancellationToken);
        var session = await ResolveSessionAsync(
            project.Id,
            request.SessionId,
            utcNow,
            cancellationToken);

        var previousHistory = await RecoverExpiredRunIfRequiredAsync(
            session,
            utcNow,
            cancellationToken);

        if (session.Status == SessionStatus.Running)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                $"Session '{session.Id}' is running without a recoverable expired lease.");
        }

        var runLease = new RunLease(
            session.Id,
            RunLeaseId.New(),
            utcNow,
            utcNow.Add(options.LeaseDuration));
        await runLeaseRepository.AddAsync(runLease, cancellationToken);

        // Persist the lease inside the still-uncommitted transaction before reading
        // history or allocating sequences. Other database writers cannot pass this point.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        previousHistory ??= await messageRepository.ListBySessionIdAsync(
            session.Id,
            cancellationToken);

        var maximumSequence = await messageRepository.GetMaximumSequenceAsync(
            session.Id,
            cancellationToken);

        var userMessage = new Message(
            MessageId.New(),
            session.Id,
            MessageRole.User,
            maximumSequence + 1,
            utcNow);
        userMessage.AddTextPart(
            MessagePartId.New(),
            1,
            request.UserPrompt,
            utcNow);
        userMessage.Complete(utcNow);

        var assistantMessage = new Message(
            MessageId.New(),
            session.Id,
            MessageRole.Assistant,
            maximumSequence + 2,
            utcNow);
        assistantMessage.AddTextPart(MessagePartId.New(), 1, string.Empty, utcNow);
        assistantMessage.StartStreaming(utcNow);

        await messageRepository.AddAsync(userMessage, cancellationToken);
        await messageRepository.AddAsync(assistantMessage, cancellationToken);
        session.Start(utcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PrepareSessionRunResult(
            project,
            session,
            userMessage,
            assistantMessage,
            previousHistory,
            runLease);
    }

    private async Task<Project> UpsertProjectAsync(
        ProjectContext projectContext,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var candidate = new Project(
            projectContext.ProjectId,
            projectContext.ProjectRoot,
            projectContext.IsGitRepository,
            projectContext.GitWorktree,
            utcNow);

        await projectRepository.UpsertAsync(candidate, cancellationToken);

        return await projectRepository.GetByIdAsync(projectContext.ProjectId, cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.ProjectNotFound,
                $"Project '{projectContext.ProjectId}' could not be loaded after registration.");
    }

    private async Task<Session> ResolveSessionAsync(
        ProjectId projectId,
        SessionId? requestedSessionId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (requestedSessionId is null)
        {
            var newSession = new Session(SessionId.New(), projectId, utcNow);
            await sessionRepository.AddAsync(newSession, cancellationToken);
            return newSession;
        }

        var session = await sessionRepository.GetByIdAsync(
            requestedSessionId.Value,
            cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.SessionNotFound,
                $"Session '{requestedSessionId.Value}' does not exist or is no longer available.");

        if (session.ProjectId != projectId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.SessionProjectMismatch,
                $"Session '{session.Id}' does not belong to project '{projectId}'.");
        }

        return session;
    }

    private async Task<IReadOnlyList<Message>?> RecoverExpiredRunIfRequiredAsync(
        Session session,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var existingLease = await runLeaseRepository.GetBySessionIdAsync(
            session.Id,
            cancellationToken);

        if (existingLease is null)
        {
            return null;
        }

        if (!existingLease.IsExpired(utcNow))
        {
            throw new SessionRunException(
                SessionRunErrorCode.SessionAlreadyRunning,
                $"Session '{session.Id}' already has an active run.");
        }

        var history = await messageRepository.ListBySessionIdAsync(
            session.Id,
            cancellationToken);

        foreach (var message in history.Where(
                     static message =>
                         message.Role == MessageRole.Assistant &&
                         message.Status == MessageStatus.Streaming))
        {
            message.Fail(RecoveryFailureReason, utcNow);
        }

        if (session.Status == SessionStatus.Running)
        {
            session.Stop(utcNow);
        }

        runLeaseRepository.Remove(existingLease);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return history;
    }
}

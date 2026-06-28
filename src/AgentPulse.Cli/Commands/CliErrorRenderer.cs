using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentPulse.Cli.Commands;

public sealed class CliErrorRenderer(
    IConsole console,
    ILogger<CliErrorRenderer> logger) : ICliErrorRenderer
{
    public async Task<int> RenderAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var rendered = Map(exception);
        logger.LogDebug(
            "CLI failure category {FailureCategory}; exception type {ExceptionType}.",
            rendered.Category,
            exception.GetType().FullName ?? exception.GetType().Name);

        await WriteErrorAsync(rendered.Message, cancellationToken);
        return rendered.ExitCode;
    }

    public async Task<int> RenderUsageAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await WriteErrorAsync(message, cancellationToken);
        return ExitCodes.Usage;
    }

    public async Task<int> RenderConfigurationAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await WriteErrorAsync(message, cancellationToken);
        return ExitCodes.Configuration;
    }

    public async Task<int> RenderCancellationAsync(CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(
            "Operation cancelled.".AsMemory(),
            cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
        return ExitCodes.Cancelled;
    }

    public async Task<int> RenderConfigurationAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        logger.LogDebug(
            "CLI startup configuration failure; exception type {ExceptionType}.",
            exception.GetType().FullName ?? exception.GetType().Name);
        var rendered = InvalidConfiguration();
        await WriteErrorAsync(rendered.Message, cancellationToken);
        return rendered.ExitCode;
    }

    private async Task WriteErrorAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
    }

    private static RenderedCliError Map(Exception exception)
    {
        if (Find<CredentialResolutionException>(exception) is { } credentialResolution)
        {
            return new(
                ExitCodes.Configuration,
                credentialResolution.Message,
                "CredentialMissing");
        }

        if (Find<ProviderCredentialStoreException>(exception) is not null)
        {
            return new(
                ExitCodes.Configuration,
                "The credential store could not be accessed safely.",
                "CredentialStore");
        }

        if (Find<ProviderCredentialValidationException>(exception) is { } credentialValidation)
        {
            return new(
                ExitCodes.Configuration,
                credentialValidation.Message,
                "CredentialInvalid");
        }

        if (Find<ProjectContextException>(exception) is { } projectContext)
        {
            return MapProjectContext(projectContext);
        }

        if (Find<SessionRunException>(exception) is { } sessionRun)
        {
            return MapSessionRun(sessionRun);
        }

        if (Find<ModelProviderException>(exception) is { } provider)
        {
            return MapProvider(provider);
        }

        if (Find<ModelProviderOperationCanceledException>(exception) is not null)
        {
            return new(
                ExitCodes.Cancelled,
                "The model provider cancelled the request before completion.",
                "ProviderCancellation");
        }

        if (Find<ModelRunException>(exception) is { } modelRun)
        {
            return MapModelRun(modelRun);
        }

        if (Find<ChatModelRequestException>(exception) is not null)
        {
            return new(
                ExitCodes.Usage,
                "The model request could not be built from the stored session history.",
                "Validation");
        }

        if (Find<OptionsValidationException>(exception) is not null)
        {
            return InvalidConfiguration();
        }

        if (Find<ArgumentException>(exception) is not null)
        {
            return new(
                ExitCodes.Usage,
                "The command input is invalid.",
                "Validation");
        }

        if (Find<OperationCanceledException>(exception) is not null)
        {
            return new(
                ExitCodes.Cancelled,
                "Operation cancelled.",
                "Cancellation");
        }

        return new(
            ExitCodes.Failure,
            "The command failed unexpectedly.",
            "Unexpected");
    }

    private static RenderedCliError MapProjectContext(ProjectContextException exception)
    {
        var message = exception.ErrorCode switch
        {
            ProjectContextErrorCode.InvalidPath =>
                "The project directory is invalid.",
            ProjectContextErrorCode.PathNotFound =>
                "The project directory does not exist.",
            ProjectContextErrorCode.PathIsNotDirectory =>
                "The project path is not a directory.",
            ProjectContextErrorCode.PathAccessDenied =>
                "The project directory cannot be accessed.",
            ProjectContextErrorCode.GitProcessTimedOut =>
                "Git discovery timed out while resolving the project directory.",
            ProjectContextErrorCode.GitProcessFailed =>
                "Git could not resolve the project directory.",
            _ => "The project directory could not be resolved safely.",
        };

        return new(ExitCodes.Usage, message, "Validation");
    }

    private static RenderedCliError MapSessionRun(SessionRunException exception)
    {
        return exception.Code switch
        {
            SessionRunErrorCode.SessionNotFound => new(
                ExitCodes.Session,
                "The requested session does not exist or is no longer available.",
                "SessionNotFound"),
            SessionRunErrorCode.SessionProjectMismatch => new(
                ExitCodes.Session,
                "The requested session belongs to a different project.",
                "SessionMismatch"),
            SessionRunErrorCode.SessionAlreadyRunning => new(
                ExitCodes.Session,
                "The requested session already has an active run.",
                "SessionBusy"),
            SessionRunErrorCode.InvalidUserPrompt => new(
                ExitCodes.Usage,
                "The prompt cannot be empty.",
                "Validation"),
            SessionRunErrorCode.InvalidSessionState or
                SessionRunErrorCode.RunLeaseNotFound or
                SessionRunErrorCode.RunLeaseOwnershipMismatch or
                SessionRunErrorCode.RunLeaseExpired => new(
                    ExitCodes.Session,
                    "The session run lock is no longer valid.",
                    "SessionLease"),
            _ => new(
                ExitCodes.Failure,
                "The session run state could not be updated safely.",
                "Persistence"),
        };
    }

    private static RenderedCliError MapModelRun(ModelRunException exception)
    {
        if (exception.Code == ModelRunErrorCode.ProviderFailure &&
            exception.ProviderErrorCode is { } providerErrorCode)
        {
            return MapProvider(providerErrorCode);
        }

        return exception.Code switch
        {
            ModelRunErrorCode.ProviderCancelled => new(
                ExitCodes.Cancelled,
                "The model provider cancelled the request before completion.",
                "ProviderCancellation"),
            ModelRunErrorCode.ProviderFailure => new(
                ExitCodes.Provider,
                "The model provider request failed.",
                "ProviderFailure"),
            ModelRunErrorCode.InvalidStream => new(
                ExitCodes.Provider,
                "The model provider returned an invalid streaming response.",
                "Protocol"),
            ModelRunErrorCode.ValidationFailure => new(
                ExitCodes.Usage,
                "The model request could not be built from the stored session history.",
                "Validation"),
            ModelRunErrorCode.LeaseLost => new(
                ExitCodes.Session,
                "The session run lock was lost while streaming.",
                "SessionLease"),
            ModelRunErrorCode.PersistenceFailure => new(
                ExitCodes.Failure,
                "The model response could not be persisted safely.",
                "Persistence"),
            ModelRunErrorCode.OutputFailure => new(
                ExitCodes.Failure,
                "The model response could not be written safely.",
                "Output"),
            _ => new(
                ExitCodes.Failure,
                "The model run failed before completion.",
                "Unexpected"),
        };
    }

    private static RenderedCliError MapProvider(ModelProviderException exception) =>
        MapProvider(exception.Code);

    private static RenderedCliError MapProvider(ModelProviderErrorCode code)
    {
        if (code == ModelProviderErrorCode.Timeout)
        {
            return new(
                ExitCodes.Timeout,
                "The model provider request timed out.",
                "Timeout");
        }

        var message = code switch
        {
            ModelProviderErrorCode.Authentication =>
                "The model provider rejected the API credential.",
            ModelProviderErrorCode.PermissionDenied =>
                "The model provider denied access to the requested resource.",
            ModelProviderErrorCode.RateLimited =>
                "The model provider rate limit was exceeded.",
            ModelProviderErrorCode.InvalidRequest =>
                "The model provider rejected the request.",
            ModelProviderErrorCode.Unavailable =>
                "The model provider is temporarily unavailable.",
            ModelProviderErrorCode.Protocol or ModelProviderErrorCode.InvalidResponse =>
                "The model provider returned an invalid response.",
            ModelProviderErrorCode.UnsupportedFeature =>
                "The model provider does not support the requested operation.",
            _ => "The model provider request failed.",
        };

        return new(ExitCodes.Provider, message, code.ToString());
    }

    private static RenderedCliError InvalidConfiguration()
    {
        return new(
            ExitCodes.Configuration,
            "The model configuration is missing or invalid. Configure AgentPulse:Model before running the command.",
            "Configuration");
    }

    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        if (exception is TException match)
        {
            return match;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var nested = Find<TException>(inner);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return exception.InnerException is null
            ? null
            : Find<TException>(exception.InnerException);
    }

    private sealed record RenderedCliError(
        int ExitCode,
        string Message,
        string Category);
}

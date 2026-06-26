using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class RepositoryTests
{
    [Fact]
    public async Task Complete_domain_graph_is_saved_and_loaded_with_utc_timestamps()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var message = PersistenceTestData.CreateMessage(session, 1, "hello");

        await using (var writeContext = database.CreateContext())
        {
            var projectRepository = new ProjectRepository(writeContext);
            var sessionRepository = new SessionRepository(writeContext);
            var messageRepository = new MessageRepository(writeContext);
            var unitOfWork = new UnitOfWork(writeContext);

            await projectRepository.AddAsync(project);
            await sessionRepository.AddAsync(session);
            await messageRepository.AddAsync(message);
            await unitOfWork.SaveChangesAsync();
        }

        await using var readContext = database.CreateContext();
        var loadedProject = Assert.IsType<AgentPulse.Domain.Projects.Project>(
            await new ProjectRepository(readContext).GetByIdAsync(project.Id));
        var loadedSession = Assert.IsType<AgentPulse.Domain.Sessions.Session>(
            await new SessionRepository(readContext).GetByIdAsync(session.Id));
        var loadedMessage = Assert.IsType<AgentPulse.Domain.Messages.Message>(
            await new MessageRepository(readContext).GetByIdAsync(message.Id));

        Assert.Equal(DateTimeKind.Utc, loadedProject.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedSession.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedMessage.CreatedAtUtc.Kind);

        var textPart = Assert.IsType<AgentPulse.Domain.Messages.TextMessagePart>(
            Assert.Single(loadedMessage.Parts));
        Assert.Equal("hello", textPart.Text);
        Assert.Equal(DateTimeKind.Utc, textPart.CreatedAtUtc.Kind);
    }

    [Fact]
    public async Task Messages_are_loaded_in_sequence_order()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var second = PersistenceTestData.CreateMessage(session, 2, "second");
        var first = PersistenceTestData.CreateMessage(session, 1, "first");

        await using (var writeContext = database.CreateContext())
        {
            await writeContext.AddRangeAsync(project, session, second, first);
            await writeContext.SaveChangesAsync();
        }

        await using var readContext = database.CreateContext();
        var messages = await new MessageRepository(readContext).ListBySessionIdAsync(session.Id);

        Assert.Equal(new long[] { 1, 2 }, messages.Select(message => message.Sequence));
    }
}

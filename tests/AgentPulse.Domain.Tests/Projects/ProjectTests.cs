using AgentPulse.Domain.Projects;

namespace AgentPulse.Domain.Tests.Projects;

public sealed class ProjectTests
{
    [Fact]
    public void Constructor_requires_utc_timestamp()
    {
        var exception = Assert.Throws<ArgumentException>(() => new Project(
            ProjectId.New(),
            "/workspace/project",
            false,
            null,
            DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)));

        Assert.Equal("createdAtUtc", exception.ParamName);
    }

    [Fact]
    public void Constructor_protects_git_worktree_invariant()
    {
        Assert.Throws<ArgumentException>(() => new Project(
            ProjectId.New(),
            "/workspace/project",
            true,
            null,
            DateTime.UtcNow));

        Assert.Throws<ArgumentException>(() => new Project(
            ProjectId.New(),
            "/workspace/project",
            false,
            "/workspace/project",
            DateTime.UtcNow));
    }

    [Fact]
    public void Strong_identifier_rejects_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => new ProjectId(Guid.Empty));
    }
}

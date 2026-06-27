namespace AgentPulse.Application.ProjectContexts;

public interface IProjectFileSystem
{
    string GetCurrentDirectory();

    string NormalizePath(string path, string baseDirectory);

    ProjectPathEntryKind GetEntryKind(string path);

    string CanonicalizePath(string normalizedAbsolutePath, ProjectPlatform platform);

    bool IsPathWithin(string path, string candidateParent, ProjectPlatform platform);
}

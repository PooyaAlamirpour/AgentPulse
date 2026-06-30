namespace AgentPulse.Infrastructure.Mutations;

internal interface IMutationFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    void DeleteDirectory(string path);

    void DeleteFile(string path);

    void MoveFile(string source, string destination, bool overwrite = false);

    void ReplaceFile(string source, string destination, string backup);

    Task WriteStagedFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        UnixFileMode? unixMode,
        CancellationToken cancellationToken);

    void SetAttributes(string path, FileAttributes attributes);

    void SetUnixFileMode(string path, UnixFileMode mode);
}

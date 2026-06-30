namespace AgentPulse.Infrastructure.Mutations;

internal sealed class SystemMutationFileSystem : IMutationFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public void DeleteFile(string path) => File.Delete(path);

    public void MoveFile(string source, string destination, bool overwrite = false) =>
        File.Move(source, destination, overwrite);

    public void ReplaceFile(string source, string destination, string backup) =>
        File.Replace(source, destination, backup, ignoreMetadataErrors: true);

    public async Task WriteStagedFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        UnixFileMode? unixMode,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81_920,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        if (unixMode is { } mode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    public void SetAttributes(string path, FileAttributes attributes) =>
        File.SetAttributes(path, attributes);

    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix file modes are not supported on Windows.");
        }

        File.SetUnixFileMode(path, mode);
    }
}

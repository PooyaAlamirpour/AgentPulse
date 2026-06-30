using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Mutations;
using AgentPulse.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Infrastructure.Tests.Mutations;

internal static class MutationTestSupport
{
    public static MutationToolOptions CreateOptions() => new()
    {
        MaxFileBytes = 1024 * 1024,
        MaxPatchBytes = 1024 * 1024,
        MaxDiffPreviewCharacters = 12_000,
        DiffContextLines = 3,
    };

    public static IWorkspaceMutationService CreateService(
        MutationToolOptions? options = null,
        IMutationFileSystem? fileSystem = null,
        IUnifiedDiffGenerator? diffGenerator = null)
    {
        options ??= CreateOptions();
        var policy = new ProtectedPathPolicy(
            new WorkspacePathResolver(),
            options,
            NullLogger<ProtectedPathPolicy>.Instance);
        return new WorkspaceMutationService(
            policy,
            diffGenerator ?? new UnifiedDiffGenerator(options),
            new PathLockManager(),
            fileSystem ?? new SystemMutationFileSystem(),
            options,
            NullLogger<WorkspaceMutationService>.Instance);
    }

    public static ApplyPatchAgentTool CreatePatchTool(
        MutationToolOptions? options = null,
        IMutationFileSystem? fileSystem = null)
    {
        options ??= CreateOptions();
        return new ApplyPatchAgentTool(
            new ApplyPatchParser(options),
            CreateService(options, fileSystem));
    }

    public static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        string workspaceRoot,
        string argumentsJson,
        IAgentToolResourcePermissionAuthorizer? authorizer = null,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return await tool.ExecuteAsync(
            document.RootElement,
            new AgentToolExecutionContext(
                workspaceRoot,
                authorizer ?? new RecordingAuthorizer()),
            cancellationToken);
    }

    internal sealed class RecordingAuthorizer(
        bool allow = true,
        Action<PermissionCall>? onCall = null) : IAgentToolResourcePermissionAuthorizer
    {
        private readonly List<PermissionCall> _calls = [];

        public IReadOnlyList<PermissionCall> Calls => _calls;

        public Task<PermissionAuthorizationResult> AuthorizeAsync(
            string operation,
            string target,
            string? description,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new PermissionCall(operation, target, description);
            _calls.Add(call);
            onCall?.Invoke(call);
            return Task.FromResult(allow
                ? PermissionAuthorizationResult.Allow()
                : PermissionAuthorizationResult.Reject(
                    AgentToolResult.Failure(
                        "Permission denied for mutation test.",
                        classification: AgentToolFailureClassification.Deterministic),
                    status: PermissionAuthorizationStatus.ApprovalUnavailable));
        }
    }

    internal sealed record PermissionCall(
        string Operation,
        string Target,
        string? Description);

    internal sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"agentpulse-mutation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathOf(string relativePath) => Path.Combine(
            Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    internal class DelegatingMutationFileSystem : IMutationFileSystem
    {
        protected SystemMutationFileSystem Inner { get; } = new();

        public virtual bool FileExists(string path) => Inner.FileExists(path);

        public virtual bool DirectoryExists(string path) => Inner.DirectoryExists(path);

        public virtual void CreateDirectory(string path) => Inner.CreateDirectory(path);

        public virtual void DeleteDirectory(string path) => Inner.DeleteDirectory(path);

        public virtual void DeleteFile(string path) => Inner.DeleteFile(path);

        public virtual void MoveFile(string source, string destination, bool overwrite = false) =>
            Inner.MoveFile(source, destination, overwrite);

        public virtual void ReplaceFile(string source, string destination, string backup) =>
            Inner.ReplaceFile(source, destination, backup);

        public virtual Task WriteStagedFileAsync(
            string path,
            ReadOnlyMemory<byte> content,
            UnixFileMode? unixMode,
            CancellationToken cancellationToken) =>
            Inner.WriteStagedFileAsync(path, content, unixMode, cancellationToken);

        public virtual void SetAttributes(string path, FileAttributes attributes) =>
            Inner.SetAttributes(path, attributes);

        public virtual void SetUnixFileMode(string path, UnixFileMode mode) =>
            Inner.SetUnixFileMode(path, mode);
    }

    internal sealed class FaultingMutationFileSystem(int failOnCommitOperation)
        : DelegatingMutationFileSystem
    {
        private int _commitOperations;

        public override void MoveFile(string source, string destination, bool overwrite = false)
        {
            ThrowWhenRequested();
            base.MoveFile(source, destination, overwrite);
        }

        public override void ReplaceFile(string source, string destination, string backup)
        {
            ThrowWhenRequested();
            base.ReplaceFile(source, destination, backup);
        }

        private void ThrowWhenRequested()
        {
            _commitOperations++;
            if (_commitOperations == failOnCommitOperation)
            {
                throw new IOException("Injected mutation commit failure.");
            }
        }
    }

    internal sealed class CountingMutationFileSystem : DelegatingMutationFileSystem
    {
        public int StagedWriteCount { get; private set; }

        public int MoveCount { get; private set; }

        public int ReplaceCount { get; private set; }

        public override Task WriteStagedFileAsync(
            string path,
            ReadOnlyMemory<byte> content,
            UnixFileMode? unixMode,
            CancellationToken cancellationToken)
        {
            StagedWriteCount++;
            return base.WriteStagedFileAsync(path, content, unixMode, cancellationToken);
        }

        public override void MoveFile(string source, string destination, bool overwrite = false)
        {
            MoveCount++;
            base.MoveFile(source, destination, overwrite);
        }

        public override void ReplaceFile(string source, string destination, string backup)
        {
            ReplaceCount++;
            base.ReplaceFile(source, destination, backup);
        }
    }

    internal sealed class RecordingUnifiedDiffGenerator(MutationToolOptions options)
        : IUnifiedDiffGenerator
    {
        private readonly UnifiedDiffGenerator _inner = new(options);

        public int CallCount { get; private set; }

        public UnifiedDiffResult CreateAdd(string relativePath, string newText)
        {
            CallCount++;
            return _inner.CreateAdd(relativePath, newText);
        }

        public UnifiedDiffResult CreateUpdate(string relativePath, string oldText, string newText)
        {
            CallCount++;
            return _inner.CreateUpdate(relativePath, oldText, newText);
        }

        public UnifiedDiffResult CreateDelete(string relativePath, string oldText)
        {
            CallCount++;
            return _inner.CreateDelete(relativePath, oldText);
        }

        public UnifiedDiffResult CreateMove(
            string oldRelativePath,
            string newRelativePath,
            string oldText,
            string newText)
        {
            CallCount++;
            return _inner.CreateMove(oldRelativePath, newRelativePath, oldText, newText);
        }

        public UnifiedDiffResult Combine(IEnumerable<UnifiedDiffResult> diffs)
        {
            CallCount++;
            return _inner.Combine(diffs);
        }
    }

    internal sealed class FailOnBackupDeleteMutationFileSystem(int failOnBackupDelete)
        : DelegatingMutationFileSystem
    {
        private int _backupDeletes;

        public override void DeleteFile(string path)
        {
            if (path.EndsWith(".bak", StringComparison.Ordinal))
            {
                _backupDeletes++;
                if (_backupDeletes == failOnBackupDelete)
                {
                    throw new IOException("Injected backup cleanup failure.");
                }
            }

            base.DeleteFile(path);
        }
    }

    internal sealed class CancelDuringStagingFileSystem(CancellationTokenSource source)
        : DelegatingMutationFileSystem
    {
        public override async Task WriteStagedFileAsync(
            string path,
            ReadOnlyMemory<byte> content,
            UnixFileMode? unixMode,
            CancellationToken cancellationToken)
        {
            var partial = content.Length == 0 ? ReadOnlyMemory<byte>.Empty : content[..1];
            await File.WriteAllBytesAsync(path, partial.ToArray(), CancellationToken.None);
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal sealed class FailDuringStagingFileSystem : DelegatingMutationFileSystem
    {
        public override async Task WriteStagedFileAsync(
            string path,
            ReadOnlyMemory<byte> content,
            UnixFileMode? unixMode,
            CancellationToken cancellationToken)
        {
            var partial = content.Length == 0 ? ReadOnlyMemory<byte>.Empty : content[..1];
            await File.WriteAllBytesAsync(path, partial.ToArray(), cancellationToken);
            throw new IOException("Injected mutation staging failure.");
        }
    }

    internal sealed class CancelAfterFirstCommitFileSystem(CancellationTokenSource source)
        : DelegatingMutationFileSystem
    {
        private int _commitOperations;

        public override void MoveFile(string source, string destination, bool overwrite = false)
        {
            base.MoveFile(source, destination, overwrite);
            CancelAfterFirstOperation();
        }

        public override void ReplaceFile(string source, string destination, string backup)
        {
            base.ReplaceFile(source, destination, backup);
            CancelAfterFirstOperation();
        }

        private void CancelAfterFirstOperation()
        {
            _commitOperations++;
            if (_commitOperations == 1)
            {
                source.Cancel();
            }
        }
    }

    public static void AssertNoMutationArtifacts(string root)
    {
        Assert.Empty(Directory.EnumerateFiles(root, ".agentpulse-*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, ".agentpulse-*.bak", SearchOption.AllDirectories));
    }
}

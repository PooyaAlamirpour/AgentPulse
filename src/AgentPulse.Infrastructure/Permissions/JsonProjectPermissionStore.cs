using System.Text.Json;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;

namespace AgentPulse.Infrastructure.Permissions;

public sealed class JsonProjectPermissionStore : IProjectPermissionStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _rootPath;

    public JsonProjectPermissionStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<bool> ContainsAsync(
        ProjectId projectId,
        PermissionApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using var fileLock = await AcquireLockAsync(projectId, cancellationToken);
        var approvals = await LoadAsync(projectId, cancellationToken);
        return approvals.Any(candidate => ApprovalsEqual(candidate, approval));
    }

    public async Task AddAsync(
        ProjectId projectId,
        PermissionApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using var fileLock = await AcquireLockAsync(projectId, cancellationToken);
        var approvals = await LoadAsync(projectId, cancellationToken);
        if (approvals.Any(candidate => ApprovalsEqual(candidate, approval)))
        {
            return;
        }

        approvals.Add(approval);
        await SaveAsync(projectId, approvals, cancellationToken);
    }

    private async Task<List<PermissionApproval>> LoadAsync(
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var path = GetDocumentPath(projectId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ProjectPermissionDocument>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (document is null || document.Version != CurrentVersion || document.Approvals is null)
            {
                throw new PermissionStoreException(
                    $"Project permission data for project '{projectId}' has an unsupported or incomplete schema.");
            }

            var approvals = new List<PermissionApproval>(document.Approvals.Count);
            foreach (var record in document.Approvals)
            {
                if (record is null)
                {
                    throw new PermissionStoreException(
                        $"Project permission data for project '{projectId}' contains an invalid approval.");
                }

                approvals.Add(PermissionApproval.Create(record.Tool, record.Operation, record.Target));
            }

            return approvals
                .Distinct(PermissionApprovalComparer.Instance)
                .ToList();
        }
        catch (PermissionStoreException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PermissionStoreException(
                $"Project permission data for project '{projectId}' contains invalid JSON.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new PermissionStoreException(
                $"Project permission data for project '{projectId}' could not be read safely.",
                exception);
        }
    }

    private async Task SaveAsync(
        ProjectId projectId,
        IReadOnlyCollection<PermissionApproval> approvals,
        CancellationToken cancellationToken)
    {
        var path = GetDocumentPath(projectId);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new ProjectPermissionDocument(
            CurrentVersion,
            approvals
                .OrderBy(static approval => approval.ToolName, StringComparer.Ordinal)
                .ThenBy(static approval => approval.Operation, StringComparer.Ordinal)
                .ThenBy(static approval => approval.Target, GetTargetComparer())
                .Select(static approval => (ProjectPermissionRecord?)new ProjectPermissionRecord(
                    approval.ToolName,
                    approval.Operation,
                    approval.Target))
                .ToList());

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PermissionStoreException(
                $"Project permission data for project '{projectId}' could not be written safely.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var lockPath = GetDocumentPath(projectId) + ".lock";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PermissionStoreException(
                    $"Project permission data for project '{projectId}' could not be locked safely.",
                    exception);
            }
        }
    }

    private string GetDocumentPath(ProjectId projectId)
    {
        return Path.Combine(_rootPath, projectId.Value.ToString("N") + ".permissions.json");
    }

    private static bool ApprovalsEqual(PermissionApproval left, PermissionApproval right)
    {
        return PermissionApprovalComparer.Instance.Equals(left, right);
    }

    private static StringComparer GetTargetComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private sealed record ProjectPermissionDocument(
        int Version,
        List<ProjectPermissionRecord?> Approvals);

    private sealed record ProjectPermissionRecord(
        string Tool,
        string Operation,
        string Target);

    private sealed class PermissionApprovalComparer : IEqualityComparer<PermissionApproval>
    {
        public static PermissionApprovalComparer Instance { get; } = new();

        public bool Equals(PermissionApproval? left, PermissionApproval? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal) &&
                   string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
                   string.Equals(
                       left.Target,
                       right.Target,
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal);
        }

        public int GetHashCode(PermissionApproval approval)
        {
            var target = OperatingSystem.IsWindows()
                ? approval.Target.ToUpperInvariant()
                : approval.Target;
            return HashCode.Combine(approval.ToolName, approval.Operation, target);
        }
    }
}

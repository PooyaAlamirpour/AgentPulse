namespace AgentPulse.Infrastructure.Mutations;

internal sealed class PathLockManager : IPathLockManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries;
    private readonly StringComparer _comparer;

    public PathLockManager()
    {
        _comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _entries = new Dictionary<string, Entry>(_comparer);
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var ordered = paths
            .Select(Path.GetFullPath)
            .Distinct(_comparer)
            .OrderBy(static path => path, _comparer)
            .ToArray();
        var leases = new List<(string Path, Entry Entry)>(ordered.Length);
        lock (_sync)
        {
            foreach (var path in ordered)
            {
                if (!_entries.TryGetValue(path, out var entry))
                {
                    entry = new Entry();
                    _entries.Add(path, entry);
                }

                entry.ReferenceCount++;
                leases.Add((path, entry));
            }
        }

        var acquired = 0;
        try
        {
            foreach (var lease in leases)
            {
                await lease.Entry.Semaphore.WaitAsync(cancellationToken);
                acquired++;
            }

            return new Releaser(this, leases);
        }
        catch
        {
            for (var index = acquired - 1; index >= 0; index--)
            {
                leases[index].Entry.Semaphore.Release();
            }

            ReleaseReferences(leases);
            throw;
        }
    }

    private void Release(IReadOnlyList<(string Path, Entry Entry)> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Entry.Semaphore.Release();
        }

        ReleaseReferences(leases);
    }

    private void ReleaseReferences(IReadOnlyList<(string Path, Entry Entry)> leases)
    {
        lock (_sync)
        {
            foreach (var lease in leases)
            {
                lease.Entry.ReferenceCount--;
                if (lease.Entry.ReferenceCount == 0 &&
                    lease.Entry.Semaphore.CurrentCount == 1 &&
                    _entries.TryGetValue(lease.Path, out var current) &&
                    ReferenceEquals(current, lease.Entry))
                {
                    _entries.Remove(lease.Path);
                    lease.Entry.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class Releaser(
        PathLockManager owner,
        IReadOnlyList<(string Path, Entry Entry)> leases) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(leases);
            }

            return ValueTask.CompletedTask;
        }
    }
}

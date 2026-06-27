using System.Text;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Infrastructure.Tests.Credentials;

public sealed class ProviderCredentialStoreTests
{
    [Fact]
    public async Task Credential_is_encrypted_in_a_temporary_user_scope_root()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        const string secret = "test-secret-value";

        await store.SaveAsync(secret);

        Assert.True(await store.ExistsAsync());
        Assert.Equal(secret, await store.GetAsync());

        var allBytes = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        Assert.False(ContainsSequence(allBytes, Encoding.UTF8.GetBytes(secret)));
    }

    [Fact]
    public async Task Save_replaces_previous_credential_and_delete_is_idempotent()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));

        await store.SaveAsync("first");
        await store.SaveAsync("second");

        Assert.Equal("second", await store.GetAsync());

        await store.DeleteAsync();
        await store.DeleteAsync();

        Assert.False(await store.ExistsAsync());
        Assert.Null(await store.GetAsync());
    }

    [Fact]
    public async Task Corrupt_credential_has_a_clear_safe_error()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        await store.SaveAsync("safe-secret");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory.Path, "xiaomi-mimo.credential"),
            "corrupt");

        var exception = await Assert.ThrowsAsync<ProviderCredentialStoreException>(
            () => store.GetAsync());

        Assert.Contains("could not be read or decrypted", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unix_directory_and_credential_file_are_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        await store.SaveAsync("secret");

        var directoryMode = File.GetUnixFileMode(directory.Path);
        var fileMode = File.GetUnixFileMode(
            System.IO.Path.Combine(directory.Path, "xiaomi-mimo.credential"));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            directoryMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            fileMode);
    }

    [Fact]
    public async Task Prompted_credential_is_saved_only_after_successful_http_acceptance()
    {
        var store = new RecordingCredentialStore();
        var session = new ProviderCredentialSession(store);
        session.Set("prompt-key", ProviderCredentialSource.Prompt);

        Assert.Null(store.SavedCredential);

        await session.MarkAcceptedAsync();
        await session.MarkAcceptedAsync();

        Assert.Equal("prompt-key", store.SavedCredential);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Environment_credential_is_not_persisted_automatically()
    {
        var store = new RecordingCredentialStore();
        var session = new ProviderCredentialSession(store);
        session.Set("environment-key", ProviderCredentialSource.Environment);

        await session.MarkAcceptedAsync();

        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Rejected_stored_credential_is_deleted_but_prompted_key_is_not_saved()
    {
        var storedStore = new RecordingCredentialStore();
        var storedSession = new ProviderCredentialSession(storedStore);
        storedSession.Set("stored-key", ProviderCredentialSource.Stored);
        await storedSession.MarkAuthenticationRejectedAsync();

        Assert.Equal(1, storedStore.DeleteCount);

        var promptedStore = new RecordingCredentialStore();
        var promptedSession = new ProviderCredentialSession(promptedStore);
        promptedSession.Set("prompt-key", ProviderCredentialSource.Prompt);
        await promptedSession.MarkAuthenticationRejectedAsync();

        Assert.Equal(0, promptedStore.SaveCount);
        Assert.Equal(0, promptedStore.DeleteCount);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RecordingCredentialStore : IProviderCredentialStore
    {
        public string? SavedCredential { get; private set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedCredential);
        }

        public Task SaveAsync(
            string credential,
            CancellationToken cancellationToken = default)
        {
            SavedCredential = credential;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            SavedCredential = null;
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedCredential is not null);
        }
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentPulse.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

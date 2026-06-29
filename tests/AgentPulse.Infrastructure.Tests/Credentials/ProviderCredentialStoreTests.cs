using System.Text;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Infrastructure.Tests.Credentials;

public sealed class ProviderCredentialStoreTests
{
    [Fact]
    public void Scoped_store_contract_requires_scope_and_legacy_operations_are_separate()
    {
        var scopedMethods = typeof(IProviderCredentialStore).GetMethods();

        Assert.NotEmpty(scopedMethods);
        Assert.All(
            scopedMethods,
            method => Assert.Contains(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(ProviderCredentialScope)));
        Assert.DoesNotContain(
            scopedMethods,
            method => method.Name.Contains("Legacy", StringComparison.Ordinal));
        Assert.All(
            typeof(ILegacyProviderCredentialStore).GetMethods(),
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(ProviderCredentialScope)));
    }

    [Theory]
    [InlineData("\rsecret-key")]
    [InlineData("secret-key\n")]
    [InlineData("secret\r\nkey")]
    [InlineData("secret\tkey")]
    [InlineData("secret\0key")]
    [InlineData("secret\u0001key")]
    [InlineData("   ")]
    public async Task Store_rejects_raw_unsafe_credentials_before_writing(
        string unsafeCredential)
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));

        var exception = await Assert.ThrowsAsync<ProviderCredentialValidationException>(() =>
            store.SaveAsync(ProviderCredentialScope.Default, unsafeCredential));

        Assert.Equal("The configured API credential contains invalid characters.", exception.Message);
        if (!string.IsNullOrWhiteSpace(unsafeCredential))
        {
            Assert.DoesNotContain(unsafeCredential, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "provider-*.credential"));
    }

    [Fact]
    public async Task Store_normalizes_only_ordinary_surrounding_spaces_after_validation()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));

        await store.SaveAsync(ProviderCredentialScope.Default, "  valid key  ");

        Assert.Equal("valid key", await store.GetAsync(ProviderCredentialScope.Default));
    }

    [Fact]
    public async Task Scoped_credential_is_encrypted_in_a_temporary_user_scope_root()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        const string secret = "test-secret-value";
        var scope = ProviderCredentialScope.Default;

        await store.SaveAsync(scope, secret);

        Assert.True(await store.ExistsAsync(scope));
        Assert.Equal(secret, await store.GetAsync(scope));

        var allBytes = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        Assert.False(ContainsSequence(allBytes, Encoding.UTF8.GetBytes(secret)));
    }

    [Fact]
    public async Task Scoped_save_replaces_previous_credential_and_delete_is_idempotent()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        var scope = ProviderCredentialScope.Default;

        await store.SaveAsync(scope, "first");
        await store.SaveAsync(scope, "second");

        Assert.Equal("second", await store.GetAsync(scope));

        await store.DeleteAsync(scope);
        await store.DeleteAsync(scope);

        Assert.False(await store.ExistsAsync(scope));
        Assert.Null(await store.GetAsync(scope));
    }

    [Fact]
    public async Task Corrupt_legacy_credential_has_a_clear_safe_error()
    {
        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        await store.SaveLegacyAsync("safe-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "legacy-provider.credential"),
            "corrupt");

        var exception = await Assert.ThrowsAsync<ProviderCredentialStoreException>(
            () => store.GetLegacyAsync());

        Assert.Contains("could not be read or decrypted", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unix_directory_and_scoped_credential_file_are_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var directory = new TemporaryDirectory();
        var store = new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
        await store.SaveAsync(ProviderCredentialScope.Default, "secret");

        var directoryMode = File.GetUnixFileMode(directory.Path);
        var credentialPath = Directory
            .EnumerateFiles(directory.Path, "provider-*.credential", SearchOption.TopDirectoryOnly)
            .Single();
        var fileMode = File.GetUnixFileMode(credentialPath);

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
        var session = CreateSession(store);
        session.Set("prompt-key", ProviderCredentialSource.Prompt);

        Assert.Equal(0, store.SaveCount);

        await session.MarkAcceptedAsync();
        await session.MarkAcceptedAsync();

        Assert.Equal("prompt-key", store.SavedCredential);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
    }

    [Fact]
    public void Prompted_credential_is_not_saved_after_provider_failure()
    {
        var store = new RecordingCredentialStore();
        var session = CreateSession(store);
        session.Set("prompt-key", ProviderCredentialSource.Prompt);

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
    }

    [Fact]
    public async Task Environment_credential_is_not_persisted_automatically()
    {
        var store = new RecordingCredentialStore();
        var session = CreateSession(store);
        session.Set("environment-key", ProviderCredentialSource.Environment);

        await session.MarkAcceptedAsync();

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
    }

    [Fact]
    public async Task Stored_credential_is_not_rewritten_after_success()
    {
        var store = new RecordingCredentialStore
        {
            ThrowOnSave = true,
        };
        var session = CreateSession(store);
        session.Set("stored-key", ProviderCredentialSource.Stored);

        await session.MarkAcceptedAsync();

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
    }

    [Fact]
    public async Task Legacy_credential_migrates_once_only_after_success()
    {
        var store = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var session = CreateSession(store);
        session.Set("legacy-key", ProviderCredentialSource.LegacyStored);

        await session.MarkAcceptedAsync();
        await session.MarkAcceptedAsync();

        Assert.Equal("legacy-key", store.SavedCredential);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.DeleteLegacyCount);
        Assert.Null(store.LegacyCredential);
    }

    [Fact]
    public void Legacy_credential_remains_when_provider_run_fails_before_acceptance()
    {
        var store = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var session = CreateSession(store);
        session.Set("legacy-key", ProviderCredentialSource.LegacyStored);

        Assert.Equal("legacy-key", store.LegacyCredential);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
    }

    [Fact]
    public async Task Failed_legacy_migration_never_deletes_legacy_credential()
    {
        var store = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
            ThrowOnSave = true,
        };
        var session = CreateSession(store);
        session.Set("legacy-key", ProviderCredentialSource.LegacyStored);

        await Assert.ThrowsAsync<ProviderCredentialStoreException>(
            () => session.MarkAcceptedAsync());

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
        Assert.Equal("legacy-key", store.LegacyCredential);
    }

    [Fact]
    public async Task Rejected_legacy_credential_deletes_scoped_and_legacy_copies_once()
    {
        var store = new RecordingCredentialStore
        {
            SavedCredential = "legacy-key",
            LegacyCredential = "legacy-key",
        };
        var session = CreateSession(store);
        session.Set("legacy-key", ProviderCredentialSource.LegacyStored);

        await session.MarkAuthenticationRejectedAsync();
        await session.MarkAuthenticationRejectedAsync();

        Assert.Equal(1, store.DeleteCount);
        Assert.Equal(1, store.DeleteLegacyCount);
        Assert.Null(store.SavedCredential);
        Assert.Null(store.LegacyCredential);
    }

    [Fact]
    public async Task Rejected_stored_credential_is_deleted_but_prompted_key_is_not_saved()
    {
        var storedStore = new RecordingCredentialStore();
        var storedSession = CreateSession(storedStore);
        storedSession.Set("stored-key", ProviderCredentialSource.Stored);
        await storedSession.MarkAuthenticationRejectedAsync();

        Assert.Equal(1, storedStore.DeleteCount);

        var promptedStore = new RecordingCredentialStore();
        var promptedSession = CreateSession(promptedStore);
        promptedSession.Set("prompt-key", ProviderCredentialSource.Prompt);
        await promptedSession.MarkAuthenticationRejectedAsync();

        Assert.Equal(0, promptedStore.SaveCount);
        Assert.Equal(0, promptedStore.DeleteCount);
    }

    private static ProviderCredentialSession CreateSession(RecordingCredentialStore store)
    {
        return new ProviderCredentialSession(
            store,
            store,
            ProviderCredentialScope.Default);
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

    private sealed class RecordingCredentialStore :
        IProviderCredentialStore,
        ILegacyProviderCredentialStore
    {
        public string? SavedCredential { get; set; }

        public string? LegacyCredential { get; set; }

        public bool ThrowOnSave { get; set; }

        public int SaveCount { get; private set; }

        public int DeleteCount { get; private set; }

        public int DeleteLegacyCount { get; private set; }

        public Task<string?> GetAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SavedCredential);
        }

        public Task SaveAsync(
            ProviderCredentialScope scope,
            string credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            if (ThrowOnSave)
            {
                throw new ProviderCredentialStoreException(
                    "The model endpoint credential could not be stored securely.");
            }

            SavedCredential = credential;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedCredential = null;
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SavedCredential is not null);
        }

        public Task<string?> GetLegacyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LegacyCredential);
        }

        public Task DeleteLegacyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegacyCredential = null;
            DeleteLegacyCount++;
            return Task.CompletedTask;
        }

        public Task<bool> LegacyExistsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LegacyCredential is not null);
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

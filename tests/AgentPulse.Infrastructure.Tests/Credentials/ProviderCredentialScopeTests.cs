using System.Text;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Tests.Credentials;

public sealed class ProviderCredentialScopeTests
{
    [Fact]
    public void Scope_normalizes_host_default_port_and_ignores_base_path_and_model()
    {
        var first = Scope(
            "https://PROVIDER.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "X-Api-Key");
        var second = Scope(
            "https://provider.example:443/another/path",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "x-api-key");

        Assert.Equal(first, second);
        Assert.Equal(first.FileId, second.FileId);
        Assert.Equal("https://provider.example:443|ApiKeyHeader|x-api-key", first.CanonicalValue);
    }

    [Fact]
    public void Scope_distinguishes_scheme_port_authentication_and_header()
    {
        var https = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "api-key");
        var http = Scope(
            "http://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "api-key");
        var otherPort = Scope(
            "https://provider.example:8443/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "api-key");
        var bearer = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);
        var otherHeader = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "x-api-key");

        Assert.NotEqual(https, http);
        Assert.NotEqual(https, otherPort);
        Assert.NotEqual(https, bearer);
        Assert.NotEqual(https, otherHeader);
    }

    [Fact]
    public async Task Stored_credential_is_available_only_to_the_same_scope()
    {
        await using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var first = Scope(
            "https://first.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);
        var second = Scope(
            "https://second.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);

        await store.SaveAsync(first, "first-secret");

        Assert.Equal("first-secret", await store.GetAsync(first));
        Assert.Null(await store.GetAsync(second));
    }

    [Fact]
    public async Task Clearing_one_scope_does_not_delete_another_scope()
    {
        await using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var first = Scope(
            "https://first.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);
        var second = Scope(
            "https://second.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "x-api-key");

        await store.SaveAsync(first, "first-secret");
        await store.SaveAsync(second, "second-secret");
        await store.DeleteAsync(first);
        await store.DeleteAsync(first);

        Assert.Null(await store.GetAsync(first));
        Assert.Equal("second-secret", await store.GetAsync(second));
    }

    [Fact]
    public async Task Legacy_credential_is_separate_and_migrates_after_default_endpoint_acceptance()
    {
        await using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        const string secret = "legacy-secret";
        await store.SaveLegacyAsync(secret);
        var legacyPath = Path.Combine(directory.Path, "legacy-provider.credential");
        var customScope = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            "api-key");

        Assert.Null(await store.GetAsync(customScope));
        Assert.Null(await store.GetAsync(ProviderCredentialScope.Default));
        Assert.Equal(secret, await store.GetLegacyAsync());
        Assert.True(File.Exists(legacyPath));

        var session = new ProviderCredentialSession(
            store,
            store,
            ProviderCredentialScope.Default);
        session.Set(secret, ProviderCredentialSource.LegacyStored);
        await session.MarkAcceptedAsync();

        Assert.False(File.Exists(legacyPath));
        Assert.Equal(secret, await store.GetAsync(ProviderCredentialScope.Default));
    }

    [Fact]
    public async Task Corrupt_legacy_credential_is_safe_and_scoped_reads_remain_isolated()
    {
        await using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        await store.SaveLegacyAsync("legacy-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "legacy-provider.credential"),
            "corrupt");
        var customScope = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);

        Assert.Null(await store.GetAsync(customScope));
        Assert.Null(await store.GetAsync(ProviderCredentialScope.Default));
        var exception = await Assert.ThrowsAsync<ProviderCredentialStoreException>(
            () => store.GetLegacyAsync());
        Assert.DoesNotContain("legacy-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_file_name_and_protected_content_do_not_contain_the_api_key()
    {
        await using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var scope = Scope(
            "https://provider.example/v1",
            OpenAiCompatibleAuthenticationMode.Bearer,
            null);
        const string secret = "secret-that-must-not-appear";

        await store.SaveAsync(scope, secret);

        var credentialFile = Assert.Single(
            Directory.EnumerateFiles(directory.Path, "provider-*.credential"));
        Assert.DoesNotContain(secret, Path.GetFileName(credentialFile), StringComparison.Ordinal);
        Assert.False(ContainsSequence(
            File.ReadAllBytes(credentialFile),
            Encoding.UTF8.GetBytes(secret)));
    }

    private static bool ContainsSequence(byte[] source, byte[] value)
    {
        return source.AsSpan().IndexOf(value) >= 0;
    }

    private static DataProtectionProviderCredentialStore CreateStore(
        TemporaryDirectory directory)
    {
        return new DataProtectionProviderCredentialStore(
            new ProviderCredentialStoreOptions(directory.Path));
    }

    private static ProviderCredentialScope Scope(
        string baseUrl,
        OpenAiCompatibleAuthenticationMode authenticationMode,
        string? headerName)
    {
        return ProviderCredentialScope.Create(
            new Uri(baseUrl, UriKind.Absolute),
            authenticationMode,
            headerName);
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentPulse.ScopeTests",
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

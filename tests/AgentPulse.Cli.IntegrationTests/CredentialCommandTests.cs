using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class CredentialCommandTests
{
    [Fact]
    public async Task Interactive_run_prompts_for_missing_key_without_echoing_it()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var secretReader = new RecordingSecretInputReader(" prompt-key ");
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            secretReader,
            console);
        var session = new ProviderCredentialSession(store);

        await resolver.ResolveForRunAsync(session);

        Assert.Equal("prompt-key", session.GetRequiredCredential());
        Assert.Equal(1, secretReader.ReadCount);
        Assert.Contains("API credential was not found for the current model endpoint.", console.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("Enter MIMO_API_KEY:", console.StandardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("prompt-key", console.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Empty_prompted_key_is_rejected()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader("   "),
            console);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(new ProviderCredentialSession(store)));

        Assert.Equal("The configured API credential contains invalid characters.", exception.Message);
    }

    [Theory]
    [InlineData("\rsecret-key")]
    [InlineData("secret-key\n")]
    [InlineData("secret\r\nkey")]
    [InlineData("secret\tkey")]
    [InlineData("secret\0key")]
    [InlineData("secret\u0001key")]
    [InlineData("   ")]
    public async Task Unsafe_environment_credentials_are_rejected_without_persistence(
        string unsafeCredential)
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader
            {
                [ProviderCredentialResolver.EnvironmentVariableName] = unsafeCredential,
            },
            store,
            new RecordingSecretInputReader("unused"),
            console);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(new ProviderCredentialSession(store)));

        Assert.Equal("The configured API credential contains invalid characters.", exception.Message);
        if (!string.IsNullOrWhiteSpace(unsafeCredential))
        {
            Assert.DoesNotContain(unsafeCredential, exception.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(0, store.SaveCount);
    }

    [Theory]
    [InlineData("\rsecret-key")]
    [InlineData("secret-key\n")]
    [InlineData("secret\tkey")]
    public async Task Unsafe_prompted_credentials_are_rejected_without_persistence(
        string unsafeCredential)
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader(unsafeCredential),
            console);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(new ProviderCredentialSession(store)));

        Assert.Equal("The configured API credential contains invalid characters.", exception.Message);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Unsafe_stored_credential_is_rejected_before_use()
    {
        const string unsafeCredential = "stored-key\n";
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore { StoredCredential = unsafeCredential };
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader("unused"),
            console);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(new ProviderCredentialSession(store)));

        Assert.Equal("The configured API credential contains invalid characters.", exception.Message);
        Assert.DoesNotContain(unsafeCredential, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Environment_variable_has_priority_and_is_not_saved_or_prompted()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore { StoredCredential = "stored-key" };
        var secretReader = new RecordingSecretInputReader("prompt-key");
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader
            {
                [ProviderCredentialResolver.EnvironmentVariableName] = " env-key ",
            },
            store,
            secretReader,
            console);
        var session = new ProviderCredentialSession(store);

        await resolver.ResolveForRunAsync(session);
        await session.MarkAcceptedAsync();

        Assert.Equal("env-key", session.GetRequiredCredential());
        Assert.Equal(0, secretReader.ReadCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Stored_key_is_used_on_second_run_without_prompt()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore { StoredCredential = "stored-key" };
        var secretReader = new RecordingSecretInputReader("prompt-key");
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            secretReader,
            console);
        var session = new ProviderCredentialSession(store);

        await resolver.ResolveForRunAsync(session);
        await session.MarkAcceptedAsync();

        Assert.Equal("stored-key", session.GetRequiredCredential());
        Assert.Equal(0, secretReader.ReadCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Non_interactive_run_without_key_has_actionable_error()
    {
        var console = new TestConsole(input: string.Empty, isInputRedirected: true);
        var store = new RecordingCredentialStore();
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader("unused"),
            console);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(new ProviderCredentialSession(store)));

        Assert.Contains("Set MIMO_API_KEY", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agentpulse auth set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Official_xiaomi_legacy_credential_is_migrated_only_after_success()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var options = new OpenAiCompatibleModelOptions();
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            store,
            new RecordingSecretInputReader("unused"),
            console,
            options);
        var session = new ProviderCredentialSession(
            store,
            store,
            options.CreateCredentialScope());

        await resolver.ResolveForRunAsync(session);

        Assert.Equal("legacy-key", session.GetRequiredCredential());
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);

        await session.MarkAcceptedAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.DeleteLegacyCount);
        Assert.Null(store.LegacyCredential);
    }

    [Fact]
    public async Task Custom_endpoint_never_reads_or_migrates_legacy_xiaomi_credential()
    {
        var console = new TestConsole(input: string.Empty, isInputRedirected: true);
        var store = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var options = GenericOptions("https://provider.example/v1", "PROVIDER_API_KEY");
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            store,
            new RecordingSecretInputReader("unused"),
            console,
            options);

        await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(
                new ProviderCredentialSession(
                    store,
                    store,
                    options.CreateCredentialScope())));

        Assert.Equal(0, store.GetLegacyCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, store.DeleteLegacyCount);
        Assert.Equal("legacy-key", store.LegacyCredential);
    }

    [Fact]
    public async Task Auth_set_status_and_clear_never_print_secret()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var environment = new DictionaryEnvironmentReader();
        var handler = new AuthCommandHandler(
            environment,
            store,
            new RecordingSecretInputReader("new-secret"),
            console);

        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("set"));
        Assert.Equal("new-secret", store.StoredCredential);
        Assert.DoesNotContain("new-secret", console.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("new-secret", console.StandardError.ToString(), StringComparison.Ordinal);

        console.StandardOutput.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("status"));
        Assert.Equal(
            "API credential is configured for the current model endpoint.",
            console.StandardOutput.ToString().Trim());

        console.StandardOutput.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("clear"));
        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("clear"));
        Assert.Null(store.StoredCredential);
    }

    [Theory]
    [InlineData("\rsecret-key")]
    [InlineData("secret-key\n")]
    [InlineData("secret\tkey")]
    public async Task Auth_set_rejects_unsafe_credentials_without_success_or_storage(
        string unsafeCredential)
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var handler = new AuthCommandHandler(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader(unsafeCredential),
            console);

        var exitCode = await handler.HandleAsync("set");

        Assert.Equal(ExitCodes.Configuration, exitCode);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(store.StoredCredential);
        Assert.DoesNotContain(
            "stored securely",
            console.StandardOutput.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "contains invalid characters",
            console.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeCredential, console.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_status_and_clear_include_legacy_credential_only_for_official_xiaomi_scope()
    {
        var officialConsole = new TestConsole(isInputRedirected: false);
        var officialStore = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var officialOptions = new OpenAiCompatibleModelOptions();
        var officialHandler = new AuthCommandHandler(
            new DictionaryEnvironmentReader(),
            officialStore,
            officialStore,
            new RecordingSecretInputReader("unused"),
            officialConsole,
            officialOptions);

        Assert.Equal(ExitCodes.Success, await officialHandler.HandleAsync("status"));
        Assert.Contains(
            "API credential is configured for the current model endpoint.",
            officialConsole.StandardOutput.ToString(),
            StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Success, await officialHandler.HandleAsync("clear"));
        Assert.Null(officialStore.LegacyCredential);
        Assert.Equal(1, officialStore.DeleteLegacyCount);

        var customConsole = new TestConsole(isInputRedirected: false);
        var customStore = new RecordingCredentialStore
        {
            LegacyCredential = "legacy-key",
        };
        var customOptions = GenericOptions(
            "https://provider.example/v1",
            "PROVIDER_API_KEY");
        var customHandler = new AuthCommandHandler(
            new DictionaryEnvironmentReader(),
            customStore,
            customStore,
            new RecordingSecretInputReader("unused"),
            customConsole,
            customOptions);

        Assert.Equal(ExitCodes.Success, await customHandler.HandleAsync("status"));
        Assert.Contains(
            "API credential is not configured for the current model endpoint.",
            customConsole.StandardOutput.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, await customHandler.HandleAsync("clear"));
        Assert.Equal("legacy-key", customStore.LegacyCredential);
        Assert.Equal(0, customStore.DeleteLegacyCount);
    }

    [Fact]
    public async Task Auth_status_reports_environment_without_disclosing_metadata()
    {
        var console = new TestConsole(isInputRedirected: false);
        var environment = new DictionaryEnvironmentReader
        {
            [ProviderCredentialResolver.EnvironmentVariableName] = "environment-secret",
        };
        var handler = new AuthCommandHandler(
            environment,
            new RecordingCredentialStore { StoredCredential = "stored-secret" },
            new RecordingSecretInputReader("unused"),
            console);

        var exitCode = await handler.HandleAsync("status");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(
            "Configured API key environment variable is available.",
            console.StandardOutput.ToString().Trim());
        Assert.DoesNotContain("secret", console.StandardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public async Task Custom_environment_variable_overrides_scoped_stored_credential_without_copying_it()
    {
        var console = new TestConsole(isInputRedirected: false);
        var store = new RecordingCredentialStore();
        var options = GenericOptions("https://provider.example/v1", "PROVIDER_API_KEY");
        var scope = options.CreateCredentialScope();
        await store.SaveAsync(scope, "stored-key");
        var environment = new DictionaryEnvironmentReader
        {
            ["PROVIDER_API_KEY"] = " environment-key ",
        };
        var resolver = new ProviderCredentialResolver(
            environment,
            store,
            new RecordingSecretInputReader("prompt-key"),
            console,
            options);
        var session = new ProviderCredentialSession(store, scope);

        await resolver.ResolveForRunAsync(session);
        await session.MarkAcceptedAsync();

        Assert.Equal("environment-key", session.GetRequiredCredential());
        Assert.Equal("stored-key", await store.GetAsync(scope));
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Resolver_never_uses_a_credential_from_another_endpoint_scope()
    {
        var console = new TestConsole(input: string.Empty, isInputRedirected: true);
        var store = new RecordingCredentialStore();
        var firstOptions = GenericOptions("https://first.example/v1", "FIRST_API_KEY");
        var secondOptions = GenericOptions("https://second.example/v1", "SECOND_API_KEY");
        await store.SaveAsync(firstOptions.CreateCredentialScope(), "first-secret");
        var resolver = new ProviderCredentialResolver(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader("unused"),
            console,
            secondOptions);

        var exception = await Assert.ThrowsAsync<CredentialResolutionException>(() =>
            resolver.ResolveForRunAsync(
                new ProviderCredentialSession(store, secondOptions.CreateCredentialScope())));

        Assert.Contains("SECOND_API_KEY", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_commands_read_write_and_clear_only_the_current_scope()
    {
        var store = new RecordingCredentialStore();
        var firstOptions = GenericOptions("https://first.example/v1", "FIRST_API_KEY");
        var secondOptions = GenericOptions("https://second.example/v1", "SECOND_API_KEY");
        await store.SaveAsync(secondOptions.CreateCredentialScope(), "second-secret");
        var console = new TestConsole(isInputRedirected: false);
        var handler = new AuthCommandHandler(
            new DictionaryEnvironmentReader(),
            store,
            new RecordingSecretInputReader("first-secret"),
            console,
            firstOptions);

        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("status"));
        Assert.Equal(
            "API credential is not configured for the current model endpoint.",
            console.StandardOutput.ToString().Trim());

        console.StandardOutput.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("set"));
        Assert.Equal("first-secret", await store.GetAsync(firstOptions.CreateCredentialScope()));
        Assert.Equal("second-secret", await store.GetAsync(secondOptions.CreateCredentialScope()));

        console.StandardOutput.GetStringBuilder().Clear();
        Assert.Equal(ExitCodes.Success, await handler.HandleAsync("clear"));
        Assert.Null(await store.GetAsync(firstOptions.CreateCredentialScope()));
        Assert.Equal("second-secret", await store.GetAsync(secondOptions.CreateCredentialScope()));
    }

    [Fact]
    public async Task Auth_set_returns_cancellation_exit_code_when_hidden_input_is_cancelled()
    {
        var console = new TestConsole(isInputRedirected: false);
        var handler = new AuthCommandHandler(
            new DictionaryEnvironmentReader(),
            new RecordingCredentialStore(),
            new ThrowingSecretInputReader(new SecretInputCancelledException()),
            console);

        var exitCode = await handler.HandleAsync("set");

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Contains("Operation cancelled.", console.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_reader_uses_intercept_handles_backspace_and_does_not_echo()
    {
        var console = new TestConsole(isInputRedirected: false);
        var keys = new QueueConsoleKeyReader(
            Key('a', ConsoleKey.A),
            Key('b', ConsoleKey.B),
            Key('\0', ConsoleKey.Backspace),
            Key('c', ConsoleKey.C),
            Key('\r', ConsoleKey.Enter));
        var reader = new SystemSecretInputReader(console, keys);

        var value = await reader.ReadAsync();

        Assert.Equal("ac", value);
        Assert.All(keys.InterceptValues, value => Assert.True(value));
        Assert.DoesNotContain("ac", console.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_reader_cancels_on_ctrl_c()
    {
        var console = new TestConsole(isInputRedirected: false);
        var keys = new QueueConsoleKeyReader(
            new ConsoleKeyInfo('\u0003', ConsoleKey.C, shift: false, alt: false, control: true));
        var reader = new SystemSecretInputReader(console, keys);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Secret_reader_honors_external_cancellation_while_waiting()
    {
        var console = new TestConsole(isInputRedirected: false);
        var reader = new SystemSecretInputReader(console, new NeverAvailableKeyReader());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(cancellationSource.Token));
    }

    private static ConsoleKeyInfo Key(char value, ConsoleKey key)
    {
        return new ConsoleKeyInfo(value, key, shift: false, alt: false, control: false);
    }

    private sealed class DictionaryEnvironmentReader : IEnvironmentVariableReader
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public string? this[string name]
        {
            set => _values[name] = value;
        }

        public string? Get(string name)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    private sealed class RecordingSecretInputReader(string value) : ISecretInputReader
    {
        public int ReadCount { get; private set; }

        public Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(value);
        }
    }


    private sealed class ThrowingSecretInputReader(Exception exception) : ISecretInputReader
    {
        public Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromException<string>(exception);
        }
    }

    private static OpenAiCompatibleModelOptions GenericOptions(
        string baseUrl,
        string environmentVariable)
    {
        return new OpenAiCompatibleModelOptions
        {
            BaseUrl = baseUrl,
            Model = "generic-model",
            AuthenticationMode = OpenAiCompatibleAuthenticationMode.Bearer,
            ApiKeyEnvironmentVariable = environmentVariable,
            IncludeThinkingConfiguration = false,
        };
    }

    private sealed class RecordingCredentialStore :
        IProviderCredentialStore,
        ILegacyProviderCredentialStore
    {
        private readonly Dictionary<string, string> _scoped = new(StringComparer.Ordinal);

        public string? StoredCredential
        {
            get => _scoped.GetValueOrDefault(ProviderCredentialScope.XiaomiDefault.CanonicalValue);
            set
            {
                if (value is null)
                {
                    _scoped.Remove(ProviderCredentialScope.XiaomiDefault.CanonicalValue);
                }
                else
                {
                    _scoped[ProviderCredentialScope.XiaomiDefault.CanonicalValue] = value;
                }
            }
        }

        public int SaveCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? LegacyCredential { get; set; }

        public int GetLegacyCount { get; private set; }

        public int DeleteLegacyCount { get; private set; }

        public Task<string?> GetAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_scoped.GetValueOrDefault(scope.CanonicalValue));
        }

        public Task SaveAsync(
            ProviderCredentialScope scope,
            string credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _scoped[scope.CanonicalValue] = credential;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _scoped.Remove(scope.CanonicalValue);
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_scoped.ContainsKey(scope.CanonicalValue));
        }

        public Task<string?> GetLegacyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetLegacyCount++;
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

    private sealed class QueueConsoleKeyReader(params ConsoleKeyInfo[] keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public List<bool> InterceptValues { get; } = [];

        public bool KeyAvailable => _keys.Count > 0;

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            InterceptValues.Add(intercept);
            return _keys.Dequeue();
        }
    }

    private sealed class NeverAvailableKeyReader : IConsoleKeyReader
    {
        public bool KeyAvailable => false;

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            throw new InvalidOperationException();
        }
    }
}

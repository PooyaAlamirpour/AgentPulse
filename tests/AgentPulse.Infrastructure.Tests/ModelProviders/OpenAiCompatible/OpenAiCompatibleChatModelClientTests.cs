using System.Net;
using System.Text.Json;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleChatModelClientTests
{
    private const string Secret = "provider-test-secret-never-log";
    private const string SystemPrompt = "Private system prompt that must not leak";
    private const string UserPrompt = "Private user history that must not leak";

    [Fact]
    public async Task Default_profile_uses_bearer_authentication_and_omits_thinking_configuration()
    {
        await using var server = SuccessfulServer();
        var credential = new RecordingCredentialSession(Secret);
        var options = CreateOptions(server.BaseUri);
        var client = CreateClient(options, credential);

        var events = await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal("POST /v1/chat/completions HTTP/1.1", request.RequestLine);
        Assert.Equal($"Bearer {Secret}", request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("api-key"));
        Assert.DoesNotContain(Secret, request.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("gpt-4.1-mini", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("functions", out _));
        Assert.Equal(["Hel", "lo"], TextDeltas(events));
        Assert.Equal(1, credential.AcceptedCalls);
        Assert.Equal(0, credential.RejectedCalls);
    }

    [Fact]
    public async Task Generic_profile_uses_bearer_authentication_and_omits_thinking_configuration()
    {
        await using var server = SuccessfulServer();
        var options = CreateOptions(server.BaseUri);
        options.Model = "generic-model";
        options.ChatCompletionsPath = "custom//chat/completions/";
        options.AuthenticationMode = OpenAiCompatibleAuthenticationMode.Bearer;
        options.ApiKeyHeaderName = "Host";
        options.ApiKeyEnvironmentVariable = "GENERIC_API_KEY";
        options.IncludeThinkingConfiguration = false;
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal("POST /v1/custom/chat/completions HTTP/1.1", request.RequestLine);
        Assert.Equal($"Bearer {Secret}", request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("api-key"));
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("generic-model", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user", "assistant"], messages.Select(value => value.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Request_model_override_does_not_mutate_provider_defaults()
    {
        await using var server = SuccessfulServer();
        var options = CreateOptions(server.BaseUri);
        options.Model = "default-model";
        var client = CreateClient(options, new RecordingCredentialSession(Secret));
        var requestModel = new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, SystemPrompt),
            new ChatModelMessage(ChatModelRole.User, UserPrompt),
        ],
        "custom-model");

        await foreach (var _ in client.StreamAsync(requestModel, CancellationToken.None))
        {
        }

        var request = await server.Request;
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal("custom-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("default-model", options.Model);
    }

    [Fact]
    public async Task Custom_api_key_header_is_transmitted_without_default_or_bearer_headers()
    {
        await using var server = SuccessfulServer();
        var options = CreateOptions(server.BaseUri);
        options.AuthenticationMode = OpenAiCompatibleAuthenticationMode.ApiKeyHeader;
        options.ApiKeyHeaderName = "x-provider-key";
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal(Secret, request.Headers["x-provider-key"]);
        Assert.False(request.Headers.ContainsKey("api-key"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Ordinary_surrounding_spaces_are_normalized_after_raw_validation()
    {
        await using var server = SuccessfulServer();
        var client = CreateClient(
            CreateOptions(server.BaseUri),
            new RecordingCredentialSession("  " + Secret + "  "));

        await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal($"Bearer {Secret}", request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\rsecret-key")]
    [InlineData("secret-key\n")]
    [InlineData("bad\rkey")]
    [InlineData("bad\nkey")]
    [InlineData("bad\r\nkey")]
    [InlineData("bad\0key")]
    [InlineData("bad\u0001key")]
    [InlineData("bad\u001Fkey")]
    [InlineData("bad\u007Fkey")]
    [InlineData("bad\tkey")]
    public async Task Invalid_credentials_are_rejected_before_any_http_request(string credential)
    {
        await using var server = SuccessfulServer();
        var client = CreateClient(
            CreateOptions(server.BaseUri),
            new RecordingCredentialSession(credential));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Authentication, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.DoesNotContain("bad", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(server.HasReceivedRequest);
    }

    [Theory]
    [InlineData(400, ModelProviderErrorCode.InvalidRequest)]
    [InlineData(401, ModelProviderErrorCode.Authentication)]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied)]
    [InlineData(408, ModelProviderErrorCode.Timeout)]
    [InlineData(409, ModelProviderErrorCode.InvalidRequest)]
    [InlineData(429, ModelProviderErrorCode.RateLimited)]
    [InlineData(500, ModelProviderErrorCode.Unavailable)]
    [InlineData(502, ModelProviderErrorCode.Unavailable)]
    [InlineData(503, ModelProviderErrorCode.Unavailable)]
    public async Task Maps_http_status_to_provider_independent_error_taxonomy(
        int statusCode,
        ModelProviderErrorCode expectedCode)
    {
        await using var server = ErrorServer(
            statusCode,
            "{\"error\":{\"message\":\"provider rejected request\",\"type\":\"request_error\",\"code\":\"provider_code\"}}");
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
        Assert.Equal("provider_code", exception.ProviderErrorCode);
        Assert.Equal("request_error", exception.ProviderErrorType);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UserPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(statusCode is 401 or 403 ? 1 : 0, credential.RejectedCalls);
        Assert.False(exception.CredentialCleanupFailed);
        Assert.False(exception.CredentialCleanupTimedOut);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"rejected\"}}")]
    [InlineData("")]
    [InlineData("{broken")]
    public async Task Unauthorized_cleanup_does_not_depend_on_error_body_shape(string body)
    {
        await using var server = ErrorServer(401, body);
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Authentication, exception.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.HttpStatusCode);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.False(exception.ErrorBodyReadTimedOut);
        Assert.Equal(1, credential.RejectedCalls);
    }

    [Theory]
    [InlineData(401, ModelProviderErrorCode.Authentication)]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied)]
    public async Task Credential_cleanup_and_error_body_timeouts_preserve_provider_failure(
        int statusCode,
        ModelProviderErrorCode expectedCode)
    {
        var cleanupStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = HangingErrorServer(statusCode);
        var credential = new RecordingCredentialSession(
            Secret,
            async cancellationToken =>
            {
                cleanupStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromMilliseconds(750);
        var client = CreateClient(options, credential);

        var run = ReadAllAsync(client);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => run);

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.True(exception.CredentialCleanupFailed);
        Assert.True(exception.CredentialCleanupTimedOut);
        Assert.True(exception.ErrorBodyReadTimedOut);
        Assert.Equal(1, credential.RejectedCalls);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, ModelProviderErrorCode.Authentication, "provider-store")]
    [InlineData(401, ModelProviderErrorCode.Authentication, "io")]
    [InlineData(401, ModelProviderErrorCode.Authentication, "invalid-operation")]
    [InlineData(401, ModelProviderErrorCode.Authentication, "object-disposed")]
    [InlineData(401, ModelProviderErrorCode.Authentication, "not-supported")]
    [InlineData(401, ModelProviderErrorCode.Authentication, "argument")]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied, "invalid-operation")]
    public async Task Non_fatal_cleanup_exception_preserves_provider_failure(
        int statusCode,
        ModelProviderErrorCode expectedCode,
        string exceptionKind)
    {
        const string cleanupDetails = "credential cleanup details must stay private";
        var body = statusCode == 401 ? "{broken" : string.Empty;
        await using var server = ErrorServer(statusCode, body);
        var credential = new RecordingCredentialSession(
            Secret,
            _ => Task.FromException(CreateCleanupException(exceptionKind, cleanupDetails)));
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.True(exception.CredentialCleanupFailed);
        Assert.False(exception.CredentialCleanupTimedOut);
        Assert.False(exception.ErrorBodyReadTimedOut);
        Assert.Equal(1, credential.RejectedCalls);
        Assert.DoesNotContain(cleanupDetails, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fatal_cleanup_exception_is_not_swallowed()
    {
        await using var server = ErrorServer(401, string.Empty);
        var credential = new RecordingCredentialSession(
            Secret,
            _ => Task.FromException(new BadImageFormatException("fatal cleanup failure")));
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        await Assert.ThrowsAsync<BadImageFormatException>(() => ReadAllAsync(client));
        Assert.Equal(1, credential.RejectedCalls);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"wrapped\",\"type\":\"wrapped-type\",\"code\":\"wrapped-code\"}}", "wrapped-type", "wrapped-code")]
    [InlineData("{\"message\":\"unwrapped\",\"type\":\"plain-type\",\"code\":42}", "plain-type", "42")]
    [InlineData("plain text failure", null, null)]
    [InlineData("", null, null)]
    [InlineData("{broken", null, null)]
    public async Task Parses_only_safe_error_metadata_and_uses_a_generic_public_message(
        string body,
        string? expectedType,
        string? expectedCode)
    {
        await using var server = ErrorServer(400, body);
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal("The model provider rejected the request.", exception.Message);
        Assert.DoesNotContain("wrapped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unwrapped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plain text failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(expectedType, exception.ProviderErrorType);
        Assert.Equal(expectedCode, exception.ProviderErrorCode);
    }

    [Fact]
    public async Task Error_metadata_and_sensitive_values_are_sanitized()
    {
        var body = "{\"error\":{\"message\":\"" +
                   Secret + " " + SystemPrompt + " " + UserPrompt +
                   " Authorization: Bearer " + Secret +
                   " https://provider.example/fail?api_key=" + Secret +
                   "\",\"type\":\"type-" + Secret +
                   "\",\"code\":\"code-" + Secret + "\"}}";
        var headers = new Dictionary<string, string>
        {
            ["Retry-After"] = "9",
            ["x-request-id"] = "request-" + Secret,
            ["Content-Type"] = "text/plain",
        };
        await using var server = ErrorServer(429, body, headers);
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));
        var rendered = exception.ToString();

        Assert.Equal(TimeSpan.FromSeconds(9), exception.RetryAfter);
        Assert.Null(exception.RequestId);
        Assert.Null(exception.ProviderErrorType);
        Assert.Null(exception.ProviderErrorCode);
        Assert.Equal("The model provider rate limit was exceeded.", exception.Message);
        Assert.DoesNotContain("api_key=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(UserPrompt, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_error_body_is_bounded()
    {
        await using var server = ErrorServer(500, new string('x', 20 * 1024));
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.Message.Length < 2300);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_body_read_timeout_is_bounded_and_reports_before_first_token()
    {
        var headersSent = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteAsync(
                stream,
                "HTTP/1.1 400 Bad Request\r\n" +
                "Content-Length: 100\r\n" +
                "Connection: close\r\n\r\npartial",
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            headersSent.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromMilliseconds(750);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        var run = ReadAllAsync(client);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => run);

        Assert.Equal(ModelProviderErrorCode.InvalidRequest, exception.Code);
        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatusCode);
        Assert.True(exception.ErrorBodyReadTimedOut);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_cancellation_while_reading_error_body_is_not_mapped_to_timeout()
    {
        var headersSent = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteAsync(
                stream,
                "HTTP/1.1 400 Bad Request\r\n" +
                "Content-Length: 100\r\n" +
                "Connection: close\r\n\r\npartial",
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            headersSent.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromSeconds(5);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));
        using var cancellation = new CancellationTokenSource();

        var run = ReadAllAsync(client, cancellation.Token);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(() => run);

        Assert.Equal(ModelProviderErrorCode.Cancelled, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
    }

    [Theory]
    [InlineData(401, ModelProviderErrorCode.Authentication)]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied)]
    [InlineData(429, ModelProviderErrorCode.RateLimited)]
    [InlineData(500, ModelProviderErrorCode.Unavailable)]
    public async Task Hanging_error_body_preserves_status_mapping_and_timeout_metadata(
        int statusCode,
        ModelProviderErrorCode expectedCode)
    {
        await using var server = HangingErrorServer(statusCode);
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromMilliseconds(750);
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(options, credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.True(exception.ErrorBodyReadTimedOut);
        Assert.Equal(statusCode is 401 or 403 ? 1 : 0, credential.RejectedCalls);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stored_credential_is_deleted_before_a_hanging_unauthorized_body_is_read()
    {
        await using var server = HangingErrorServer(401);
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromMilliseconds(750);
        var store = new TrackingCredentialStore(Secret);
        var session = new ProviderCredentialSession(store, options.CreateCredentialScope());
        session.Set(Secret, ProviderCredentialSource.Stored);
        var client = CreateClient(options, session);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Authentication, exception.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.HttpStatusCode);
        Assert.True(exception.ErrorBodyReadTimedOut);
        Assert.Equal(1, store.DeleteCount);
        Assert.Null(store.Credential);
    }

    [Fact]
    public async Task Stored_credential_cleanup_completes_before_user_cancels_hanging_unauthorized_body()
    {
        var headersSent = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteAsync(
                stream,
                "HTTP/1.1 401 Unauthorized\r\n" +
                "Content-Length: 100\r\n" +
                "Connection: close\r\n\r\npartial",
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            headersSent.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromSeconds(5);
        var store = new TrackingCredentialStore(Secret);
        var session = new ProviderCredentialSession(store, options.CreateCredentialScope());
        session.Set(Secret, ProviderCredentialSource.Stored);
        var client = CreateClient(options, session);
        using var cancellation = new CancellationTokenSource();

        var run = ReadAllAsync(client, cancellation.Token);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => store.DeleteCount == 1, TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(() => run);

        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.Equal(1, store.DeleteCount);
        Assert.Null(store.Credential);
    }

    [Fact]
    public async Task Legacy_credential_cleanup_deletes_scoped_and_legacy_copies_on_unauthorized()
    {
        await using var server = ErrorServer(401, string.Empty);
        var options = CreateOptions(server.BaseUri);
        var store = new TrackingCredentialStore(Secret)
        {
            LegacyCredential = Secret,
        };
        var session = new ProviderCredentialSession(
            store,
            store,
            options.CreateCredentialScope());
        session.Set(Secret, ProviderCredentialSource.LegacyStored);
        var client = CreateClient(options, session);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Authentication, exception.Code);
        Assert.False(exception.CredentialCleanupFailed);
        Assert.Equal(1, store.DeleteCount);
        Assert.Equal(1, store.DeleteLegacyCount);
        Assert.Null(store.Credential);
        Assert.Null(store.LegacyCredential);
    }

    [Fact]
    public async Task Prompt_credential_does_not_delete_store_on_unauthorized()
    {
        await using var server = ErrorServer(401, string.Empty);
        var options = CreateOptions(server.BaseUri);
        var store = new TrackingCredentialStore();
        var session = new ProviderCredentialSession(store, options.CreateCredentialScope());
        session.Set(Secret, ProviderCredentialSource.Prompt);
        var client = CreateClient(options, session);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Authentication, exception.Code);
        Assert.False(exception.CredentialCleanupFailed);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public async Task Environment_credential_is_not_mutated_when_forbidden_body_hangs()
    {
        await using var server = HangingErrorServer(403);
        var options = CreateOptions(server.BaseUri);
        options.ErrorBodyReadTimeout = TimeSpan.FromMilliseconds(750);
        var store = new TrackingCredentialStore();
        var session = new ProviderCredentialSession(store, options.CreateCredentialScope());
        session.Set(Secret, ProviderCredentialSource.Environment);
        var client = CreateClient(options, session);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.PermissionDenied, exception.Code);
        Assert.Equal(HttpStatusCode.Forbidden, exception.HttpStatusCode);
        Assert.True(exception.ErrorBodyReadTimedOut);
        Assert.Equal(0, store.DeleteCount);
    }

    [Theory]
    [InlineData(SystemPrompt)]
    [InlineData("Private system")]
    [InlineData("Private   user history")]
    [InlineData(UserPrompt)]
    [InlineData(Secret)]
    [InlineData("Authorization: Bearer provider-test-secret-never-log")]
    [InlineData("https://provider.example/fail?token=private-value")]
    public async Task Provider_error_message_is_never_exposed_to_public_exceptions(
        string providerMessage)
    {
        var body = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = providerMessage,
                type = "invalid_request_error",
                code = "invalid_request",
            },
        });
        await using var server = ErrorServer(400, body);
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal("The model provider rejected the request.", exception.Message);
        Assert.DoesNotContain(providerMessage, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UserPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal("invalid_request_error", exception.ProviderErrorType);
        Assert.Equal("invalid_request", exception.ProviderErrorCode);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_are_not_followed_and_target_receives_no_credential(int statusCode)
    {
        await using var target = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteResponseAsync(
                stream,
                500,
                "Unexpected",
                "redirect target must not be called",
                null,
                cancellationToken);
        });
        var sensitiveLocation = new Uri(target.BaseUri, "redirect?api_key=" + Secret);
        await using var source = ErrorServer(
            statusCode,
            string.Empty,
            new Dictionary<string, string> { ["Location"] = sensitiveLocation.ToString() });
        var client = CreateClient(CreateOptions(source.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(target.HasReceivedRequest);
    }

    [Fact]
    public async Task Invalid_stream_before_first_delta_reports_before_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, "data: {broken}\n\n", cancellationToken);
        });
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
        Assert.Equal(0, credential.AcceptedCalls);
    }

    [Fact]
    public async Task Invalid_stream_after_delta_reports_after_first_token_and_preserves_delta()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel") + "data: {broken}\n\n", cancellationToken);
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), CancellationToken.None))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task First_byte_timeout_is_one_deadline_across_headers_and_first_body_byte()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("late"), cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.FirstByteTimeout = TimeSpan.FromSeconds(1);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Timeout_before_response_headers_reports_before_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.FirstByteTimeout = TimeSpan.FromMilliseconds(750);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task First_byte_timeout_reports_before_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.FirstByteTimeout = TimeSpan.FromMilliseconds(750);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Idle_timeout_after_delta_reports_after_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.StreamIdleTimeout = TimeSpan.FromMilliseconds(750);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), CancellationToken.None))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Cancellation_before_token_cancels_network_request_and_reports_stage()
    {
        var clientDisconnected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            try
            {
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, cancellationToken);
                clientDisconnected.TrySetResult(read == 0);
            }
            catch (IOException)
            {
                clientDisconnected.TrySetResult(true);
            }
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        using var cancellation = new CancellationTokenSource();
        var run = ReadAllAsync(client, cancellation.Token);
        await server.Request.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(() => run);

        Assert.Equal(ModelProviderErrorCode.Cancelled, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.True(await clientDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Cancellation_after_delta_reports_stage_and_preserves_delta()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        using var cancellation = new CancellationTokenSource();
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), cancellation.Token))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                    cancellation.Cancel();
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelProviderErrorCode.Cancelled, exception.Code);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Complete_async_sends_tool_definitions_and_parses_tool_calls()
    {
        const string responseBody =
            """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"read","arguments":"{\"path\":\"README.md\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14}}
            """;
        await using var server = new LocalModelServer((stream, request, cancellationToken) =>
            LocalModelServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                responseBody,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                cancellationToken));
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(CreateOptions(server.BaseUri), credential);
        var requestModel = new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, SystemPrompt),
            new ChatModelMessage(ChatModelRole.User, UserPrompt),
        ],
        tools:
        [
            new ChatModelToolDefinition(
                "read",
                "Read a file.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"),
        ]);

        var response = await client.CompleteAsync(requestModel, CancellationToken.None);
        var captured = await server.Request;

        using var document = JsonDocument.Parse(captured.Body);
        var root = document.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("stream_options", out _));
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        var function = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("read", function.GetProperty("name").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
        Assert.Equal(ModelFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(new ModelUsage(10, 4, 14), response.Usage);
        Assert.Equal(1, credential.AcceptedCalls);
    }

    [Fact]
    public async Task Complete_async_maps_tool_result_messages()
    {
        const string responseBody =
            """
            {"choices":[{"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}
            """;
        await using var server = new LocalModelServer((stream, request, cancellationToken) =>
            LocalModelServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                responseBody,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                cancellationToken));
        var client = CreateClient(
            CreateOptions(server.BaseUri),
            new RecordingCredentialSession(Secret));
        var requestModel = new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, SystemPrompt),
            ChatModelMessage.CreateAssistantToolCalls(
                null,
                [new ChatModelToolCall("call-1", "read", "{\"path\":\"README.md\"}", 1)]),
            ChatModelMessage.CreateToolResult("call-1", "read", "{\"success\":true,\"output\":\"ok\"}"),
        ]);

        var response = await client.CompleteAsync(requestModel, CancellationToken.None);
        var captured = await server.Request;

        using var document = JsonDocument.Parse(captured.Body);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("call-1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call-1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("read", messages[2].GetProperty("name").GetString());
        Assert.Equal("done", response.Text);
        Assert.Empty(response.ToolCalls);
    }

    private static LocalModelServer SuccessfulServer()
    {
        return new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(
                stream,
                Data("Hel") + Data("lo") + Finish("stop") + Usage(2, 3, 5) + Done(),
                cancellationToken);
        });
    }

    private static LocalModelServer HangingErrorServer(int statusCode)
    {
        return new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteAsync(
                stream,
                $"HTTP/1.1 {statusCode} {Reason(statusCode)}\r\n" +
                "Content-Length: 100\r\n" +
                "Connection: close\r\n\r\npartial",
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
    }

    private static LocalModelServer ErrorServer(
        int statusCode,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new LocalModelServer((stream, request, cancellationToken) =>
            LocalModelServer.WriteResponseAsync(
                stream,
                statusCode,
                Reason(statusCode),
                body,
                headers,
                cancellationToken));
    }

    private static OpenAiCompatibleChatModelClient CreateClient(
        OpenAiCompatibleModelOptions options,
        IProviderCredentialSession credentialSession)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAiCompatibleChatModelClient(
            new SingleHttpClientFactory(httpClient),
            options,
            new OpenAiCompatibleSseParser(),
            credentialSession);
    }

    private static OpenAiCompatibleModelOptions CreateOptions(Uri baseUri)
    {
        return new OpenAiCompatibleModelOptions
        {
            BaseUrl = new Uri(baseUri, "v1").ToString(),
            FirstByteTimeout = TimeSpan.FromSeconds(2),
            StreamIdleTimeout = TimeSpan.FromSeconds(2),
            ErrorBodyReadTimeout = TimeSpan.FromSeconds(2),
        };
    }

    private static ChatModelRequest CreateRequest()
    {
        return new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, SystemPrompt),
            new ChatModelMessage(ChatModelRole.User, UserPrompt),
            new ChatModelMessage(ChatModelRole.Assistant, "Prior assistant response"),
        ]);
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> ReadAllAsync(
        OpenAiCompatibleChatModelClient client,
        CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(), cancellationToken))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    private static string[] TextDeltas(IEnumerable<ModelStreamEvent> events)
    {
        return events.OfType<ModelStreamEvent.TextDelta>()
            .Select(value => value.Text)
            .ToArray();
    }

    private static string Data(string text) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n";

    private static string Finish(string reason) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";

    private static string Usage(long prompt, long completion, long total) =>
        $"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{total}}}}}\n\n";

    private static string Done() => "data: [DONE]\n\n";

    private static string Reason(int statusCode)
    {
        return statusCode switch
        {
            301 => "Moved Permanently",
            302 => "Found",
            303 => "See Other",
            307 => "Temporary Redirect",
            308 => "Permanent Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            408 => "Request Timeout",
            409 => "Conflict",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Error",
        };
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OpenAiCompatibleChatModelClient.HttpClientName, name);
            return client;
        }
    }

    private sealed class TrackingCredentialStore(string? credential = null)
        : IProviderCredentialStore, ILegacyProviderCredentialStore
    {
        public string? Credential { get; private set; } = credential;

        public string? LegacyCredential { get; set; }

        public int DeleteCount { get; private set; }

        public int DeleteLegacyCount { get; private set; }

        public Task<string?> GetAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Credential);
        }

        public Task SaveAsync(
            ProviderCredentialScope scope,
            string credentialValue,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Credential = credentialValue;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Credential = null;
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            ProviderCredentialScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Credential is not null);
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

    private static Exception CreateCleanupException(string kind, string message)
    {
        return kind switch
        {
            "provider-store" => new ProviderCredentialStoreException(message),
            "io" => new IOException(message),
            "invalid-operation" => new InvalidOperationException(message),
            "object-disposed" => new ObjectDisposedException("credential-store", message),
            "not-supported" => new NotSupportedException(message),
            "argument" => new ArgumentException(message),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown test exception kind."),
        };
    }

    private sealed class RecordingCredentialSession(
        string credential,
        Func<CancellationToken, Task>? rejection = null)
        : IProviderCredentialSession
    {
        public int AcceptedCalls { get; private set; }

        public int RejectedCalls { get; private set; }

        public void Set(string value, ProviderCredentialSource source) =>
            throw new NotSupportedException();

        public string GetRequiredCredential() => credential;

        public Task MarkAcceptedAsync(CancellationToken cancellationToken = default)
        {
            AcceptedCalls++;
            return Task.CompletedTask;
        }

        public Task MarkAuthenticationRejectedAsync(
            CancellationToken cancellationToken = default)
        {
            RejectedCalls++;
            return rejection?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}

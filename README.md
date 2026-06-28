# AgentPulse

**AgentPulse** is an open-source, cross-platform .NET 8 command-line assistant with project-aware prompts, persistent conversations, real-time model streaming, secure endpoint-scoped credentials, Git-aware context, and recovery-safe session state.

The implementation is developed through a 10-phase roadmap. **Phase 9 implementation is complete**: the CLI now has a documented Node compatibility matrix, centralized exit-code and error rendering, a pipe-safe stdout/stderr contract, hardened interactive and non-interactive input, configurable stderr-only logging, secret redaction, and real-process coverage for failure, cancellation, partial persistence, and crash recovery. Native verification for this revision remains pending on Windows, Linux, and macOS.

> **Development status:** Roadmap implementation complete — **10 of 10 phases completed**
> **Current milestone:** Phase 9 — CLI Hardening and Compatibility Verification
> **Default provider profile:** Xiaomi MiMo, model `mimo-v2.5-pro`

> [!WARNING]
> Model API calls may incur usage charges. Review the configured provider's current pricing, account limits, and data-handling terms before running prompts or opt-in live tests.

---

## Current Capabilities

### CLI

- `agentpulse --help`
- `agentpulse run "prompt"`
- `agentpulse run --dir <path> "prompt"`
- `agentpulse run --model <model> "prompt"`
- `agentpulse run --session <id> "prompt"`
- Prompt input from a positional argument or redirected `stdin`; a positional prompt takes precedence
- Git-aware canonical project get-or-create without changing the process working directory; repository subdirectories resolve to the same project
- New sessions by default, or ordered completed-history continuation through `--session`
- One database-backed active run per session across processes
- Request-level `--model` override without mutating global configuration
- Immediate streaming of model text to `stdout` with periodic partial persistence
- Session metadata, errors, and credential prompts on `stderr`; successful runs print `Session ID: <id>` after streaming completes
- A single final newline after successful completion
- `Ctrl+C` cancellation with exit code `130`, partial-response preservation, and owned-lease release
- Stable exit codes for usage (`2`), configuration/credential (`3`), session (`4`), provider (`5`), timeout (`124`), cancellation (`130`), and unexpected failure (`1`)
- Standard .NET logging configured through appsettings or environment variables, always rendered on `stderr` with ANSI disabled
- Central safe error rendering with no raw provider body, prompt, history, authorization data, or credential disclosure
- Endpoint-scoped credential commands:
  - `agentpulse auth set`
  - `agentpulse auth status`
  - `agentpulse auth clear`

### OpenAI-Compatible Model Transport

- A single runtime `OpenAiCompatibleChatModelClient` implementation of `IChatModelClient`
- Xiaomi MiMo supplied through the default configuration profile; no provider registry or provider-selection flag
- Configurable absolute base URL, relative Chat Completions path, model, authentication, completion-token limit, thinking extension, and streaming timeouts
- OpenAI-compatible `POST` request with `stream: true` and `stream_options.include_usage: true`
- `IHttpClientFactory`, `HttpCompletionOption.ResponseHeadersRead`, and an infinite `HttpClient.Timeout`
- Automatic HTTP redirects disabled to prevent forwarding credentials to another target
- HTTPS required for remote hosts; HTTP accepted only for loopback test or development endpoints
- Incremental SSE parsing for fragmented frames, fragmented UTF-8, LF/CRLF, comments, keep-alive events, multi-line `data:`, `[DONE]`, deltas, finish reason, and usage
- A single first-byte deadline spanning request start, response headers, and the first body byte
- Distinct stream-idle and bounded error-body read timeouts
- Cancellation propagated through `SendAsync`, response-stream reads, and SSE enumeration
- Provider-independent error taxonomy and `BeforeFirstToken` / `AfterFirstToken` failure stages
- No automatic retry for chargeable streaming requests
- No tools, function calls, plugins, web search, attachments, or reasoning-content persistence

### Default Xiaomi MiMo Profile

```json
{
  "AgentPulse": {
    "Model": {
      "BaseUrl": "https://api.xiaomimimo.com/v1",
      "ChatCompletionsPath": "chat/completions",
      "Model": "mimo-v2.5-pro",
      "AuthenticationMode": "ApiKeyHeader",
      "ApiKeyHeaderName": "api-key",
      "ApiKeyEnvironmentVariable": "MIMO_API_KEY",
      "MaxCompletionTokens": 4096,
      "ThinkingMode": "disabled",
      "IncludeThinkingConfiguration": true,
      "FirstByteTimeout": "00:00:30",
      "StreamIdleTimeout": "00:01:00",
      "ErrorBodyReadTimeout": "00:00:10"
    }
  }
}
```

The Xiaomi profile sends:

```http
api-key: <API_KEY>
```

and preserves the existing request extension:

```json
{
  "thinking": {
    "type": "disabled"
  }
}
```

### Generic OpenAI-Compatible Endpoint

A custom endpoint is selected only through configuration. No new command is required:

```json
{
  "AgentPulse": {
    "Model": {
      "BaseUrl": "https://provider.example/v1",
      "ChatCompletionsPath": "chat/completions",
      "Model": "provider-model",
      "AuthenticationMode": "Bearer",
      "ApiKeyHeaderName": "api-key",
      "ApiKeyEnvironmentVariable": "PROVIDER_API_KEY",
      "MaxCompletionTokens": 4096,
      "ThinkingMode": "disabled",
      "IncludeThinkingConfiguration": false,
      "FirstByteTimeout": "00:00:30",
      "StreamIdleTimeout": "00:01:00",
      "ErrorBodyReadTimeout": "00:00:10"
    }
  }
}
```

PowerShell environment-variable equivalent:

```powershell
$env:AgentPulse__Model__BaseUrl = "https://provider.example/v1"
$env:AgentPulse__Model__ChatCompletionsPath = "chat/completions"
$env:AgentPulse__Model__Model = "provider-model"
$env:AgentPulse__Model__AuthenticationMode = "Bearer"
$env:AgentPulse__Model__ApiKeyEnvironmentVariable = "PROVIDER_API_KEY"
$env:AgentPulse__Model__IncludeThinkingConfiguration = "false"
$env:AgentPulse__Model__ErrorBodyReadTimeout = "00:00:10"
$env:PROVIDER_API_KEY = "..."
```

Supported authentication modes are:

- `Bearer` → `Authorization: Bearer <API_KEY>`
- `ApiKeyHeader` → `<ApiKeyHeaderName>: <API_KEY>`

Sensitive or transport-controlled header names such as `Host`, `Content-Length`, `Transfer-Encoding`, `Connection`, `Upgrade`, proxy authentication headers, cookie headers, `Content-Type`, and `Authorization` are rejected for `ApiKeyHeader` mode. `ApiKeyHeaderName` is ignored in `Bearer` mode. Every raw API credential is validated before normalization: CR, LF, NUL, tab, DEL, and all other control characters are rejected wherever they occur. Only ordinary ASCII spaces at the beginning or end may be removed after validation; general whitespace trimming is never used.

### Configuration Precedence

Non-secret model configuration uses the standard order below, from lowest to highest priority:

1. Default values in code
2. `appsettings.json`
3. `appsettings.{Environment}.json`
4. Environment variables

Environment variables therefore override JSON. The CLI project copies both `appsettings.json` and any existing `appsettings.*.json` files to Build and Publish output with `PreserveNewest`; environment-specific files remain optional. Supported model keys include:

```text
AgentPulse__Model__BaseUrl
AgentPulse__Model__ChatCompletionsPath
AgentPulse__Model__Model
AgentPulse__Model__AuthenticationMode
AgentPulse__Model__ApiKeyHeaderName
AgentPulse__Model__ApiKeyEnvironmentVariable
AgentPulse__Model__MaxCompletionTokens
AgentPulse__Model__ThinkingMode
AgentPulse__Model__IncludeThinkingConfiguration
AgentPulse__Model__FirstByteTimeout
AgentPulse__Model__StreamIdleTimeout
AgentPulse__Model__ErrorBodyReadTimeout
```

The actual API key is deliberately not a bindable option. `OpenAiCompatibleModelOptions` has no `ApiKey` property, and an `ApiKey` value placed in JSON is ignored.

### Secure, Endpoint-Scoped Credentials

Credential resolution for `run` uses this order:

1. The environment variable named by `AgentPulse:Model:ApiKeyEnvironmentVariable`
2. The securely stored credential for the current endpoint scope
3. A hidden interactive prompt

An environment credential is never copied into the credential store. A prompted credential is stored only after a successful streamed provider response begins. A valid scoped stored credential is reused without being rewritten after each successful run. As soon as a `401` or `403` response header is received, the rejected stored credential is invalidated for the current endpoint scope before the error body is read; cleanup therefore still occurs when that body is malformed, incomplete, or times out. Environment credentials are never modified.

A credential scope is derived from non-secret endpoint identity:

```text
normalized scheme + normalized host + effective port + authentication mode + API-key header name when applicable
```

The model and base-URL path are intentionally excluded because one key commonly covers multiple models and API paths on the same provider origin. Scheme, port, authentication mode, and API-key header differences create different scopes. Host casing and default ports are normalized.

Changing `BaseUrl` to another host does **not** make the previous credential available to that host. Scoped file names use a SHA-256 digest of the non-secret scope; neither the key nor a key-derived hash appears in the file name or metadata. Credential contents remain protected by ASP.NET Core Data Protection.

A legacy unscoped Phase 6 credential is considered only for the official Xiaomi endpoint. After that credential is accepted successfully by Xiaomi, it is migrated exactly once into the scoped format and the legacy file is removed only after the scoped save succeeds. It remains untouched after a failed provider run and is never migrated or used for a custom host.

CLI help and command descriptions are provider-neutral; Xiaomi appears only as the default configuration profile and optional live-test target.

The `auth` commands always operate on the current configuration scope:

- `auth set` stores or replaces only the current scope
- `auth status` reports only the current scope or configured environment-variable availability
- `auth clear` removes only the current scope and does not change environment variables

No command prints any key fragment, key length, key hash, or complete scope value.

The protected credential root is under the current user's logical local application-data directory:

```text
<LocalApplicationData>/AgentPulse/security/
```

The exact operating-system path is derived at runtime. Credentials are never stored in SQLite, JSON configuration, the repository, Git configuration, or command-line arguments.

### Endpoint and Redirect Security

- `BaseUrl` must be absolute.
- `ChatCompletionsPath` must be relative and cannot contain another scheme, host, query, fragment, backslash, traversal segment, encoded separator, encoded NUL, or single/double-encoded traversal sequence.
- Base URL and relative path are combined without changing origin.
- Remote HTTP endpoints are rejected; loopback HTTP remains available for local contract tests and development.
- `301`, `302`, `303`, `307`, and `308` are converted to provider errors instead of being followed.
- Redirect locations and provider error URLs are sanitized by removing user information, query strings, and fragments.
- Error bodies are read only up to a bounded limit and within `ErrorBodyReadTimeout` (default `00:00:10`). If reading times out, the already-known HTTP status, provider-independent error kind, retry metadata, and `BeforeFirstToken` stage are preserved while `ErrorBodyReadTimedOut` is set; user cancellation remains distinct.
- `FirstByteTimeout` is one deadline from `SendAsync` start through response headers and the first body byte; it is not restarted after headers.
- Public provider exceptions use fixed messages selected from the error taxonomy. Raw provider messages and complete error bodies are not retained or printed, so echoed API keys, request bodies, system prompts, and conversation history cannot reach normal CLI output. Only bounded, token-safe provider type/code, request ID, retry metadata, status, stage, and timeout state may be retained.

### Persistence and Recovery

- Default runtime database path: `<LocalApplicationData>/AgentPulse/data/agentpulse.db`
- Stable user-scoped storage shared by Debug, Release, and published executions
- `AgentPulse__Persistence__DatabasePath` override support
- Design-time migrations use a separate temporary database and never open the user's runtime database by default
- Project, Session, Message, MessagePart, and RunLease domain models
- Entity Framework Core with SQLite and migrations
- User and streaming assistant records committed before provider execution
- Ordered previous history with the current prompt included exactly once
- Immediate delta rendering and exact ordered text accumulation
- Configurable partial flush interval and character threshold
- Final flush on success, cancellation, or failure
- Partial text preserved after a failure or cancellation following the first token
- Failure stage tracked during the current HTTP/SSE run rather than inferred from persisted text
- Session returned to `Idle` on every finalized path
- Independent periodic lease renewal during long streams

### Project Context

- Absolute and relative path resolution
- Current-directory fallback and path normalization
- Git executable, repository root, and worktree discovery
- Stable deterministic project identifiers
- Separate identifiers for distinct worktrees
- Non-Git directory support
- Testable platform, clock, filesystem, Git, and process abstractions

### Architecture and Quality

- Clean Architecture with one-way dependencies
- Domain isolated from HTTP, provider details, SSE, console, credentials, and EF Core
- Application owns provider-independent request, event, failure-stage, and streaming orchestration contracts
- Infrastructure owns the single OpenAI-compatible transport, SSE parser, secure credentials, EF Core, and SQLite
- CLI owns hidden input, configuration composition, console rendering, commands, and exit codes
- Nullable reference types enabled and warnings treated as errors
- Deterministic tests use local HTTP servers and per-test temporary credential/database roots; Host tests do not create or read the real user-scoped AgentPulse data directories
- Normal tests require neither internet access nor an API key

---

## Technology Stack

| Area | Technology |
|---|---|
| Runtime | .NET 8 |
| Language | C# 12 |
| Architecture | Clean Architecture |
| Hosting and DI | .NET Generic Host and Microsoft.Extensions.DependencyInjection |
| HTTP | `HttpClient` and `IHttpClientFactory` |
| Secret protection | ASP.NET Core Data Protection |
| Persistence | Entity Framework Core 8 |
| Database | SQLite |
| Testing | xUnit |
| Version-control discovery | Git CLI |

---

## Architecture

```mermaid
flowchart LR
    CLI[AgentPulse.Cli] --> APP[AgentPulse.Application]
    CLI --> INFRA[AgentPulse.Infrastructure]
    APP --> DOMAIN[AgentPulse.Domain]
    INFRA --> APP
    INFRA --> DOMAIN
```

- `AgentPulse.Domain` has no dependency on other project layers.
- `AgentPulse.Application` depends only on Domain.
- `AgentPulse.Infrastructure` implements Application ports.
- `AgentPulse.Cli` is the Composition Root.
- Application contracts do not expose provider DTOs or API-key handling.
- Runtime resolves exactly one `IChatModelClient`: `OpenAiCompatibleChatModelClient`.

```text
src/
  AgentPulse.Domain
  AgentPulse.Application
  AgentPulse.Infrastructure
  AgentPulse.Cli

tests/
  AgentPulse.Domain.Tests
  AgentPulse.Application.Tests
  AgentPulse.Infrastructure.Tests
  AgentPulse.Cli.IntegrationTests
```

---

## Build and Test

Prerequisites:

- .NET 8 SDK
- Git, recommended for project-context features

```bash
dotnet restore
dotnet build --no-restore -warnaserror
dotnet test --no-build
```

Normal tests use deterministic local HTTP servers and do not call an external provider.

### Optional Live Xiaomi Test

The live test reads only environment variables and never reads the stored credential. It runs only when both `MIMO_API_KEY` is present and `AGENTPULSE_RUN_LIVE_TESTS` is exactly `1`.

PowerShell:

```powershell
$env:MIMO_API_KEY = "..."
$env:AGENTPULSE_RUN_LIVE_TESTS = "1"
dotnet test --no-build --filter "Category=LiveXiaomi"
```

Bash:

```bash
MIMO_API_KEY="..." AGENTPULSE_RUN_LIVE_TESTS="1" \
  dotnet test --no-build --filter "Category=LiveXiaomi"
```

---

## Running AgentPulse

### Prompt Runs

```bash
dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
dotnet run --project src/AgentPulse.Cli -- run --dir <path> "Explain this project"
dotnet run --project src/AgentPulse.Cli -- run --model <model> "Explain this project"
dotnet run --project src/AgentPulse.Cli -- run --session <id> "Continue the conversation"
echo "Explain this project" | dotnet run --project src/AgentPulse.Cli -- run
```

A run without `--session` creates a new session and prints `Session ID: <id>` to `stderr` after the streamed response finishes. Reuse that ID with `--session` to continue the completed message history. Running from any subdirectory of the same Git worktree resolves to the same canonical project, so continuation works from the repository root or its descendants. Model text and its final newline are the only content written to `stdout`; session metadata and errors stay on `stderr`, so redirecting `stdout` produces a clean response file. Only one run may be active in a session at a time. `Ctrl+C` or a failure preserves the partial assistant response, finalizes the session when possible, and releases the owned lease. JSON event output, attach, fork, and tool calling are not supported in this phase.

When no credential exists for the current endpoint, the CLI requests it with hidden input. After a successful provider response, it is protected for that endpoint scope.

### Default Xiaomi Environment Variable

PowerShell:

```powershell
$env:MIMO_API_KEY = "..."
dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
```

Bash:

```bash
MIMO_API_KEY="..." dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
```

### Redirected Standard Input

When no positional prompt is supplied, redirected `stdin` is read in full and only trailing pipe line endings are removed. A positional prompt takes precedence and prevents reading `stdin`. A redirected process cannot securely read a missing credential from the same stream, so configure the current endpoint with `auth set` or its configured API-key environment variable first.

### Credential Commands

```bash
dotnet run --project src/AgentPulse.Cli -- auth set
dotnet run --project src/AgentPulse.Cli -- auth status
dotnet run --project src/AgentPulse.Cli -- auth clear
```

---

## Roadmap

| Phase | Status | Title | Key Capabilities |
|---:|:---:|---|---|
| 0 | ✅ | Behavioral Baseline | Scope, observable behavior, architecture mapping, decisions |
| 1 | ✅ | Solution and CLI Foundation | Generic Host, DI, CLI input, `stdin`, cancellation |
| 2 | ✅ | Domain and Persistence | Entities, SQLite, migrations, repositories, transactions |
| 3 | ✅ | Project Context | Paths, Git discovery, worktrees, deterministic project IDs |
| 4 | ✅ | Session and Message Lifecycle | Ordered history, run lease, recovery, transaction boundaries |
| 5 | ✅ | Model Request Construction | Provider-independent messages, history, project system context |
| 6 | ✅ | Real Xiaomi Streaming and Secure Credentials | Real HTTP streaming, SSE, hidden credentials, partial persistence, full vertical flow |
| 7 | ✅ | OpenAI-Compatible Provider Generalization and Hardening | Generic transport, endpoint scope, redirect defense, error taxonomy, failure stages |
| 8 | ✅ | Final Vertical Prompt Flow and Session Reliability | Project/session resolution, cross-process lease, history, model override, stdin, streaming checkpoints, terminal state handling |
| 9 | 🟡 | CLI Hardening and Compatibility Verification | Implementation complete; native verification for this revision remains pending on Windows, Linux, and macOS |

The Phase 0 through Phase 9 implementation roadmap is complete. Native Windows, Linux, and macOS verification must be rerun for this revision before Phase 9 is described as fully cross-platform verified. Product capabilities explicitly excluded from these phases remain out of scope.

---

## Phase 9 Test Coverage

Phase 9 adds or preserves deterministic coverage for:

- Positional prompt parsing, redirected multiline Unicode `stdin`, option ordering, duplicate/unknown options, missing values, and validation failures
- Relative, absolute, missing, file, and space-containing project directories plus duplicate-safe project get-or-create
- New-session execution and explicit existing-session continuation with stable, session-scoped completed history
- Request-level model override with unchanged provider defaults on the next run
- Cross-process-capable SQLite session leases, immediate busy rejection, owner-aware idempotent release, and reuse after success, failure, or cancellation
- User and assistant placeholder persistence before the provider call
- Immediate ordered console deltas and time-or-character checkpoint persistence without duplicate tokens
- Completed assistant metadata, including effective model, finish reason, and usage
- Cancellation before or during streaming and during final persistence, with partial response recovery and exit code `130` at the CLI boundary
- Provider failure after partial output with sanitized error metadata and no raw provider body, prompt, history, or credential disclosure
- Checkpoint persistence failure with failed terminal state where possible and unconditional owned-lease release
- Real SQLite repositories and composition with a test-only controllable `IChatModelClient`; no fake provider is registered at runtime
- Explicitly opt-in live Xiaomi connectivity only
- Real-process exit-code coverage for parser, directory, session, configuration, credential, provider, timeout, cancellation, and unexpected failure categories
- Separate stdout/stderr capture, logging-level verification, and secret-marker redaction across output and persisted failure metadata
- Process termination after a persisted checkpoint followed by expired-lease recovery and a successful continuation

The complete behavioral decisions are recorded in [`docs/cli-compatibility.md`](docs/cli-compatibility.md). Local build, configuration, stdin, session, logging, and Ctrl+C instructions are in [`docs/local-cli.md`](docs/local-cli.md).

---

## Engineering Principles

- Small, reviewable phases
- Provider-independent application contracts
- Infrastructure behind explicit ports
- No secrets in logs, errors, telemetry, test output, or repository files
- No automatic retry of chargeable streaming requests
- Exact ordered persistence of streamed text
- UTC-only stored timestamps
- Cancellation on all asynchronous boundaries
- Database changes only through migrations
- No premature registry, discovery, tools, plugins, agent loops, or source editing

---

## Project Status

```text
Implementation completed: Phase 0 through Phase 9
Native verification for this revision: Windows, Linux, and macOS pending execution
```

---

## Contributing

For bug reports and feature requests, please open a GitHub issue. Pull requests are welcome.

For collaboration or direct coordination, contact [@Alamirpour](https://t.me/Alamirpour) on Telegram.

## Maintainer

AgentPulse is developed and maintained by **Pooya Alamirpour**.

Found an issue or interested in contributing or collaborating? Contact me on Telegram: [@Alamirpour](https://t.me/Alamirpour)

## License

AgentPulse is licensed under the [MIT License](LICENSE).

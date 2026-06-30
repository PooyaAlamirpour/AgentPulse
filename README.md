# AgentPulse

AgentPulse is a .NET 8 command-line coding agent with persistent sessions, an extensible tool registry, a bounded agent loop, and an OpenAI-compatible model adapter. The current implementation can inspect a selected workspace through read-only tools and continue a model conversation automatically until the model returns a final answer.

> Model requests may incur provider charges. Review the configured provider's pricing, limits, and data-handling terms before use.

## Current Status

Implemented in the current codebase:

- Project-aware CLI execution with `--dir`
- Persistent projects, sessions, messages, message parts, and run leases in SQLite
- Provider-agnostic conversation models for system, user, assistant, tool-call, and tool-result messages
- OpenAI-compatible text completions and function/tool calling
- Central tool registry populated through dependency injection
- Bounded agent loop with deterministic sequential execution of multiple tool calls
- Persistent assistant tool-call messages, tool-result messages, and final assistant responses
- Read-only `read`, `glob`, and `grep` tools restricted to the active workspace
- Deterministic `Allow`, `Ask`, and `Deny` permission rules with once, session, and project approvals
- Structured logging, cancellation propagation, tool timeouts, and stable CLI error mapping
- Deterministic unit and integration tests that do not require a live model provider

## Requirements

- .NET 8 SDK
- A compatible Chat Completions endpoint
- An API credential supplied through the configured environment variable or the credential command

## CLI Usage

Restore and build the solution:

```bash
dotnet restore AgentPulse.sln
dotnet build AgentPulse.sln --no-restore -warnaserror
```

Run the agent in the current directory:

```bash
dotnet run --project src/AgentPulse.Cli -- run "Summarize this project"
```

Run against another workspace:

```bash
dotnet run --project src/AgentPulse.Cli -- run --dir "/path/to/workspace" "Find the main entry point"
```

Continue an existing session:

```bash
dotnet run --project src/AgentPulse.Cli -- run --session <session-id> "Continue the analysis"
```

Override the configured model for one request:

```bash
dotnet run --project src/AgentPulse.Cli -- run --model <model-name> "Inspect the solution"
```

The prompt can also be supplied through redirected standard input. `Ctrl+C` cancels the active provider/tool operation and the CLI returns exit code `130`.

## Model Configuration

The default profile is OpenAI-compatible and uses `OPENAI_API_KEY`:

```json
{
  "AgentPulse": {
    "Model": {
      "BaseUrl": "https://api.openai.com/v1",
      "ChatCompletionsPath": "chat/completions",
      "Model": "gpt-4.1-mini",
      "AuthenticationMode": "Bearer",
      "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
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

Configuration can be overridden through `appsettings.json`, environment-specific settings, or environment variables. Example:

```bash
export AgentPulse__Model__BaseUrl="https://provider.example/v1"
export AgentPulse__Model__Model="provider-model"
export AgentPulse__Model__ApiKeyEnvironmentVariable="PROVIDER_API_KEY"
export PROVIDER_API_KEY="..."
```

### Xiaomi MiMo streaming provider

AgentPulse also retains the existing Xiaomi MiMo streaming provider. It uses `https://api.xiaomimimo.com/v1`, model `mimo-v2.5-pro`, the `api-key` authentication header, and the `MIMO_API_KEY` environment variable. Set the credential before using that provider:

```bash
export MIMO_API_KEY="..."
```

The Xiaomi provider currently supports the existing streaming path. The bounded Tool Calling/Agent Loop path uses the configured provider that supports non-streaming `CompleteAsync` tool calls; Xiaomi is not advertised as Tool Calling-capable in this phase.

Credential commands operate on the currently configured endpoint scope:

```bash
dotnet run --project src/AgentPulse.Cli -- auth set
dotnet run --project src/AgentPulse.Cli -- auth status
dotnet run --project src/AgentPulse.Cli -- auth clear
```

Secrets are not stored in project configuration or SQLite.

## Tool Calling and Agent Loop

For each CLI run, AgentPulse performs the following bounded loop:

1. Builds the system, history, and current user messages.
2. Sends the active tool definitions with the model request.
3. Persists an assistant response containing any returned tool calls.
4. Resolves each tool by name through the registry.
5. Validates and executes tool arguments asynchronously.
6. Persists one tool-result message for each call, linked by its stable tool-call identifier.
7. Sends the expanded conversation back to the model.
8. Persists and returns the final assistant text when no further tool call is requested.

A model response may contain multiple tool calls. They are executed sequentially in their declared order so the stored history and returned tool results remain deterministic. Unknown tools, malformed arguments, validation failures, timeouts, and tool exceptions become structured failed tool results so the model can recover in a later iteration.

`MaxToolIterations` prevents an unbounded conversation. Reaching the configured limit ends the run with a specific application error rather than continuing indefinitely.

AgentPulse stops repeated deterministic tool failures early instead of consuming the full tool-iteration limit.

## Built-in Tools

All built-in tools are **read-only**. Every path is normalized against the active workspace. Parent traversal, absolute paths outside the workspace, separator variations, and existing symbolic-link or junction escapes are rejected. `.git`, `bin`, and `obj` directories are skipped during workspace scans.

### Read

Tool name: `read`

Reads a text file with line numbers.

Parameters:

- `path` — required workspace-relative file path
- `offset` — optional one-based starting line
- `limit` — optional maximum number of lines

The tool validates files and directories separately, limits readable bytes and lines, caps total output, and emits a truncation notice when more content remains.

### Glob

Tool name: `glob`

Finds files by a cross-platform glob pattern such as `**/*.cs`.

Parameters:

- `pattern` — required glob pattern
- `basePath` — optional workspace-relative starting path
- `maxResults` — optional result limit capped by configuration

Results are unique, sorted deterministically, and limited before they are returned to the model.

### Grep

Tool name: `grep`

Searches text files using a regular expression.

Parameters:

- `pattern` — required regular expression
- `basePath` — optional workspace-relative starting path
- `include` — optional glob filter such as `**/*.cs`
- `caseSensitive` — optional case-sensitivity flag; default is `false`
- `maxResults` — optional result limit capped by configuration

Each result contains the workspace-relative file path, line number, and matching line. Binary files and files above the configured scan size are skipped. Invalid or excessively expensive regular expressions return structured errors.

## Tool Configuration

Tool settings live under `AgentPulse:Tools`:

```json
{
  "AgentPulse": {
    "Tools": {
      "MaxToolIterations": 8,
      "MaxOutputCharacters": 30000,
      "MaxGlobResults": 200,
      "MaxGrepResults": 100,
      "MaxReadLines": 500,
      "MaxReadableFileBytes": 5242880,
      "MaxGrepFileBytes": 2097152,
      "ToolTimeout": "00:00:30"
    }
  }
}
```

Environment-variable examples:

```bash
export AgentPulse__Tools__MaxToolIterations=8
export AgentPulse__Tools__MaxReadLines=500
export AgentPulse__Tools__MaxGlobResults=200
export AgentPulse__Tools__MaxGrepResults=100
```

`MaxOutputCharacters` is also enforced centrally by the agent loop, even when a tool applies a smaller internal limit.

## Permission System

AgentPulse evaluates `Allow`, `Ask`, and `Deny` rules immediately before a classified tool executes. Tool arguments and workspace paths are validated first, so an `Allow` decision never bypasses workspace boundaries, symbolic-link or junction protection, or existing read limits. Tools without explicit permission metadata are denied by default with a failed tool result instead of executing.

`glob` and `grep` use two permission boundaries. The request-level check decides whether the search may start, and a resource-level check evaluates every canonical, workspace-relative candidate path before it is returned or, for `grep`, before its content is opened or read. Denied paths are excluded completely. Paths that require approval are included only after a compatible approval; an indeterminate resource decision fails closed.

The current classified read-only tools remain backward compatible: when no explicit rule matches, `read`, `glob`, and `grep` use `DefaultDecision`, which is `Allow` by default inside the active workspace. A matching `Ask` rule opens an interactive approval prompt. The rule `Scope` is the maximum approval lifetime:

- `Once`: `Allow once` or `Deny`
- `Session`: `Allow once`, `Allow for this session`, or `Deny`
- `Project`: all options, including `Always allow for this project`

Scope is enforced in the permission core as well as the CLI. A prompt implementation cannot persist an approval that exceeds the matching rule scope. Session approvals are held only in memory for the matching session. Project approvals are stored atomically in the AgentPulse application-data directory and are isolated by deterministic project identity. `Scope` does not affect `Allow` or `Deny` rules; it is used only to bound approvals for `Ask` rules.

A non-interactive run never waits for approval. An `Ask` decision becomes a failed tool result with the message `Permission approval is required, but the current run is non-interactive.` Explicit `Deny` always has priority and cannot be overridden by session or project approvals. Persisted approvals that are broader than the current rule scope are treated as stale and ignored.

Permission settings live under `AgentPulse:Permissions`:

```json
{
  "AgentPulse": {
    "Permissions": {
      "DefaultDecision": "Allow",
      "Rules": [
        {
          "Tool": "grep",
          "Operation": "search",
          "Target": "restricted/one-time/**",
          "Decision": "Ask",
          "Scope": "Once"
        },
        {
          "Tool": "grep",
          "Operation": "search",
          "Target": "restricted/session/**",
          "Decision": "Ask",
          "Scope": "Session"
        },
        {
          "Tool": "glob",
          "Operation": "search",
          "Target": "restricted/project/**",
          "Decision": "Ask",
          "Scope": "Project"
        },
        {
          "Tool": "*",
          "Operation": "*",
          "Target": "secrets/**",
          "Decision": "Deny",
          "Scope": "Project"
        }
      ]
    }
  }
}
```

Rules support exact tool and operation names or `*`. Target matching supports exact workspace-relative paths plus limited `*` and `**` wildcards. Parent traversal, rooted targets, unsupported wildcard syntax, empty selectors, invalid scopes, and duplicate rules are rejected during startup. Resolution is deterministic: more specific tool and target matches win, and equally specific rules resolve in the safer order `Deny`, `Ask`, then `Allow`. Omitting `Scope` preserves the existing `Project` default.

## Adding a Tool

A tool is independent of the CLI and model-provider SDK. Implement `IAgentTool` and register it with dependency injection:

```csharp
using System.Text.Json;
using AgentPulse.Application.AgentTools;

public sealed class ListLanguagesTool : IAgentTool
{
    public string Name => "list_languages";

    public string Description => "List language names detected in the workspace.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{},"additionalProperties":false}
        """;

    public Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AgentToolResult.Success("C#, SQL"));
    }
}
```

Register it alongside the built-in tools:

```csharp
services.AddSingleton<IAgentTool, ListLanguagesTool>();
```

The registry discovers all registered `IAgentTool` instances, rejects duplicate ordinal names, and exposes their provider-agnostic definitions to the model adapter.

## Persistence

The default SQLite database is stored under the current user's local application-data directory. Override it with:

```bash
export AgentPulse__Persistence__DatabasePath="/path/to/agentpulse.db"
```

A completed tool-enabled history can be reconstructed in sequence as:

- user message
- assistant message with one or more tool-call parts
- one tool message per tool result
- additional tool turns when requested
- final assistant message

Tool results are never stored as user or assistant text messages.

## Architecture

The solution follows one-way Clean Architecture dependencies:

- `AgentPulse.Domain` — persistent conversation and session entities
- `AgentPulse.Application` — provider-independent model contracts, tool contracts, registry, agent loop, and use cases
- `AgentPulse.Infrastructure` — OpenAI-compatible adapter, SQLite persistence, workspace security, and built-in tools
- `AgentPulse.Cli` — command parsing, configuration, credential input, output, and exit codes

Provider DTOs remain inside Infrastructure. Domain and Application models do not reference an external provider SDK.

## Build and Test

Build the complete solution with warnings treated as errors:

```bash
dotnet restore AgentPulse.sln
dotnet build AgentPulse.sln --no-restore -warnaserror
```

Run all tests:

```bash
dotnet test AgentPulse.sln --no-build
```

The regular test suite uses fake model clients, local HTTP servers, temporary workspaces, and temporary SQLite databases. It does not require internet access or a real API credential.

## Current Limitations

The following capabilities are not implemented in the current phase:

- Write, edit, patch, shell, or Bash tools
- Plan and build modes
- Subagents
- Long-term memory, compaction, or context pruning
- Full-screen TUI
- MCP or LSP integration
- Plugin system
- Server mode
- GitHub integration
- Voice or image input
- Session sharing

The current CLI agent path returns the final response after the tool loop completes. The existing streaming transport remains available internally, but incremental tool-call streaming is not implemented.

## Contributing

Keep changes focused, preserve the one-way architecture, add deterministic tests for new behavior, propagate `CancellationToken`, and avoid provider-specific types outside Infrastructure.

## Maintainer

Pooya Alamirpour — [@Alamirpour](https://t.me/Alamirpour)

## License

AgentPulse is available under the [MIT License](LICENSE).

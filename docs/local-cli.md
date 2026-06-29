# Running the AgentPulse CLI locally

## Prerequisites

- .NET 8 SDK
- Git when repository-aware project resolution is needed
- The built-in OpenAI-compatible provider defaults, or an explicit OpenAI-compatible endpoint and model override
- An API credential supplied through the configured environment variable or `agentpulse auth set`

The real credential, prompt, and conversation history must not be placed in committed configuration files or diagnostic output.

## Restore and build

PowerShell:

```powershell
dotnet restore
dotnet build --no-restore -warnaserror
```

Bash:

```bash
dotnet restore
dotnet build --no-restore -warnaserror
```

## Direct execution

PowerShell:

```powershell
dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
dotnet run --project src/AgentPulse.Cli -- run --dir "C:\work\My Project" "Explain this project"
```

Bash:

```bash
dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
dotnet run --project src/AgentPulse.Cli -- run --dir "/work/My Project" "Explain this project"
```

The executable name used in help and packaged execution is `agentpulse`; the assembly and namespaces retain the `AgentPulse` identity.

## Redirected stdin

A positional prompt takes precedence. When no positional prompt exists, redirected stdin is read in full. Multiline UTF-8 text is preserved, one leading BOM is removed, and only final pipe line endings are removed.

PowerShell:

```powershell
"Explain this project" | dotnet run --project src/AgentPulse.Cli -- run
Get-Content -Raw .\prompt.txt | dotnet run --project src/AgentPulse.Cli -- run
```

Bash:

```bash
printf '%s\n' 'Explain this project' | dotnet run --project src/AgentPulse.Cli -- run
cat prompt.txt | dotnet run --project src/AgentPulse.Cli -- run
```

A non-interactive process never opens a credential prompt and fails before HTTP when the credential is absent. In a real interactive terminal, AgentPulse may request the configured credential with input interception so the secret is not echoed. This does not create an interactive chat loop.

## Provider configuration

Without `appsettings.json` or environment overrides, AgentPulse uses the built-in OpenAI-compatible base URL, model, and `OPENAI_API_KEY` credential-variable name. Missing explicit model configuration is therefore not an error by itself. If the credential is absent, resolution fails before any HTTP request with exit code `3` and actionable credential guidance.

The configurable profile is under `AgentPulse:Model`. Environment variables use the standard double-underscore form, for example:

PowerShell:

```powershell
$env:AgentPulse__Model__BaseUrl = "https://provider.example/v1"
$env:AgentPulse__Model__Model = "provider-model"
$env:AgentPulse__Model__ApiKeyEnvironmentVariable = "PROVIDER_API_KEY"
$env:PROVIDER_API_KEY = "<set outside source control>"
```

Bash:

```bash
export AgentPulse__Model__BaseUrl='https://provider.example/v1'
export AgentPulse__Model__Model='provider-model'
export AgentPulse__Model__ApiKeyEnvironmentVariable='PROVIDER_API_KEY'
export PROVIDER_API_KEY='<set outside source control>'
```

Alternatively, run `dotnet run --project src/AgentPulse.Cli -- auth set` from an interactive terminal. Stored credentials are scoped to the configured endpoint and protected by the existing credential store.

An explicit configuration with a non-absolute/unsupported base URL, an empty model, or an invalid/empty credential environment-variable name fails during startup with exit code `3`.

## Tool execution configuration

The built-in read-only tools are configured under `AgentPulse:Tools`. Environment-variable overrides use the standard double-underscore form, for example:

```powershell
$env:AgentPulse__Tools__MaxToolIterations = "8"
$env:AgentPulse__Tools__MaxReadLines = "500"
$env:AgentPulse__Tools__MaxGlobResults = "200"
$env:AgentPulse__Tools__MaxGrepResults = "100"
```

All tool paths are resolved against the selected workspace. The built-in tools cannot write files and reject paths that escape the workspace.

## Session continuation

A successful run writes only model text to stdout. The session identifier is written separately to stderr:

```text
Session ID: <id>
```

Continue the same project session with:

```powershell
dotnet run --project src/AgentPulse.Cli -- run --session <id> "Continue"
```

Only one run may be active for a session. A concurrent run fails with exit code `4` without creating an extra message.

## Logging

AgentPulse uses standard .NET logging configuration. Console logs always go to stderr and colors are disabled. The default level is `Warning`.

PowerShell:

```powershell
$env:Logging__LogLevel__Default = "Information"
dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
```

Bash:

```bash
Logging__LogLevel__Default=Information \
  dotnet run --project src/AgentPulse.Cli -- run "Explain this project"
```

Supported levels are `None`, `Error`, `Warning`, `Information`, `Debug`, and `Trace`. Debug and Trace record only safe metadata such as IDs, model identity, category, and prompt length. They do not record the prompt, history, raw provider body, authorization data, or credentials.

## Ctrl+C and partial responses

`Ctrl+C` cancels the active run and uses exit code `130`. An ordinary user presses `Ctrl+C`. The Windows process test targets the isolated CLI process group with `CTRL+BREAK`; Linux sends SIGINT with `SIG_UNBLOCK=1` and `SIG_SETMASK=2`; macOS sends SIGINT with Darwin values `SIG_UNBLOCK=2` and `SIG_SETMASK=3`. Text already streamed to stdout is not erased. The same partial text is checkpointed in the database, the assistant message is finalized as `Cancelled`, and the owned session lease is released. A later command can continue the same session.

## Bounded shutdown

After the command finishes, host shutdown has a two-second upper bound. A shutdown timeout or stop error writes only a short safe diagnostic to stderr, never writes to stdout, and does not replace the command's primary exit code (including `130` for Ctrl+C).

## Runtime data locations

Unless overridden by configuration:

- the SQLite database is stored below the platform-local application-data location for AgentPulse;
- protected credential files are stored below the AgentPulse security directory in platform-local application data.

For isolated development or tests, set:

```text
AgentPulse__Persistence__DatabasePath
AgentPulse__Security__CredentialRootPath
```

Use paths outside the source tree. Do not commit database, WAL/SHM, credential, captured output, or test-result files.

## Tests

```powershell
dotnet test --no-build
```

The CLI integration suite starts the real built CLI process with isolated database, credential, home, stdout, stderr, and local fake-provider resources. Windows uses `CTRL_BREAK_EVENT` against an isolated process group; Linux and macOS use group-targeted SIGINT with their platform-specific signal-mask constants. Interactive Windows uses ConPTY; interactive Linux/macOS uses the platform PTY through `/usr/bin/script`. It does not use the real user credential store or external network.

Run the interrupt-sensitive platform tests separately with:

```powershell
dotnet test --no-build --filter "Category=ProcessInterrupt"
```

PTY tests require the operating system's terminal facility: ConPTY on supported Windows versions and `/usr/bin/script` on Linux/macOS. Native verification for this revision remains pending until these tests run successfully on Windows, Linux, and macOS. A missing native terminal prerequisite fails explicitly; the tests never silently return as passed.

## Exit codes

```text
0    success/help
1    unexpected/general/persistence/output failure
2    usage or validation failure
3    configuration or credential failure
4    session/project conflict or missing session
5    provider request failure
124  provider timeout
130  Ctrl+C/user cancellation
```

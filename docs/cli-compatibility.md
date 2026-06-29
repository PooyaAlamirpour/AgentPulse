# CLI compatibility matrix — Phase 9

This document records the observable compatibility decision for the Phase 0 Node baseline and the hardened .NET `agentpulse` CLI. The Node files used as behavioral references are `run.ts`, `run-completion.ts`, `ui.ts`, `bootstrap.ts`, and `provider/error.ts`. TypeScript structure is not treated as a porting contract.

Status values:

- **Matched** — the observable behavior is equivalent for the supported Phase 9 surface.
- **Fixed in Phase 9** — the .NET behavior was hardened or made explicit in this phase.
- **Intentionally Different** — the difference is deliberate, documented, and tested.
- **Not Applicable** — the Node behavior belongs to a feature explicitly outside the .NET Phase 9 scope.

## Matrix

| Behavior | Node baseline | .NET Phase 9 behavior | Status | Coverage |
|---|---|---|---|---|
| Command syntax | `run [message..]` plus a larger option set | `agentpulse run [--dir] [--model] [--session] [prompt]` | Intentionally Different | `Help_documents_the_phase_nine_stream_contract`, parser tests |
| Executable name | reference Node CLI-branded executable | Existing `agentpulse` executable and assembly remain unchanged | Intentionally Different | help process tests, repository docs test |
| Help output | yargs-generated command help | Stable, provider-neutral help matching the supported surface | Fixed in Phase 9 | `Help_runs_successfully_without_requesting_a_credential`, `Help_documents_the_phase_nine_stream_contract` |
| Version output | yargs exposes the Node package version | No public version option exists in the approved .NET command surface; `--version` is rejected on stderr with exit `2` | Intentionally Different | `Parser_and_usage_failures_return_the_usage_exit_code` |
| Unknown command | Parser failure | stderr, exit `2`, empty stdout | Fixed in Phase 9 | `Parser_and_usage_failures_return_the_usage_exit_code` |
| Unknown option | Parser failure | stderr, exit `2`, empty stdout | Fixed in Phase 9 | same theory |
| Missing option value | Parser failure | stderr, exit `2`, empty stdout | Fixed in Phase 9 | same theory |
| Prompt positional | Message arguments are accepted | Positional prompt is accepted and preserved | Matched | `Run_receives_prompt_from_arguments_and_streams_only_model_text_to_stdout` |
| Prompt from stdin | Redirected stdin can contribute input | Redirected stdin is read only when no positional prompt exists | Intentionally Different | stdin process tests and `PromptInputReaderTests` |
| Positional plus stdin | Node appends redirected stdin to message input | Positional prompt wins and stdin is not read | Intentionally Different | `Positional_prompt_takes_precedence_over_redirected_stdin` |
| Empty prompt | Run fails | Validation message on stderr, exit `2` | Fixed in Phase 9 | empty/BOM/whitespace process tests |
| Interactive stdin | Node can use terminal input in interactive flows | A real terminal may securely prompt for a missing credential, but a missing run prompt never opens a chat loop | Intentionally Different | `Interactive_process_prompts_for_a_credential_without_echoing_or_persisting_it`, help contract |
| Redirected stdin | Supported | Full UTF-8 input, multiline/Unicode preserved, leading BOM removed, terminal CR/LF removed | Fixed in Phase 9 | `Redirected_stdin_preserves_supported_text`, existing multiline process test |
| Invalid directory | Bootstrap fails | Fails before database, session, message, credential, or provider work; exit `2` | Fixed in Phase 9 | nonexistent/file directory process tests |
| Path containing spaces | Supported through argument parsing | Supported through `ArgumentList` and `Path` APIs; no quote characters persisted | Fixed in Phase 9 | `Path_containing_spaces_is_resolved_without_quotes_in_persistence` |
| Invalid session ID format | Parser validation | Rejected before database access; exit `2` | Fixed in Phase 9 | parser process theory |
| Session not found | Node reports session lookup failure | Safe stderr message, exit `4`, no message/provider request | Fixed in Phase 9 | `Well_formed_missing_session_returns_session_exit_code_without_creating_messages` |
| Session from another project | Project/session association enforced | Safe stderr message, exit `4`, no new message/provider request | Fixed in Phase 9 | `Session_from_another_project_is_rejected_without_creating_messages` |
| Session busy | Node runtime serializes session work | SQLite lease rejects a concurrent run; exit `4` | Intentionally Different | `Session_busy_is_stable_and_does_not_create_an_extra_message` |
| Recovered crashed session | Node lifecycle is server/runtime based | Expired database lease is recovered on the next run; abandoned assistant becomes `Failed` | Intentionally Different | `Crash_after_partial_checkpoint_is_recovered_by_the_next_run` |
| No explicit provider configuration | Provider setup depends on product configuration | Built-in OpenAI-compatible defaults are used; credential resolution then runs, and a missing credential fails before HTTP with exit `3` | Intentionally Different | `Missing_explicit_configuration_uses_built_in_defaults_then_reports_missing_credential` |
| Invalid explicit provider configuration | Provider configuration validation | Invalid base URL, empty model, or invalid credential-variable name fails before network access; exit `3` | Fixed in Phase 9 | `Invalid_explicit_model_configuration_returns_configuration_exit_code` |
| Missing credential | Provider/auth resolution fails | Detected before HTTP; non-interactive mode fails with guidance, while an interactive terminal uses a hidden credential prompt; exit `3` when unresolved | Fixed in Phase 9 | non-interactive and PTY credential process tests |
| Authentication failure | Provider error renderer | Safe categorized message; exit `5` | Fixed in Phase 9 | provider HTTP failure theory |
| Permission failure | Provider error renderer | Safe categorized message; exit `5` | Fixed in Phase 9 | provider HTTP failure theory |
| Rate limit | Provider error renderer | Safe categorized message; exit `5` | Fixed in Phase 9 | provider HTTP failure theory |
| Provider unavailable | Provider error renderer | Safe categorized message; exit `5` | Fixed in Phase 9 | provider HTTP failure theory |
| Protocol/invalid response | Stream/parser error | Partial stdout retained; safe stderr; exit `5` | Fixed in Phase 9 | `Partial_provider_failure_preserves_stdout_and_persistence_and_allows_continuation` |
| Timeout | Provider timeout | Distinct exit `124`, not cancellation | Fixed in Phase 9 | `First_byte_timeout_is_distinct_from_user_cancellation` |
| Ctrl+C before first token | Cancellation | Native process-group interrupt on Windows, Linux, and macOS; safe cancellation message, assistant `Cancelled`, lease released, exit `130`, no hang | Fixed in Phase 9 | `Interrupt_before_first_token_cancels_the_run_and_allows_continuation` |
| Ctrl+C after partial response | Cancellation | Native process-group interrupt on Windows, Linux, and macOS; partial stdout and checkpoint retained, assistant `Cancelled`, lease released, exit `130` | Fixed in Phase 9 | `Interrupt_after_partial_token_preserves_partial_state_and_releases_the_lease` |
| Unexpected exception | Bootstrap prints error | Central safe renderer, empty stdout, exit `1`; stack trace is not user-facing | Fixed in Phase 9 | renderer/unit composition coverage |
| stdout content | Node UI uses stderr while response formats vary | Only streamed model text and the successful final newline | Intentionally Different | success, partial failure, logging tests |
| stderr content | Node UI diagnostics use stderr | Session ID, validation/config/provider errors, cancellation, and logs only | Matched | process output tests |
| Help stream | yargs help is normal command output | Help is written to stdout and exits `0` | Matched | help process tests |
| Session ID rendering | Node runtime owns session presentation | `Session ID: <id>` is emitted only on stderr after success | Intentionally Different | success and metadata process tests |
| Exit codes | Mostly generic process failure semantics | Central contract: `0,1,2,3,4,5,124,130` | Intentionally Different | process exit-code tests |
| ANSI/color | UI styles terminal output | Current single-command .NET surface emits no ANSI; redirected streams remain plain | Intentionally Different | output assertions and no-ANSI policy |
| Logging behavior | Node log subsystem | Standard .NET logging, configurable by existing configuration/environment, console logs on stderr | Intentionally Different | logging-none/debug process tests |
| Secret redaction | Provider renderer may expose selected provider details | Credential, raw error body, prompt/history, authorization, and connection secrets are never rendered/logged/persisted as failure metadata | Intentionally Different | `Debug_logging_stays_on_stderr_and_redacts_credentials_and_provider_body` |
| Attachments/tools/agent loop | Supported by Node `run` | Not implemented in this phase | Not Applicable | scope/hygiene documentation tests |
| JSON event format | Supported by Node | Explicitly out of scope | Not Applicable | scope/hygiene documentation tests |
| Interactive multi-turn loop | Available elsewhere in Node product | Explicitly out of scope; `run` remains one command/one response | Not Applicable | help contract |
| Host shutdown | Node lifecycle differs | Host stop is bounded to two seconds; a safe stderr diagnostic never replaces the primary command exit code | Intentionally Different | `Shutdown_timeout_is_bounded_and_writes_only_a_safe_stderr_diagnostic` |

## Intentional differences

| Difference | Node behavior | .NET behavior | Reason | Test covering .NET behavior |
|---|---|---|---|---|
| Executable identity | reference Node CLI executable naming | Existing `agentpulse` assembly/command | Avoid an unrelated assembly, namespace, package, and documentation rename | help process tests |
| Supported command surface | Many options, tools, attachment, sharing, and JSON output | Only `--dir`, `--model`, `--session`, positional prompt, and redirected stdin | Later product capabilities are explicitly excluded from Phase 9 | parser/help tests |
| Positional plus redirected stdin | Redirected text may be appended | Positional prompt takes precedence and stdin remains unread | Preserves the approved Phase 8 input contract and avoids accidental pipe consumption | `Positional_prompt_takes_precedence_over_redirected_stdin` |
| Output contract | UI and completion rendering depend on format/TTY | stdout is pipe-safe model text; all metadata/logs/errors use stderr | Deterministic shell composition and redirection | stdout/stderr process tests |
| Exit codes | Generic Node process conventions | Typed .NET CLI categories | Makes automation distinguish usage, configuration, session, provider, timeout, and Ctrl+C | exit-code process tests |
| Run serialization/recovery | Node server/runtime lifecycle | Database-backed lease with expiry recovery | Existing .NET persistence architecture supports cross-process safety without a background service | busy/crash recovery process tests |
| Credential prompting | Product may route through broader auth flows | Non-interactive runs fail before HTTP; a true terminal uses an intercepting, non-echoing prompt | Prevent CI/pipe hangs while preserving secure local setup | non-interactive and PTY credential process tests |
| Logging format | Node-specific logger | Standard .NET simple console logger, no color, stderr-only | Reuse the existing .NET configuration system and avoid a new framework/CLI option | logging process tests |
| Native interrupt harness | Node test/runtime mechanisms differ | Windows runs the CLI as the root of an isolated process group and targets it with `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT)`; Linux and macOS use `setpgid` plus `kill(-pgid, SIGINT)` | Exercises the actual `Console.CancelKeyPress` boundary without changing product runtime | before-token and after-partial process tests |

## Exit-code contract

| Code | Meaning |
|---:|---|
| `0` | Success/help |
| `1` | Unexpected, output, persistence, or general failure |
| `2` | Usage, parser, prompt, directory, or validation failure |
| `3` | Invalid explicit configuration or missing credential; absent explicit model configuration uses built-in defaults |
| `4` | Session missing, mismatch, busy, or lease conflict |
| `5` | Provider authentication, permission, rate limit, availability, protocol, or other request failure |
| `124` | Provider timeout |
| `130` | User cancellation/Ctrl+C |

## Stream contract

- Successful run stdout is exactly the streamed provider response followed by one platform newline.
- Failure before the first token leaves stdout empty.
- Failure or cancellation after tokens leaves partial stdout unchanged and does not append an error.
- stderr carries session metadata, short actionable errors, cancellation messages, and configured logs.
- Console logging is disabled from stdout at every level, including Debug and Trace.
- No raw provider body, API key, authorization header, complete prompt, or conversation history may be rendered.
## Process-level platform coverage

- **Windows implementation:** a test-only helper owns a private console and launches the real CLI as the root of an isolated process group. Interrupt tests target that group with `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, groupId)`. The helper registers a real handler for both `CTRL_C_EVENT` and `CTRL_BREAK_EVENT` while the event is in flight, then unregisters it in `finally`/`Dispose`. The test runner itself never joins the helper console.
- **Linux implementation:** the helper calls `setpgid(0, 0)` before launching the CLI, keeps the CLI in that isolated group, and the test process sends `kill(-pgid, SIGINT)`. The test-only launcher uses the Linux `pthread_sigmask` values `SIG_UNBLOCK=1` and `SIG_SETMASK=2`.
- **macOS implementation:** the same POSIX process-group path is used with `setpgid` and group-targeted SIGINT. The test-only launcher uses the Darwin `pthread_sigmask` values `SIG_UNBLOCK=2` and `SIG_SETMASK=3`. Interactive execution uses the BSD `/usr/bin/script` syntax rather than the util-linux argument form.
- **Interactive terminals:** Windows uses ConPTY; Linux uses util-linux `script`; macOS uses BSD `script`. Text assertions normalize only recognized VT control sequences and retain the raw transcript for safe diagnostics. The fake credential marker must not be echoed or persisted.
- The interrupt-sensitive tests are serialized in the `ProcessInterruptTests` xUnit collection and tagged with `Category=ProcessInterrupt`.
- Missing native terminal prerequisites fail explicitly; platform detection never returns from a test as a passing assertion-free path.
- Host shutdown is bounded to two seconds. A timeout or stop failure writes a safe stderr diagnostic and preserves the primary success, failure, timeout, or cancellation exit code.

## Verification status

Phase 9 implementation is complete. Native verification for this revision remains pending on Windows, Linux, and macOS. Do not describe the phase as fully cross-platform verified until the interrupt and interactive terminal tests have executed successfully on all three operating systems.

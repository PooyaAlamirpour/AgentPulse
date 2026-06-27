# Model Request Construction Policy

## Scope

Phase 5 builds provider-independent model requests in the Application layer. It does not call a provider, perform HTTP requests, execute streaming, scan source files, or introduce tools, agents, plugins, functions, or attachments.

## Deterministic request order

Every request is constructed in this exact order:

```text
system
→ completed previous history ordered by Message.Sequence
→ current user message
```

The current run boundary is the current user message sequence. History entries with the current user message identifier, or with a sequence at or after that boundary, are not added as previous history. Duplicate detection uses identifiers and sequences, never text comparison.

## Previous-message status policy

| Message status | Included in model history |
| --- | --- |
| `Completed` | Yes |
| `Pending` | No |
| `Streaming` | No |
| `Failed` | No |
| `Cancelled` | No |

Excluding an incomplete message from a model request does not delete or alter its persisted content.

## Text-part conversion

Only `TextMessagePart` is supported in this phase. Parts are sorted by `MessagePart.Order` and joined with a single line-feed character (`\n`). Unsupported part types fail request construction instead of being silently ignored. A message that has no usable text is rejected.

## System context format

The system message contains Project ID, current directory, project root, Git repository state, Git worktree, platform, and current UTC date. The date uses the invariant round-trip ISO 8601 format (`O`). Missing optional values are rendered as `(none)`. Paths are emitted exactly as provided by the validated `ProjectContext`.

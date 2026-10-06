# External subagent trial host

`Start-SubagentTrialHost.ps1` brokers one persistent CLI JSONL session for an
external coding subagent. The model remains outside AgentLang. The host fixes
the runtime DLL, project directory, CLI profile, argument list, operation
whitelist, and transport limits before the subagent sends protocol requests.
The broker is a transport wrapper, not an operating-system sandbox: an external
subagent can still have other host tools unless its surrounding platform limits
them.

The current wrapper uses a Windows Job Object with kill-on-close and is
intentionally x64-only. At startup it checks the `Marshal.SizeOf` and
`Marshal.OffsetOf` layout used by `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`:
`JOBOBJECT_BASIC_LIMIT_INFORMATION` is 64 bytes with `LimitFlags` at offset 16;
`IO_COUNTERS` is 48 bytes; the extended structure is 144 bytes with `IoInfo` at
64 and memory-limit/peak fields at 112, 120, 128, and 136. A mismatch stops the
host before the runtime starts.

## Run

Use a fresh, isolated project directory and a new trace filename for each
session. Pin the CLI DLL and its `.deps.json` and `.runtimeconfig.json` files.
The wrapper records their hashes at session start. For the AgentLang CLI:

```powershell
pwsh -NoProfile -File scripts/Start-SubagentTrialHost.ps1 `
  -CliDll .agentlang/subagent-matched/host-9c6ba75/AgentLang.Cli.dll `
  -ProjectPath .agentlang/subagent-trials/run-001 `
  -TracePath .agentlang/reports/subagent-run-001.jsonl `
  -AllowedOperations 'task.begin,define,test,commit,task.commit,describe,search,tests' `
  -Profile agentlang `
  -ExchangeTimeoutMilliseconds 15000 `
  -MaxRequestBytes 262144 `
  -MaxResponseBytes 524288 `
  -MaxExchanges 100
```

The operation list is a comma-delimited host setting, not agent input. Each
request remains one JSON object on one UTF-8 JSONL line. The wrapper relays a
valid, allowed request unchanged and returns its single runtime response line.
Malformed JSON and non-whitelisted operation names receive structured host
errors and are never forwarded. The host exits when the request limit is
exceeded, the exchange limit is reached, the runtime stops responding, or the
operator cancels the host.

Each exchange has one deadline covering both the async stdin write/flush and
the runtime stdout response read. Those operations run concurrently so a child
that stops reading cannot hold the broker in a blocking pipe write. Request and
response payload limits exclude the terminating LF; trace wire-byte counts
include it. A configured request limit is at most 512 KiB, a response limit is
at most 1 MiB, the deadline is at most 120 seconds, and a session is limited
to 100 exchanges. The default maximum runtime response is 512 KiB.

## Optional cumulative inspection budget

`MaxInspectionResponseBytes` is an optional nonnegative cumulative budget over
complete runtime inspection response payloads. Omit it for the unchanged
schema-1 relay. An explicit zero enables the budget and denies inspection before
forwarding; it differs from omission. The budget excludes the terminating LF,
includes a preceding CR when present, and counts runtime diagnostics as well as
successful inspection data. Complete responses are admitted atomically; the
broker never truncates JSON to fit. The separate per-line response limit still
applies first.

The host classifies operations, independently of request fields. Conventional
inspection is `inspect`, `read` and `search`. AgentLang inspection includes word,
type, dependency and source queries, bounded context/graph queries, metadata
tests/examples listings, history/diff, task/status logs, storage status and
stack inspection. `failed-tests` executes tests and is excluded, as are
`test`, `test-all`, example execution, eval, definitions, commits, snapshots and
all mutations. Inspection can still record task bookkeeping; it does not promise
zero host-state changes.

When no allowance remains, `TRIAL_INSPECTION_BUDGET_EXHAUSTED` reports a query
that was not sent or executed. When a complete valid response exceeds a positive
remaining allowance, `TRIAL_INSPECTION_BUDGET_EXCEEDED` replaces the agent-facing
response while the raw runtime response remains in the trace. The query was
observed to finish; denial does not imply rollback. Noninspection results stay
visible regardless of the remaining inspection allowance. Timeout, invalid JSON,
oversized responses and other uncertain execution keep their existing handling;
the host never automatically replays a request.

Enabled sessions use trace schema 2 and distinguish raw runtime payloads,
selected host responses, admitted inspection payloads, noninspection payloads
and host denial/control responses. A post-flush `response-delivered` event
records successful pipe delivery, separately from selection before the write.
It does not establish model consumption. The cap does not bound total agent
input: tests/mutations, denial responses and the actor's supplied instructions
are outside it. Do not describe these bytes as tokens or a model context window.

In schema 2, an exchange's `observedRuntimeResponse` retains complete valid
runtime payloads, including withheld ones, with payload/wire hashes and base64.
The existing `response` field is the selected line before the stdout write.
`inspectionBudget` records classification, admission decision and before/after
allowance. `responseAccounting` separates raw valid runtime bytes, selected
bytes, admitted inspection bytes, noninspection runtime bytes, host control
bytes and post-flush pipe delivery totals. The terminal `session-end` includes
the final counters. Partial or invalid observed responses keep their existing
forensic completeness flags and do not consume inspection admission.

## Profiles and host arguments

`agentlang` is the default profile. It supplies the fixed clock and, when
provided, the configured capability allowlist. `conventional` omits those
AgentLang-only flags. Conventional startup can use a fixed host argument list,
for example a configured validation project; the agent cannot select or change
that list. Use `AdditionalCliArguments` from a host PowerShell caller, or pass
the same string array as one JSON value when launching this script through a
command line:

```powershell
-Profile conventional `
-AdditionalCliArgumentsJson '["--validation-project","MatchedRenewal.fsproj"]'
```

The wrapper rejects additional arguments that override `--project`, `--jsonl`,
`--allow`, or `--clock`, and places the fixed project path and JSONL mode under
host control. Never place credentials in this argument list.

## Trace and uncertain outcomes

The trace is append-only JSONL created with `CreateNew`; an existing trace is
never overwritten. It records hashes for every top-level runtime DLL beside the
CLI and its `.deps.json` and `.runtimeconfig.json`, plus the profile, allowed
operations, host-selected arguments, per-exchange canonical JSON, the original
line, exact wire bytes as base64 and SHA-256, elapsed time, host/runtime source,
and structured error codes. Runtime stderr content is not copied to the trace;
only byte counts and a prefix hash are retained. The wrapper does not inspect,
record, or emit environment variables or authorization headers.

For a successfully returned response, `requestDelivery.state` is `confirmed`
only after the complete UTF-8 request line and flush completed into the runtime
pipe. It does not prove that the runtime committed a durable change. A timeout,
oversized response, child exit, cancellation, or invalid response can happen
after a mutation ran. Such exchanges report `executionState: uncertain`, include
`automaticRetry: never`, and preserve any observed partial or invalid response
bytes with a completeness flag. The broker
does not replay a request. Inspect authoritative project state in a fresh
runtime process before deciding how to proceed. A missing response is not proof
of rollback.

Traces intentionally retain exact prompts, tool requests, and runtime responses,
which may include project information. Store them accordingly. Do not include
secret credentials in requests, prompts, project data, or fixed CLI arguments.

## Verification

Run the focused transport and persistence verifier on Windows x64:

```powershell
pwsh -NoProfile -File scripts/Verify-SubagentTrialHost.ps1
```

The verifier creates fresh, retained scratch projects under `.agentlang/`,
builds a tiny controlled fake runtime, saves a JSON evidence report under
`.agentlang/reports/`, and checks a real pinned CLI flow plus deny/malformed,
oversized, partial-response timeout, invalid and oversized response, child-exit,
large write-to-nonreading-child, verifier output-cap, conventional-profile, and
fresh-process reload cases. The fake child is a transport fixture, not a model or language
implementation. The evidence records exact runtime DLL/configuration and
wrapper/document hashes, repository HEAD and dirty status, process outcomes,
traces, and bounded check results. If the requested evidence filename already
exists, the verifier selects the next `-retry-NN` filename instead of
overwriting earlier evidence. It contains no model token, turn, or
context-window measurements.

The conventional profile has no special OS sandbox. Its fixed build/test
command can execute code with the permissions of the host process; use only a
reviewed project snapshot in an appropriately restricted environment.

# External subagent trial host v2

`Start-SubagentTrialHostV2.ps1` brokers one persistent CLI JSONL session for an
external coding subagent. The model remains outside AgentLang. The host fixes
the runtime DLL, project directory, CLI profile, argument list, operation
whitelist, and transport limits before the subagent sends protocol requests.
The broker is a transport wrapper, not an operating-system sandbox: an external
subagent can still have other host tools unless its surrounding platform limits
them.

The current conventional dispatcher fixes `-p:NuGetAudit=false` for both build
and run validation and records it in each validation response's command array.
This keeps vulnerability-feed availability out of task acceptance. Package
restoration still follows normal .NET behavior; this is not a fully offline build
policy. Pin the runtime and validation policy before participant dispatch.
Historical traces retain their original commands and outcomes.

## Participant isolation

Each participant receives a fresh context, its own project copy, pinned prompt
and runtime, and a separate broker trace. Its prompt must explicitly prohibit
listing other agents, reading their status or messages, contacting siblings,
delegating work, and using another participant's findings. All project discovery,
edits and tests go through its assigned broker. The coordinator must not supply
solution hints during an active trial.

Where the surrounding platform supports it, remove collaboration and unrelated
filesystem tools from the participant's available tools. The broker itself cannot
enforce these restrictions on external tools. Prompt compliance alone is not
proof of enforced isolation. Use a single active participant for a focused
discovery follow-up when concurrent participants are unnecessary.

Record any observed or reported out-of-band communication, including exact
messages and coordinator replies where available. Mark affected runs as
non-independent, preserve their results, and identify any repeat as a separate
trial. Ask the participant to disclose non-broker tool use at completion. Do not
silently replace contaminated observations with cleaner ones. Report147's fold
discovery trial demonstrates why these restrictions need to be explicit.

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
pwsh -NoProfile -File scripts/Start-SubagentTrialHostV2.ps1 `
  -CliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll `
  -ProjectPath .agentlang/subagent-trials/run-001 `
  -TracePath .agentlang/reports/subagent-run-001.jsonl `
  -AllowedOperations 'task.begin,define,test,commit,task.commit,describe,search,tests' `
  -Profile agentlang `
  -ExchangeTimeoutMilliseconds 15000 `
  -MaxRequestBytes 262144 `
  -MaxResponseBytes 524288 `
  -MaxExchanges 100
```

This example uses a local Release CLI build. To run a pinned experiment build,
pass its CLI DLL with `-CliDll` and keep its companion files beside it.

The operation list is a comma-delimited host setting, not agent input. Each
request remains one JSON object on one UTF-8 JSONL line. The wrapper relays a
valid, allowed request unchanged and returns its single runtime response line.
The broker and both CLI entry points select UTF-8 explicitly, including when
the Windows terminal starts with a legacy code page. Valid non-ASCII text must
survive exactly; malformed UTF-8 bytes receive `TRIAL_INVALID_UTF8` without
being forwarded. A subsequent valid request can still execute. See
[report 158](../reports/158-utf8-trial-transport.md) for terminal and pipe checks.
Malformed JSON and non-whitelisted operation names receive structured host
errors and are never forwarded. The host exits when the request limit is
exceeded, the exchange limit is reached, the runtime stops responding, or the
operator cancels the host.

The v2 host also recognizes the exact JSON object `{"op":"host.close"}` as a
transport control. Send it as one complete, valid UTF-8 JSONL line. It is not a
runtime operation, is never forwarded, does not use the response-size budget,
and does not consume an exchange. It remains available when `MaxExchanges` has
been reached. The host writes no JSON acknowledgement: it closes child stdin
and waits up to `ExchangeTimeoutMilliseconds` for the runtime to exit. A
shutdown timeout or nonzero runtime exit makes the host exit nonzero. The
`AllowedOperations` setting rejects `host.close` so it cannot be exposed as a
runtime operation.

An object with extra fields, duplicate properties, or another invalid shape
using `op: "host.close"` is rejected as `TRIAL_INVALID_REQUEST` and never
forwarded. Such malformed requests follow the normal exchange-count and limit
rules. This reserved control is separate from the operation whitelist that
governs requests sent to the runtime.

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

V2 `session-start` records `hostProtocolVersion: "subagent-trial-host-v2"` and
`transportControls: ["host.close"]`. An accepted control writes a `host-close`
event with the unchanged request's `requestRaw`, sorted-key `requestCanonical`,
`requestWireUtf8Bytes`, `requestWireSha256`, `requestWireBase64`, and the
current `exchangeCount`. Wire fields include the terminating LF; raw and
canonical values are the payload without LF. The event records that the
control was accepted, not that shutdown completed. The final `session-end`
records `terminationKind` as `host-close`, `input-eof`, `cancelled`,
`exchange-limit`, or `other-failure`, along with `hostExitCode` and
`runtimeExitCode`. A successful close requires terminal host exit 0 and a
session-end with `terminationKind: "host-close"`, `hostExitCode: 0`, and
`runtimeExitCode: 0`.

Ordinary input EOF is recorded as `terminationKind: "input-eof"`. That says
only that the input stream ended; it is not evidence that the actor completed
successfully. Do not infer cancellation or actor completion from EOF.

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
pwsh -NoProfile -File scripts/Verify-SubagentTrialHostV2.ps1 `
  -CliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll
```

The verifier requires a CLI DLL. It exercises a real CLI write, commit and
fresh-process reload through `host.close`, then uses a controlled fake runtime
for close-at-limit, no-ack under a one-byte response cap, malformed and
duplicate close rejection, unterminated input, ordinary EOF, and bounded
shutdown of a hanging child. It also checks that the termination auditor rejects
EOF, failed shutdown, and mutated, duplicate, or missing close evidence. The
fake runtime is a transport fixture, not a model or language implementation.
The v1 verifier remains the broader baseline transport suite.

The verifier writes a fresh real CLI trace under `.agentlang/reports/` with a
name like `trial-host-v2-<run-id>-real-cli-write-close.jsonl`. Pass that exact
trace to the termination auditor:

```powershell
pwsh -NoProfile -File scripts/Audit-SubagentTrialTerminationV2.ps1 `
  -TracePath '.agentlang/reports/trial-host-v2-<run-id>-real-cli-write-close.jsonl'
```

The focused verifier writes a JSON evidence report and retains its scratch
projects and transport traces under `.agentlang/`. The evidence includes the
tested revision and dirty state, CLI and script hashes, fixture and process
results, and trace inventory. The termination auditor checks explicit close
evidence; it is not a general repository-integrity audit. These checks provide
no model behavior, throughput, end-user turn, or context-window measurements.

The conventional profile has no special OS sandbox. Its fixed build/test
command can execute code with the permissions of the host process; use only a
reviewed project snapshot in an appropriately restricted environment.

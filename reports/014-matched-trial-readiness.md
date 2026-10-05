# Matched external subagent trial readiness

Status: preparation in progress. No matched agent trials have run; no performance result is claimed.

## Purpose and scope

Prepare one matched follow-up task in three modes: A starts from trusted primitives and generated Customer/Subscription schemas, B also retains the two library words accepted from the previous external-agent pilot, and C is an equivalent conventional F# fixture with those baseline helper semantics. The task adds premium annual renewal balance behavior. This is a toy Float-balance fixture, separate from the exact-money business contract and the proposed 60-task bank.

All modes receive the same policy: exactly `premium` customers have a 10% baseline discount; an additional 5% reduction of that discounted balance applies only for exactly `annual` subscriptions with `renewable = true`. Every other combination keeps the baseline balance. Vocabulary discovery is part of the task; the prompt does not identify baseline helper names. Independent acceptance must cover the complete three-condition eligibility matrix, case-sensitive inputs, zero/negative/larger balances, preservation of prior behavior, and persisted typed output. Language definitions must satisfy library tests and own-body coverage; conventional validation uses a fixed executable test project. These differing development gates must be reported when comparing observed effort.

The baseline words in B come from the actual accepted external-agent task recorded in report 004, not from a newly synthesized solution to the renewal task. C's baseline helper translations are host-prepared comparison infrastructure and must be independently checked against B before any trial. A must have zero authored words/tests before the task; generated schema operations are explicitly allowed.

## Runtime pin

The host artifacts were copied before new Core builds to `.agentlang/subagent-matched/host-9c6ba75`. Root checked every DLL and the dependency/runtime configuration files against the successful report-010 parity candidate hashes. The 22-file manifest identifies implementation revision `f1836d1f3a097b6bfa9dcde41d047a0a5da00187` and publication revision `9c6ba7569b0b28c485a2dec753e001043762e2e4`. This keeps new language edits from changing an in-progress pilot's runtime.

## Framework limit

On 2026-10-05, the framework refused a fresh subagent spawn with `agent thread limit reached`. Reusing existing implementation agents would preserve earlier project context. Such a run cannot be presented as a fresh-context controlled comparison. Fixture/host work can proceed independently, but actual fresh matched trials remain pending until the framework permits new agent contexts. This is a limitation of the experiment interface, not an AI component of the language.

## Measurement contract

The external trial host should own one persistent JSONL child process and record exact requests/responses, UTF-8 bytes, forwarding decisions, elapsed exchange time, and bounded failure outcomes. Request/response bytes are observable protocol artifacts; they are not total framework context or token counts. Exact LLM tokens, framework turns, and full model context remain unavailable and must be recorded as unavailable. Startup/baseline/host acceptance interactions are separate from agent interactions.

The host whitelist confines protocol operations but does not provide an OS sandbox against a subagent's other tools. Conventional F# validation runs with host permissions. Runtime/provider logs, source snapshots, fixed binary hashes, independent acceptance, and reviewed traces are required before a run is accepted. A single task per mode is exploratory evidence rather than a general token, latency, or correctness improvement.

## Validation

Root ran `scripts/Verify-MatchedRenewalFixtures.ps1` against the pinned Release runtime and the freshly built conventional fixture. All **70 selected checks passed**. The verifier checks exact Growing source provenance, zero authored Flat words/tests, exactly two Growing words and seven passing tests, absence of the renewal solution in both language dictionaries, twelve matched finite baseline cases, exact single-Float language output, and the conventional placeholder's intended failure: seven of eight eligibility cases pass and only premium/annual/renewable fails. Evidence is currently saved in `.agentlang/reports/014-matched-fixture-validation.json`; publication alongside the fixture milestone remains pending. This selected comparison does not prove all Float inputs or signed-zero representation parity.

The first root startup probe incorrectly submitted retained durable source to `define`, which correctly rejected host-managed maturity/revision metadata. The supported setup copies the fixture to a fresh project's `dictionary.agent` and starts the runtime normally. That path passes all seven retained tests. Legacy source without IDs receives identities on load; actual trial snapshots must freeze identities before runs to be reproducible.

The fixture owner reports fresh Conventional.Cli and conventional fixture Release builds with zero warnings/errors, intended default test exit 1, strict baseline adapter validation, successful search/fixed validate operations, and rejection of unsupported execute. Root independently verified the baseline semantics and default test failure described above. No matched agent run has occurred.

Root's focused bounded trial-host verifier passed **16 checks** against the
pinned runtime. These cover a real write/test/commit transaction, confirmed pipe
delivery, fresh-process reload, x64 JobObject ABI layout, rejected malformed,
disallowed and oversized requests, bounded verifier output capture, partial
response timeout, a blocked large stdin write, oversized/invalid/lost responses
with truthful uncertain execution, and Conventional-profile argument handling.
The Conventional-profile check uses a controlled fake child; it does not prove
the actual Conventional CLI adapter yet. Evidence is retained at
`.agentlang/reports/014-subagent-host-validation-retry-01.json`. The first attempt
failed on PowerShell rejecting an intentionally empty input string; that binding
was fixed and its failed evidence is retained separately. Dependency/script hash
pinning was then tightened and the verifier passed all 16 checks again; evidence
is retained at `.agentlang/reports/014-subagent-host-validation-retry-02.json`
with dependency DLL and verification-source hashes. The fixture verifier also
passed its 70 checks again with schema-v2 dependency manifests at
`.agentlang/reports/014-matched-fixture-validation-v2.json`. Full gate and
clean-head CI are pending. No agent or model ran during either verifier.

Root also exercised the actual Conventional CLI through the Conventional trial
host using a fresh copy of the fixture and two finite requests. Four assertions
passed: clean broker/runtime exit with two responses, complete delivery of both
requests, search finding the five baseline matches, and fixed validation
truthfully returning `VALIDATION_FAILED` for the intentionally unsolved premium
annual renewable case. This expected test failure does not count as task success.
The source trace and artifact hashes are recorded in
`.agentlang/reports/014-conventional-host-smoke-validation.json`. An initial
startup probe placed its trace inside the project; the host rejected that layout
before execution, and the corrected probe uses a separate trace path.

The CLI owner also bounded standalone sessions to 100 request lines by default,
configurable from 1 to 100. An excess line is rejected without dispatch and exits
with code 2; malformed lines count toward the cap, and EOF at the exact cap exits
normally. Its isolated Release build and normal/exact/excess/malformed/startup
argument checks passed. Root reviewed the implementation and independently
checked the ordinary host integration above. These bounds do not claim a total
host heap quota or make the fixed validation command an OS sandbox.

The permanent `AgentLang.Conventional.Cli.Tests` suite is now part of the
solution and normal validation gate. Its owner and the coordinator each ran
`dotnet run --project tests/AgentLang.Conventional.Cli.Tests/AgentLang.Conventional.Cli.Tests.fsproj -c Release`:
all **5 groups / 305 assertions passed**. It proves exact/default request
boundaries, malformed-line accounting, startup rejection before dispatch,
restoration of Console streams, and that a valid over-limit mutation is never
dispatched. These focused results precede the complete code-milestone gate and
do not constitute an agent experiment.

Readiness audit corrected the fixture README's identity-freezing instruction:
loading a legacy seed assigns IDs in memory, and saving that seed alone does
not persist those IDs. A verified committed-generation migration remains
required before any reproducible matched trial. The fixture verifier's 70
checks establish its stated selected cases and manifests, not this missing
preparation step. Frontend cutover also requires equivalent new-source fixtures.

## Integrated validation

The subsequent fresh full Release gate passed all 23 required checks, and the
pinned historical CLI comparison passed 314 selected checks. See
[report 021](021-integrated-flow-foundation.md) and its saved evidence. These
results validate the delivered slice; the full Flow migration and controlled
agent evaluation remain incomplete.

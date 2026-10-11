# 220 — Fresh language/F# typed batch maintenance comparison

Status: preparation only. No participant dispatched; not frozen. Follow the
decision in report 219 without pooling historical 217 outcomes into this pair.

## Question

Can fresh external coding agents migrate an existing batch result contract while
preserving full behavior and inherited evidence in retained AgentLang and
conventional F#? This is one bounded pair, not a reliability-rate estimate or a
causal decomposition of types, dictionary, coverage or editing interface.

Use the original unmigrated retained terminal-213 project, fingerprint
`b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611`.
F# receives equivalent nominal IDs, documented/tested monthly/annual renewal
helpers, batch/helper behavior and inherited scenarios. Do not start from migrated
217 source or a stub. Language attachment coverage and repository tests differ;
record suite sizes and review semantic scenarios rather than equating test counts.

## Task and preservation

Change the existing batch from an ordinary business error to a nominal failure
record with zero-based input index, old subscription ID and exact original cause.
The F# seed's typed DomainError record maps native Business errors to the shared
code/message contract before the task; this mapping is frozen, and participant
errors are compared verbatim. In particular, native Business's overlap wording
differs from Flow's. The grader must not normalize that difference after edits.
Process input order; stop at the first failure. Preserve complete successful Store,
list ordering, unrelated entities and the caller's input. Empty lists return the
unchanged Store. Never expose a partial Store as success.

Preserve existing batch/helper identities (names in F#, stable IDs in AgentLang).
Only their result contract and related internal state/helper implementation may
change. Related new types/helpers are allowed. Keep the plan enum, renewal input,
lower-level domain/renewal APIs and unrelated tests/examples unchanged. Preserve
all inherited batch/helper names, fixtures, scenarios, success expectations and
underlying causes. Permit necessary typed failure wrappers and counter/state
adaptations, independently reviewed. Add later-item and first-error regressions.

## Required readiness before freezing

1. Fresh build both CLIs; pin authoritative source, output artifacts and broker.
2. Verify original retained seed fingerprint and isolated copy equality. Validate
   it on the new CLI without changing published definitions.
3. Fresh-build F# domain dependency/seed and pass baseline tests. Independently
   review semantic equivalence and nominal-ID/error-message projection.
4. Run correct migrated controls against the nine cases from 216's independent
   model. Preserve complete Store/input projections and exact cause code/message.
   Execute every case. Wrong index, last-error and changed-message controls must
   discriminate as predicted by the model; setup failure is not observed behavior.
5. Keep participant source tests out of the grader build. Score only an isolated
   copy, record exact response count/exit and unchanged source hashes.
6. Freeze neutral packets, seed/source/runtime hashes, calibrated adapter and
   controls. Record randomized launch order before dispatch. No control solution,
   oracle detail, other-arm project or outcome feedback reaches participants.

## Protocol and outcomes

One fresh Luna/max context per arm, sequential runs, identical 100-exchange and
request/response/timeout limits using Start-SubagentTrialHostV2.ps1. Runtime
operation lists differ with interface; explicitly print each exact allowlist in
its bootstrap. Explain `{"op":"host.close"}` as a host transport command outside
that runtime allowlist. In the language bootstrap, explain same-session staged
record definition, tests and untargeted group publication. Observe terminal status
with Audit-SubagentTrialTerminationV2.ps1. Poll the same live process after an
observation timeout; do not restart or repeat an uncertain mutation.

Report independently: all nine semantic cases; inherited preservation; nominal
failure contract; language library publication/F# local validation; normal
finalization; discovery/current production reuse; exchanges and wire bytes.
Overall acceptance requires source review and normal finalization as well as
behavior. Record assistance or missing receipts rather than repairing endpoints.
Model tokens/context and native performance are unavailable in this protocol.

Security boundary is unchanged: pure subscription model, virtual language I/O,
empty effect grants, no packages, network listener or arbitrary .NET facility.
F# validation runs a fixed local project. Participant broker-only confinement is
prompt-governed, not an OS sandbox. Disclose non-broker use. No CI runs.

## Implementation scope

Reuse existing conventional CLI/dispatcher and frozen model 216. New seed and
small conventional projection adapter live under this directory; operational
copies, runtime receipts and controls live under ignored `.agentlang` storage.
Do not modify the sealed 216/217 archives or pin assumptions. Readiness failures
remain evidence. This plan itself is not a dispatch gate or an efficacy result.

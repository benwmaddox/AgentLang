# 065 — Callable compact context prerequisite

Status: locally verified, 2026-10-06. The completed repeated agent
sequence is archived in [report 064](064-repeat-agent-purpose-review.md).
Its publication passed all 32 checks in a clean checkout; saved
[CI metadata](evidence/064-main-ci.json) and
[validation](evidence/064-main-validation.json) pin `3b8dcd5`.

This bounded change shares parser-verified Flow call references between
`describe` and the existing byte-budgeted `context` entries. Dictionary names
remain query/declaration/test-owner names. Call references are additive metadata,
admitted inside the existing exact data budget; no execution, typing, effects,
IR or persistence semantics change is introduced. The existing resolver moved
unchanged into `FlowParser`; both interfaces use it. Container syntax that
intercepts an ordinary dictionary name receives a null reference and a reason.

The [plan and evaluation boundary](../docs/SELECTIVE-CONTEXT-PLAN.md) separates
this interface prerequisite from aggregate retrieval-budget work and actual
external-agent outcomes. Tests verify root, dotted and case-sensitive names,
reserved prefixes, invalid and intercepted container spellings, and generated
nominal constructors. Runtime acceptance compares context with describe, uses
returned references in a typed `List<Email>` expression, and inspects an effectful
candidate without executing its word or provider. Existing budget tests verify
exact transported JSON bytes, deterministic output, complete-entry omissions
and root-too-small errors with the added metadata included.

A fresh `dotnet build AgentLang.sln --configuration Release` passed with zero
warnings or errors. Focused Discovery passed 106 assertions; language acceptance
passed all 35 groups and 632 assertions. The exact Release gate,
`pwsh -NoProfile -File scripts/Validate.ps1 -ReportPath .agentlang/reports/065-local-validation.json -Configuration Release`,
passed all 32 checks, including the 17-check trial-host verifier. Saved
[local validation](evidence/065-local-validation.json) and
[host detail](evidence/065-local-subagent-host.json) identify parent `3b8dcd5`
with a dirty tree containing this change. Clean committed-source CI for this
milestone is pending publication.

No new agent-efficiency, token, context-window or native-memory result is claimed.
The next step is the separately scoped retrieval-budget host change and matched
fresh-subagent evaluation. Finished repeat artifacts and runtime pins remain
unchanged; the full PRD remains incomplete.

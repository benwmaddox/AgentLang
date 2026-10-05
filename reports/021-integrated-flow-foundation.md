# Integrated Flow foundation milestone

Status: working-tree Release validation passed on 2026-10-05. Tested against
HEAD 998a857 with the implementation changes present; this is not yet committed
CI evidence.

This milestone adds the opt-in typed Flow frontend, lexical Scope semantics,
authored source-origin projection, raw source-site coverage identity,
structured value expectations and evaluation output, bounded runtime values,
legacy parser safety, and bounded external-subagent trial preparation.
Flow still uses the authoritative verified semantic IR and its interpreter.
The default Runtime/protocol authoring frontend remains legacy stack syntax.

The fresh solution Release build passed with zero warnings/errors. All 23
required validation checks passed. Selected results: language 34 groups / 583
assertions; Source 84; Flow 47; IR verifier 73; IR formatting 39; Interpreter 22;
conventional CLI 305; persistence projection 99; matched fixtures 70; bounded
trial host 16; parser-process recovery 5. The task bank's 2,114 assertions
validate artifact shape, not successful execution of its 60 proposed tasks.

The pinned reference comparison passed 314 selected CLI-visible checks across
24 subprocesses against the freshly built candidate. This verifies the
selected historical behavioral contracts, not all programs, Flow/legacy
semantic equivalence, IR format compatibility, or agent productivity. IR
format version 2 is tested separately by the formatter suite.

Commands:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/021-integrated-flow-foundation-validation.json
pwsh -NoProfile -File scripts/Verify-IrParity.ps1 -ReferenceCliDll .agentlang/ir-parity/host-019d754/AgentLang.Cli.dll -CandidateCliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll -EvidencePath .agentlang/reports/021-integrated-ir-parity.json
```

Saved machine-readable evidence is in `reports/evidence/021-*`. Reports 013,
014, 015 and 019 explain the delivered slices and their limits. Required test
projects and process verifiers can no longer be silently skipped when absent.

Next: complete the Flow semantic surface, then integrate durable authored
source, tests/examples, semantic maintenance, snapshots/abort and default
protocol cutover. See `docs/FLOW-SEMANTIC-SURFACE.md`. Controlled agent trials
must wait for equivalent Flow fixtures and stable identity snapshots. Native
arenas and LLVM remain research/follow-on work; no allocator or agent-efficiency
claim follows from this milestone.
## Publication gate

Implementation commit fe55c4c29b70a719ff36699c1dca22685018b8b8 was pushed to
prototype. CI run 37337744955 failed only the bounded host verifier's partial
response assertion; the remaining checks passed. Main has not received this
milestone. The failure detail records exit 124 and overall elapsed 4786 ms,
but the assertion also checks exact partial bytes and request/response state;
elapsed time alone does not establish the cause. The artifact omitted the
referenced per-exchange JSONL trace, so that cause remains unverified.

The failed CI validation and host reports are preserved in
`reports/evidence/021-failed-ci-*`. Repair and fresh validation are required
before merging; local green checks do not supersede this failed gate.

The unchanged verifier was rerun locally after the CI failure and again passed
16 checks. Its preserved partial-response trace records a 413.1464 ms exchange,
confirmed delivery, 11 response bytes, and uncertain execution. That is scoped
local evidence, not a diagnosis of the missing CI assertion conjunct. See
`reports/evidence/023-host-before-repair.json` and the paired JSONL trace.
The workflow now uploads synthetic verifier JSONL traces for future CI failures.
The repaired verifier subsequently passed 17 focused checks and the complete
fresh Release gate passed all 23 required checks, with zero build warnings or
errors. Evidence is saved under `reports/evidence/023-host-repair-*`. This
repairs the local publication gate; committed hosted CI must still pass before
main receives the milestone. Report 023 describes the strengthened dripped
response test and bounded failure evidence.
## Committed CI evidence

CI run [37341736314](https://github.com/benwmaddox/AgentLang/actions/runs/37341736314)
completed successfully for repair revision
146bece225a7e9ca54a4d6e73804df7786e15ea2. Its clean checkout passed all 23
required checks, including 17 host checks. The exact run identity, validation
report and bounded host evidence are saved in `reports/evidence/023-ci-run.json`,
`023-committed-ci-validation.json` and `023-committed-ci-subagent-host.json`.
This closes the failed publication gate for the implementation; complete Flow
semantic coverage, durable integration and default cutover remain required.
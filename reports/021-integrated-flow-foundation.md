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
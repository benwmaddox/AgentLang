# Report 182 evidence audit

Reviewed 2026-10-10 against checkout `b4ad8aea0fcabbb3403b4cfd12dcc3254069b8d6`. This was a read-only evidence review; no participant dispatch, scorer rerun, build, or runtime test was performed. The evidence supports bounded feasibility and the report’s no-comparative-advantage conclusion.

## Archive integrity and inspected entries

For report 179, `reports/evidence/179-preview-repair-and-record-migration/archive.json` records 20,319,144 bytes, 11,064 ZIP entries, and SHA-256 `74feb1911fa3a27b54b5185e4af4a287ccf6ca5e052399f510fa06fb35dbc9b8`. The computed ZIP SHA-256 matches, and the opened ZIP contains 11,064 entries. Its `sourceRevision` is `86d9d0c30ca39ee00078c160cfc9934bc64234fc` (the report 178 calibration commit, an ancestor of the reviewed checkout). Relevant later evidence files are pinned individually in the index.

The following report 179 archive members were read:

- `.agentlang/preview-sequence-179/validation-summary.json`
- `.agentlang/preview-sequence-179/oracle/a1s1.json`, `a1s2.json`, `a2s1.json`, `a2s2.json`, `a3s1.json`, `a3s2.json`, `a4s1.json`, `a4s2.json`, `a5s1.json`, `a5s2.json`, `a6s1.json`, and `a6s2.json`
- `.agentlang/preview-sequence-179/audit-retained-1-stage1.json` and `audit-retained-1-stage2.json`
- `.agentlang/preview-sequence-179/result-retained-1-stage1.md`, `result-retained-1-stage2.md`, `result-reset-1-stage2.md`, `result-retained-2-stage2.md`, `result-reset-2-stage2.md`, `result-fsharp-1-stage2.md`, and `result-fsharp-2-stage2.md`
- `.agentlang/preview-sequence-179/capture-retained-1-stage1.json`, `.agentlang/preview-sequence-179/capture-fsharp-1-stage2.json`, `.agentlang/preview-sequence-179/trace-retained-1-stage1.jsonl`, and `.agentlang/preview-sequence-179/trace-fsharp-1-stage2.jsonl`
- `.agentlang/preview-sequence-179/gate.json`, `.agentlang/preview-sequence-179/phase2/evidence/calibration-summary.json`, `.agentlang/preview-sequence-179/integration-amendment.json`, and `.agentlang/preview-sequence-179/publication-review.json`

Selected member hashes were recomputed from the ZIP and matched the index: `validation-summary.json` (`9ff84598d0f40d441147491133c00f8f3160613f0db0b8583d155ab161d4d228`); `oracle/a1s1.json` (`630b7e3e0063d00dd322dda182b825808c7f43872de7066f6165f0053d8134a6`); `oracle/a1s2.json` (`52c2777c923cbd8f4dc83f36d24aa05aa0b390d368fd4d9c1ecd02cb330643cc`); `oracle/a4s2.json` (`882ec9097e1659fe9112366fec125c0875f586ea86d57f804a2abcd636c1e827`); `oracle/a6s2.json` (`28ea141198ec6fc1d86ef82875d9cc43d8dda95f35560fadb6f59d9056af8168`); both retained-1 audit JSON files; and both retained-1 source-review notes.

For report 178, `reports/evidence/178-preview-defect-control-calibration/archive.json` records 7,324,930 bytes and SHA-256 `5078be91b112da0d308fd83b371e1976de0102fc0e5b08f2905915f5930f54dd`. The computed ZIP hash matches and the ZIP contains 4,568 entries. The following members were read:

- `.agentlang/efficacy-maintenance-178/controls/faulty-reset/control-summary.json` and `.agentlang/efficacy-maintenance-178/controls/faulty-retained/control-summary.json`
- `.agentlang/efficacy-maintenance-178/oracle/flow/reset-correct-pinned/agentlang-runs/20261010T153257Z-01/score.json`
- `.agentlang/efficacy-maintenance-178/oracle/flow/reset-defect-pinned/agentlang-runs/20261010T153345Z-01/score.json`
- `.agentlang/efficacy-maintenance-178/oracle/flow/retained-correct-pinned/agentlang-runs/20261010T153319Z-01/score.json`
- `.agentlang/efficacy-maintenance-178/oracle/flow/retained-defect-pinned/agentlang-runs/20261010T153406Z-01/score.json`
- `.agentlang/efficacy-maintenance-178/oracle/fsharp/fsharp-correct-pinned/run-20261010T153345Z-01/result.json` and `.agentlang/efficacy-maintenance-178/oracle/fsharp/fsharp-defect-pinned/run-20261010T153352Z-01/result.json`

## Findings

All six participant stage-one repair scores are 37/37. Each stage-two original and proposed projection is also 37/37, for 18 passing projections total, with zero setup failures. The tasks specify the exact two-field result type, so report 179 demonstrates repair and typed API migration, not autonomous abstraction or type design. Discovery and reuse are supported by earlier bounded studies summarized in report 177.

The archived validation summary records 12 passing automatic preservation audits, eight AgentLang library-publication stages, four F# stages with successful local validation, and 12 normal terminal finalizations. The inspected source-review notes preserve unrelated definitions and inherited assertions while allowing the specified fixture/assertion updates. The representative captured retained-1 and F# traces both end in `session-end`/`host-close`, with host and runtime exit codes 0. F# validation and AgentLang library publication are distinct outcomes; the corrected report 182 table and roadmap now say so.

The negative controls support the report’s warning that coverage alone does not certify intent. Report 178’s correct controls score 37/37 in retained, reset, and F#; all three injected-fault controls score 29/37 with eight behavioral mismatches. The two 179 migration controls with deliberately matching incorrect tests qualify through the library gate but score 27/37 on the affected projection. The 179 calibration summary records four retained setup failures; these are coordinator setup failures, not participant failures.

The study has two participants per condition in one fixed order, and 33 of 37 cases are reused historical fixtures. Retained and reset are whole-project packages; F# has different seed provenance, suites, and editing operations. Both retained first-stage participants shortened their launch allowlists. Several proposed-state tests share lower-level implementation helpers, and retained-2 and reset-2 use error-to-input fallbacks. The separate oracle reduces dependence on these test weaknesses but does not remove the design confounds. Broker exchanges and durations describe protocol activity; they do not establish tokens, effective context, total agent wall time, runtime throughput, or comparative reliability.

The strongest justified decision is to retain feasibility as the conclusion: agents in all three conditions completed this bounded repair and migration task with behavior and unrelated-evidence preservation, and AgentLang participants passed its publication gate. These outcomes do not show a language-caused or comparative reliability lead. Close this easy sequence; use existing brokers on a different held-out, more demanding maintenance workload when efficacy work resumes, with behavior, preservation, and recovery declared separately. Continue native type and arena conformance as a separate engineering track. Reports 180 and 181 were reviewed only as engineering-report descriptions here; their archives and underlying results were not independently audited in this subtask.

## Audit limits

This audit opened and parsed the listed actual ZIP members and recomputed selected report 179 member hashes. It did not rehash all 11,064 report 179 entries, inspect every participant source diff or preservation note, or rerun the independent scorers/builds. Its preservation and finalization conclusion is bounded by the checked representative artifacts and the archived validation/publication summaries. This is not a security certification.

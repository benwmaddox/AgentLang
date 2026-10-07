# Matched vocabulary-retention preparation

Status: implementation in progress. No model actors have been launched, no new
study has been frozen and no acceptance or efficiency result is claimed.

The prior goal turn made concrete progress: milestone 080 added explicit external
trial termination, passed the clean-source 37-check release gate, saved reports
and raw evidence, and was merged/pushed to private main and prototype at 997019d.
Its remote CI result is observed separately.

The next step tests accepted vocabulary transfer rather than adding runtime
features. [The study plan](../docs/VOCABULARY-RETENTION-TRIAL-PLAN.md) specifies
S01→S07 in Flat, Retained and Reset-rich arms, with two rotated blocks and fresh
Luna/max actors. Both rich arms share identical baseline APIs; only Retained
receives independently accepted S01 output. Failed S01 output is never carried
forward. The compact help text and independent exact-Money oracle are held fixed.

The old preparer requires S06 as S07's immediate predecessor. The old follow-up
verifier is specific to Flat S06/S07. Neither can represent this comparison
unchanged, so new versioned tooling records the actual two-task provenance while
preserving 001/002 artifacts. Global design freezing precedes launches; dynamic
per-launch pins bind accepted predecessor output when it exists.

Preparation and validation results will be appended only after execution. The
full PRD, controlled token/context measurements and 60-task executed evaluation
remain incomplete. This small study has no new conventional arm and cannot prove
general superiority or isolate the effect of shared help.

## Executed preparation controls

Two scratch bootstrap runs passed (31 and 33 checks respectively). The final run
loads Flat with 6 types, no authored words and no tests; rich loads 31 types,
53 words and 151 tests. These are dirty-source candidate controls, not canonical
frozen baselines. Earlier oracle-default and legacy-inventory failures remain in
the evidence directory and were corrected before the successful runs.

Three preparation rejection cases passed: predecessor arguments on an ineligible
cell, missing accepted S01 inputs, and fallback without failed S01 evidence.
Normal preparation and clean per-launch pin acceptance remain pending.

The runtime provenance probe found assembly informational versions bound to
3c34b51 rather than the later report-only HEAD 997019d. Canonical baseline creation
will use a fresh clean build, archive its exact generated identities once, and
retain that build throughout the study. Rebuilding after report commits would
change DLL hashes despite unchanged language semantics.

Independent review identified a launch-order defect: default preparation reports
were written into an unignored tracked directory before the clean freeze gate.
The operational default now uses the ignored trial directory; reports
will be archived after pin creation. This is a tooling defect found before any
actor launch, not an agent-task failure. Final clean end-to-end validation remains
pending.

The review also identified a transfer-integrity gap. The final trace audit now binds the independently verified project hash and acceptance file, and later preparation requires that passing audit before carry-forward or explicit fallback. No actor has been launched while these gates remain incomplete.

Milestone 080 remote CI passed all 37 checks on clean revision 997019d. Its report and downloaded evidence were published to main and prototype at 78cd757. That report-only follow-up does not validate the unfinished 081 tooling; no remote result is claimed for 78cd757 itself.

Archive ignore-rule checks pass: the exact003 frozen baseline subtree can track its .agentlang store and dictionary.agent; live run/acceptance outputs remain ignored. This checks path rules, not nonexistent archive contents. Evidence: evidence/081-archive-ignore-rules.json.

The four preparation/freeze/verification/audit scripts are now source-ready and independently reviewed. Their canonical acceptance, audit, predecessor and full-inventory hash contracts agree. Parser checks pass and their working source bytes already match repository LF rules. The new preflight runner remains under implementation; genuine clean pins, actor acceptance, trace controls and agent outcomes are still pending. Review: evidence/081-independent-review.md.

The finalized preparation default passes an executed rejection control: the exact invalid-predecessor diagnostic is preserved in ignored operational evidence, Git status is unchanged, and R01 actor/run presence is unchanged. No runtime process, frozen pin or actor acceptance is inferred. Wrapper and raw diagnostic are saved in evidence/081-default-preparation-rejection-control.json and evidence/081-default-preparation-rejection-raw.json.

The benchmark completion audit confirms that the 60-task bank is still a specification: all entries are planned/non-executable, its 180 cases remain proposed, and adapters/snapshot pins are pending. Prior fresh-agent studies are separate evidence, not execution of that bank. Protocol bytes/exchanges remain distinct from model tokens, turns and effective context. See evidence/081-benchmark-completion-audit.md. The next step remains the current retention study, followed by reviewed executable task adapters and genuine matched agent runs.

Five completed preflight setup failures are archived with byte-verified raw diagnostics in evidence/081-preflight-raw and indexed by evidence/081-preflight-archive-index.json. They exposed a multi-character path separator, a bootstrap wrapper-path assumption, a null count for the zero-word Flat seed, a nested acceptance-field lookup, and an outdated expected predecessor diagnostic. These are setup failures before model actors, not task outcomes. Candidate controls and genuine frozen controls remain separate; a passing behavior control alone does not establish launch readiness.

Both positive reference implementations passed the independent behavior oracle: S01 10/10 and S07 54/54. They were scratch candidate controls with unfrozen metadata, not model actor outcomes. The same driver then failed while reading the no-op acceptance metadata, so the complete candidate gate remains pending.

The latest candidate run also established both negative behavior controls: missing S01 executes zero oracle cases, while the plausible wrong S07 formula passes its own library tests but fails 12 of 54 independent Money cases. The full driver still stopped on its expected predecessor-rejection text, so candidatePassed and launchReady remain false. Further full candidate execution is deferred until final source is reviewed; the diagnostic fix can be checked independently.

An earlier preflight source snapshot implements separate candidate and frozen-control entries. Its source SHA-256 is 275829aa3645cf321ec81e779982a33b07b17655296b69a2bd5a554061e856b9; parser checks and a final-byte missing-candidate smoke pass, reporting pending/launchReady=false. The focused predecessor correction matches the actual rejection and creates no R04 run or actor copy. Frozen checks require actual committed artifacts, a marked deterministic reference session, independent acceptance and ordered coordinator evidence; positive frozen execution and the full final-source candidate rerun remain pending. Evidence: evidence/081-source-control-275829aa.json and evidence/081-focused-predecessor-control.json.

Final review additionally requires successful candidate cleanup, all candidate checks and no reported failure. A passing deterministic reference reports referenceControlsPassed=true but launchReady=false until its archive and owned-slot cleanup are recorded; UTC times require an explicit UTC suffix. Final source SHA-256 is d1c40e9df0a4907fe17e20c188bccda6f283cf5ea0c7b21bf50a2359477cf05c. Parser and pending-entry smoke pass against those exact bytes (evidence/081-final-source-control.json). The full clean candidate and positive frozen reference remain unexecuted.

Independent final source review found no remaining material cross-script/schema blocker across all five scripts. Their staged bytes match the reviewed working sources, and parser checks pass. This establishes implementation readiness for the clean validation workflow, not passing frozen controls or model outcomes. See evidence/081-independent-review.md. The implementation commit precedes the fresh release build and full final-source candidate execution; publication will include their actual results.

## Clean execution and archive correction

On clean commit 8bff657, the fresh Release validation passed all 37 checks, including the 98-check/30-outcome business-policy preflight. The candidate retention gate passed all 62 checks and verified its owned cleanup; its overall frozen phase remained pending. Canonical BootstrapOnly passed 33 checks. Exact reports, seed states, runtime provenance and raw candidate evidence are byte-verified in evidence/081-clean-controls, indexed by evidence/081-clean-controls-index.json.

Baseline archiving then rejected the Flat seed's physical staging path versus its canonical source path, despite identical bytes. The failed snapshot and diagnostic are retained. The reviewed fix permits only that exact mapping and requires matching staging, seed-state, canonical-file and committed-blob hashes. Rich source checks remain unchanged. Canonical seed dictionaries and generated identities were preserved.

These results precede the archive correction. The correction changes one pinned source artifact, so the final candidate inventory must be rerun on committed corrected source. The runtime bundle remains bound to build commit 8bff657 because runtime source is unchanged. Patched archive/global-freeze/reference controls and actual model actors remain pending; no token or context savings are claimed.

The corrected archive executed successfully from commit 34d04db: all 23 committed source snapshots and both canonical Flat/rich baseline archives were created. The original BootstrapOnly seed identities were retained. Raw evidence now has an explicit recursive Git byte-preservation rule, preventing newline normalization of nested reports and transcripts. The next clean candidate run will bind this corrected source inventory; global freeze and model trials remain pending.

The final patched-source candidate rerun passed all 62 checks on clean commit 6882307dae11543d4d2d7844ee3de0e23ee2568d, with no failed checks and all owned cleanup fields true. Its exact report/raw evidence are archived under evidence/081-clean-controls/patched-candidate and indexed by evidence/081-patched-candidate-index.json. All 81 earlier archived evidence files were also checked against their committed Git blob bytes. Frozen reference controls remain pending and launchReady=false.

# 220 — Typed batch comparison readiness

Status: controls accepted for preparation readiness; no participants dispatched
and no trial freeze approved. This follows the [219 decision](219-efficacy-after-typed-batch-maintenance.md)
to compare fresh retained-language and conventional F# maintenance edits using
the existing brokers, pure subscription model and independent nine-case oracle.

Both CLIs were freshly built. The retained unmigrated 213 seed was copied exactly:
fingerprint `b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611`.
On the new CLI it passes 151 inherited tests with its full fingerprint unchanged.
No migrated 217 candidate supplies the initial seed.

The disposable language control defines, tests and atomically publishes the
typed failure contract in one JSONL session. A fresh process passes 153 tests and
39 example owners. Independent scoring executes/passes all nine full-result
migration cases and all 66 creator regressions. All automated identity, library,
baseline and unchanged-copy gates pass. Independent source review preserves all
six inherited batch/helper scenarios, fixtures, success expectations and cause
assertions; two new regressions check complete typed failures and input preservation.
This is a preparation control, not a participant outcome.

On separate disposable candidates, always-zero-index matches its fault prediction
on 9/9 executed cases while matching correct behavior on 6/9. Last-error-wins
likewise matches its prediction on 9/9 while matching correct behavior on 8/9.
Neither fault is committed. Source baseline and positive control remain unchanged.

The new language adapter retains 216's semantic, preservation, identity, library
and response-completeness gates. It explicitly verifies new source/runtime pins
rather than asserting that the old 213 runtime is still current. The readiness
control snapshot verifies 51 required inputs, including compile sources, five
runtime artifacts, broker files and imported oracle sources. The pin gate rejects
seven malformed/incomplete manifests. This control-only snapshot prohibits dispatch
and is not a freeze of the complete future study.

F# seed and adapter acceptance were initially withheld despite passing a fresh
build/test run and four-control calibration (9/9, 6/9, 8/9 and 4/9 correct matches).
Independent review found missing inherited fixture/assertion details: the third
missing-ID item in the competing-first-error case, missing-old-ID helper branch,
unchanged helper timestamp assertions and the original legacy subscription term.
It also found that day-cell projection could miss sub-day timestamp changes,
known-ID projection could miss extra stored entities, and failure reflection did
not enforce the exact nominal record/cause identity. These blocked freeze; passing
calibration alone does not prove a complete-result checker. Corrections and
targeted negative controls were added, with original attempts retained.
The second review confirms inherited scenarios, precise instants and nominal
contract checks are now present, but found two further gaps: unknown IDs were
discarded during map membership projection, and copied-source hashes were not
rechecked after grading. Both were corrected and checked before acceptance.
Calibration compile failures are retained as preparation evidence;
they are not participant failures or executed semantic mismatches.

The final independent review accepts corrected preparation readiness. The F#
baseline passes eight monthly/annual and six batch/helper scenarios. Its baseline
build/run receipt is explicitly reconstructed from observed output; the original
console output was not saved. Expanded calibration preserves raw build/run logs:
correct behavior passes 9/9; always-zero, last-error and changed-message controls
match their predicted 6/9, 8/9 and 4/9 correct cases. Sub-day timestamp and unknown
customer controls produce behavioral mismatches. Wrong nominal record, old-ID and
cause types fail setup on all five error fixtures; these are not executed semantic
failures. Duplicate, missing, extra and reordered result IDs are rejected, as is
a mutated scoring copy. All real copied/original source hashes remain unchanged.

The [evidence index](evidence/220-typed-batch-comparison-readiness/index.json)
seals text receipts, control sources and failed attempts, excluding generated
binaries/build trees. Every archived member is verified by length and SHA-256.
The [review](evidence/220-typed-batch-comparison-readiness/review.md) distinguishes
preparation acceptance from the unchanged raw score's review-pending fields.
Next generate and verify the complete neutral participant packets, private runtime
pins and recorded launch order before running the fresh pair. No participant
result, comparative efficacy result or full dispatch freeze exists yet.

The seed must carry equivalent tested renewal helpers and six batch/helper
scenarios. Its nominal DomainError record
maps native domain messages to the shared contract before migration, including
the otherwise different overlap wording. This mapping is frozen against participant
edits; the scorer compares actual cause code and message verbatim. It must grade
a copied project with participant tests excluded, and discriminate localization
and changed-message faults before any fresh participant is dispatched.
F# stores entities in maps; subscription ordering is reconstructed by the adapter
from fixture/request IDs rather than measured as native collection order. Disclose
that representation difference when interpreting any later comparison.

The first conventional CLI build failed because the sandbox could not reach the
NuGet advisory feed and NU1900 was treated as an error. Retrying with only NU1900
exempted from warnings-as-errors succeeded; advisory warnings remain visible.
Both builds use single-node MSBuild, no node reuse and disabled workload resolver.
Separate host-side read-only direct/transitive advisory checks on both CLI package
graphs exited zero with no advisories listed. No dependency was changed; this
scoped check does not establish general security safety.

No listener, external I/O grant, arbitrary language host facility or CI run is
added. Language controls use virtual I/O and empty grants. A fixed F# project will
run local validation; broker-only participant confinement remains prompt-governed,
not an OS sandbox. The [plan](../experiments/AgentLang.SubagentTrials/typed-batch-comparison-220/PLAN.md)
requires equivalent unmigrated seeds, adapter calibration, explicit allowlists and
host.close instructions, fresh contexts, recorded randomized order and separate
behavior/preservation/finalization outcomes. No comparative efficacy claim follows
from passing readiness controls.

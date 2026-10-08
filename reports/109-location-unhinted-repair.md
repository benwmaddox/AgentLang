# Location-unhinted repair comparison

Status: completed exploratory comparison, 2026-10-07. All three fresh actors
located and repaired the supplied pricing discrepancy. This supports feasibility,
not comparative reliability superiority.

## Design

After the bounded native work through report 108, return to the primary agent
question: can fresh actors locate and repair a billing defect when the task omits
the exact operation name? Reuse report 102's immutable planted starts, frozen
Flow/1 runtime and existing 54-case independent oracle. No new harness or scorer
was introduced. This does not evaluate Flow/2 syntax or native performance.

Three fresh Luna/max subagents received the same pricing policy and discrepancy
description, with no operation name, implementation fix, or warning that passing
expectations encoded the defect. The public policy specified exact raw ordinal
"premium" matching, final-balance truncation and every signed Int64 balance.
This is location-unhinted repair, not blind discovery of an unspecified defect.

Conditions were retained vocabulary, reset-rich vocabulary and conventional F#.
Language actors used the same JSONL protocol and 100-exchange ceiling; F# used
ordinary project files/tests. Access to other arms, reports, oracle, scorer and
provenance was prohibited by instructions, not OS isolation. The plan, task and
archive provenance were recorded before dispatch. The old unfinished 004 study
and original 003 results remain untouched.

## Results

| Condition | Independent before -> after | Final own tests | Whole-project regression | Actor runtime exchanges |
| --- | --- | --- | --- | --- |
| Retained vocabulary | 42/54 -> 54/54 | 9/9 | 166/166 | 29 |
| Reset-rich vocabulary | 42/54 -> 54/54 | 7/7 | 158/158 | 30 |
| Conventional F# | 42/54 -> 54/54 | 26/26 plus 7 seed checks | Same project runner | Not comparable |

Every start failed the same twelve independent fractional-premium cases while
its own tests passed. Both language actors found the target after listing words,
then inspected its description/source. All final implementations use bounded
signed quotient/remainder arithmetic, avoiding intermediate overflow while
truncating the final 90% balance toward zero. Non-premium balances are unchanged.

Both language outputs retain pure, persistent library Customer-to-Money contracts.
Retained still calls the existing `customer.premium?`; reset compares raw kind
directly. Preservation checks pass for retained's 54 prior functions and 31
nominal types, and reset's 53 functions and 31 types. Own coverage is respectively
34/34 and 36/36 instructions, with 2/2 branch outcomes in each. Fresh-copy
coordinator test-all runs independently confirm the whole-project totals above;
retained also ran test-all itself, while reset ran only its target tests.

F# preserves Domain.fs, the probe program, project, pinned Business DLL and
original seed-test body. It also completes two formerly throwing public helpers,
`isPremium` and `discountBasisPoints`, and uses them in the target. Strict
unrelated-operation preservation is unmet. No incorrect pricing behavior was
demonstrated, but the 54-case target oracle does not independently validate those
helper interfaces. The broader task and unfinished helper topology limit a fair
comparison of edit scope across conditions; this is not a clean reliability win
for the language.

## Tests and recovery

Retained corrected the original named +/-1059 cases to expect +/-953 and added
six cases. Reset kept those test names but replaced their inputs with +/-11,
expecting +/-9, and added four cases. The regular unchanged case remains in both.
Do not describe reset as preserving and correcting the original premium inputs.
Independent acceptance covers the original rounding behavior separately.

Retained test batches were 3/3, 5/9, 5/9, 9/9 and 166/166. The first failed batch
still used the defective implementation and also contained incorrect new boundary
expectations. The second used the correct implementation: all four failures came
from two stale original expectations and two mistaken Int64-boundary literals.
Correcting those expectations yielded 9/9 without another implementation change.
This trace corrects the actor's final claim that its failures were all before the
implementation repair.

Reset batches were 3/3, 5/7, 7/7 and a post-publication 7/7. Its only failed repair
batch contained two incorrect actor-written boundary expectations; actual results
already matched the pricing rule. These are observed recoveries, not new proofs
of adversarial commit-gate enforcement.

All 29 retained and 30 reset protocol responses returned ok=true, including
responses containing failing tests. Count contained test results, not just the
protocol flag. Retained searched for a test name and got no matches before
rewriting cases. That is observed test-discovery friction, not proof that test
source inspection is unavailable; reset shows no equivalent failed search.

## Interpretation

The dictionary interface supports discovering and repairing an unnamed target,
and retained vocabulary was reused without modification. The conventional actor
also repaired the target. One actor per condition, a supplied business contract,
unequal tool interfaces and different fixture/helper topology do not establish a
general correctness, retention or efficiency advantage. No controlled model-token
or latency measurement is available; runtime exchanges cannot be compared with
ordinary file-tool calls.

Full structural coverage qualified the incorrect starting library functions.
Coverage checks which paths executed; it cannot establish that expected values
match the business rule. The incorrect new boundary literals in both language
trials reinforce the need for independent expected values and acceptance tests.
Strong types and library gates remain useful constraints, not proof of policy
correctness.

Next research should prioritize independent behavioral expectations and a
matched, unfamiliar rule/refactor over another run of this same pricing defect.
Use current Flow/2 authoring and equivalent implemented helper topology if that
comparison proceeds. Keep it bounded; do not resume the large deferred study just
to obtain another exploratory result. Native architecture work may continue as
the secondary priority, without treating these results as a comparative win.

## Validation and evidence

The existing retention verifier was run before and after for each language arm
against the pre-defect base vocabulary. It exits 1 because full-study frozen pins
and attached examples are inapplicable/missing here; behavior and preservation
pass separately. Neither R04 nor R06 from the original study is reclassified.
The existing conventional scorer built and ran fresh isolated output, with all
build/test/probe exits 0. Fresh copied language projects were loaded by the frozen
CLI and evaluated with test-all. No language/runtime source changed; no full
runtime rebuild was needed. All ten pinned Release runtime files are unchanged.

Retained host closed through Ctrl+C with host/runtime exit 0 in its trace. The
reset host's session handle was missing during cleanup and no matching host
process remained, but its trace has no session-end record. Clean reset-host
shutdown is unverified; no exit code is invented and no host was restarted.

[Evidence index](evidence/109-location-unhinted-repair/index.json) records the
pre-dispatch plan/task/provenance, exact start/final projects, protocol traces,
before/after scores, fresh regression outputs, reviews and hashes. Conventional
archives exclude generated bin/obj only. Failed language test responses are
retained; no conventional failed-build transcript is available.
Independent read-only reviews checked arithmetic, source preservation and the
original-test correction/replacement distinction. Publication includes this
report and roadmap updates; automatic CI remains disabled.

Archive validation reread all 1,885 entries in seven ZIPs and verified all 42
indexed evidence files. The prior report 102 evidence remains unchanged.

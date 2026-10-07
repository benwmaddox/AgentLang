# Guided defect-repair comparison

Status: completed exploratory comparison, 2026-10-07. All three fresh agents
repaired the behavior. This supports repair feasibility; it does not establish
a reliability advantage over conventional F#.

## Design

One fresh Luna/max external subagent ran each condition: retained vocabulary,
reset rich vocabulary, and conventional F#. The existing R02 defect was seeded
in all three: subtract truncated 10% instead of truncating the final 90% balance.
Each started with the same three passing own expectations: premium 1059 -> 954,
premium -1059 -> -954, and regular 1059 -> 1059. The two premium expectations
intentionally encode the wrong rule. Both language seeds were persistent library
functions with full own instruction and branch coverage.

The public task specified raw ordinal premium matching, final-balance truncation,
the full signed Int64 range, possibly incorrect expectations, and preservation
of unrelated behavior. This is guided repair, not blind defect discovery. The
retained target called a previously created `customer.premium?`; reset compared
raw kind directly. Actors could not read independent acceptance cases. Language
actors used the same protocol and 100-exchange limit; F# used ordinary file edits
and tests. Those access restrictions were instructions, not OS isolation.

The frozen Flow/1 runtime from report 099 kept the language conditions consistent.
This trial does not evaluate Flow/2. Existing independent 54-case acceptance was
reused, and a separate reviewer recomputed every before/after result using
BigInteger signed division and ordinal kind matching.

## Results

| Condition | Independent before -> after | Final own tests | Own instruction / branch coverage | Actor runtime exchanges |
| --- | --- | --- | --- | --- |
| Retained vocabulary | 42/54 -> 54/54 | 6/6 | 34/34; 2/2 | 33 |
| Reset rich vocabulary | 42/54 -> 54/54 | 6/6 | 36/36; 2/2 | 40 |
| Conventional F# | 42/54 -> 54/54 | 9/9, plus 7 seed checks | Not measured | Not comparable |

The same 12 independent cases failed in every planted start. Every final output
passes all 54 cases. All use bounded quotient/remainder arithmetic and preserve
the public Customer-to-Money contract. Both language outputs remain pure,
persistent library functions. Prior vocabulary is preserved: retained 54 words
and 31 nominal types, reset 53 words and 31 types. The retained implementation
continues to call `customer.premium?`.

Fresh-copy whole-project regression runs passed 163/163 language tests for
retained and 157/157 for reset; the starts had 160 and 154 respectively. The
retained actor itself ran test-all; the reset whole-project run was performed
by the coordinator after completion. F# changed only Operations.fs and
SelfTests.fs. Direct hashes confirm unchanged Domain.fs, Program.fs, project and
Business DLL, with unchanged unrelated-operation and seed-test prefixes.

Reset and F# corrected the original +/-1059 expectations to +/-953. Retained
replaced those cases with +/-1065 expecting +/-958, removing the misleading
expectations while retaining fractional-rounding tests. Separate coordinator
evaluations confirm retained also returns 953 for 1059 and -953 for -1059.
Do not describe this as all actors preserving and correcting the original cases.

## Recovery and interpretation

Retained test batches passed 3/3, 6/6 and 163/163. Reset batches were 3/3, 0/6,
4/6 and 6/6: a noncanonical Instant fixture caused the first failed replacement
batch; two mistaken boundary expectations caused the next. Both were corrected
before durable replacement. The runtime protocol returned successful test
responses even for failed tests; count the contained results, not just `ok`.
Neither actor encountered a protocol error during repair.

The reset actor reported difficulty finding original test bodies and recreated
the named cases from observed information. This is a reported discovery problem,
not proof that the runtime lacks test-source inspection. The timestamp friction
also recurred from report 099. Existing test checks exposed these mistakes;
this trial did not attempt to publish a failing replacement, so it is not a
new adversarial demonstration of the commit gate.

Full structural coverage qualified every incorrect starting function. Coverage
establishes executed paths, not agreement with the business specification.
Independent acceptance remains necessary. All conditions repaired the supplied
rule correctly, so the result provides no comparative reliability superiority.
The retained condition used fewer exchanges here, but one actor per condition,
one guided defect and unequal testing choices cannot establish an efficiency
benefit. Do not compare these repair counts directly with report 099's new-feature
counts. No model-token or controlled latency measurement is available.

## Protocol and evidence limits

The original retention verifier was reused for behavior and preservation against
the pre-defect base vocabulary. Its full-study metadata protocol requires frozen
study pins and attached examples which this quick trial does not provide. Both
final `metadataPassed` flags remain false for those reasons. Behavior and
preservation pass separately; no original R04/R06 study result is reclassified.
Repair starts, source and tests are independently archived.

A retained host startup ended before any request. The coordinator verified
terminal process state and a session-end trace before a fresh PTY launch.
After scoring, the coordinator mistakenly requested v2 `host.close` from the
v1 host, producing one denied operation after the 33 actor exchanges. That is
orchestration, not an actor repair error. Both active v1 hosts then terminated
through authorized Ctrl+C; all session-end records report exit 0. Both startup
and final traces are retained, including the coordinator error.

[Raw evidence index](evidence/102-defect-repair/index.json) contains the pre-recorded plan, task,
primer, seed provenance, typed probes, before/after scoring, fresh regressions,
traces and readable sources. ZIP archives preserve exact language projects and
conventional source projects with their pinned Business DLL, excluding only
conventional bin/obj build outputs. Archive-entry and overall SHA-256 indexes
bind the bytes. A read-only independent reviewer confirmed arithmetic,
preservation, tests and the retained-test replacement distinction.

## Next action

The demonstrated discovery/composition/repair capabilities justify a bounded
native backend while reliability research continues, as explicitly authorized
by the user. [Report 103](103-llvm-architecture-and-native-slice.md) records the
accepted LLVM architecture and scalar AOT work in progress. It remains separate
from efficacy evidence and from any future arena/mailbox performance claims.
Archive validation verified all 1,238 project entries against their recorded
SHA-256 hashes. No language/runtime source changed for this research milestone;
validation was the fresh behavior/regression runs and evidence integrity checks.

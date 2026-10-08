# Pair-reminder comparison: interpretation notes

This task-scoped comparison starts from three implementations of the same faulty
pair operation. Every seed passes its own tests and independently scores 10/12.
The seeded tests deliberately contain a faulty expected result, making this a
repair of implementation and specification examples together. It is not blind
bug discovery: the public task explains the broken first-ineligible behavior.
The location and optional helper names are withheld.

All four new target cases are matched input/return assertions. The retained
single-invoice vocabulary is real prior actor output from report 111. Its
inherited tests differ in assertion strength from the conventional helper's
inherited tests; preserve and report that difference. Reset is compact, not a
large distracting dictionary. Two samples per condition cannot establish a
population-level reliability advantage.

The independently enumerated oracle covers complete virtual filesystem state,
ordered results, aggregate read/write counts, repeated calls, aliased inputs,
empty/custom contents, exact status matching in both slots and capability
preflight. The absence of provider-event tracing limits dynamic order claims;
source/IR inspection is a separate piece of evidence.

Negative controls distinguish coverage from assertions. A wrong-path candidate
can be blocked by tests; a content-preserving redundant write can pass value
assertions while violating the effect contract. Only unchanged actor tests may
be used to claim post-solution regression detection. The predispatch committed
Flow wrong-path control changes one expected value solely to expose incorrect
committed behavior to the oracle; it is NOT a claim that original own tests
accepted that mutant.

Preparation evidence limitations: the first failed F# corrected-control score
was overwritten before archive. Its later passing run is preserved and must not
be presented as first-attempt success. The first Flow wrong-path score evaluates
unchanged committed correct source after replacement was refused; label it as a
non-mutant diagnostic, not an oracle miss. Frozen actor traces/results must not
be overwritten. Initial Flow launcher failures happened only during preparation.

No product/runtime code changes or CI runs belong to this comparison milestone.
Local fixture builds, pinned-runtime reloads, frozen independent scoring and
mutation checks are the applicable validation. Model token usage is unavailable;
protocol payload bytes and wall-clock session time are descriptive proxies only.

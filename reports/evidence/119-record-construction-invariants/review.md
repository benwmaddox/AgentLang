# Independent review notes (in progress)

Reviewer: record_validation_review, read-only Luna/max.

Found: persisted Stack record validator bindings could be stripped and silently resolved by current name because missing-target rejection was Flow-only. Runtime owner was asked to reject missing/mismatched stored targets across frontends, retaining legitimate uncommitted Stack staging. Tests pending final review.

Checked: selected candidate type/validator closure iteration pulls validators of transitive generated constructor/accessor types into persistence. Frozen-validator union independently roots every persistent type validator, covering those generated dependency edges.

Checked: native Range conformance tests cover both fields via accessors, valid values/fuel and full false-result diagnostic parity at O0/O2, and exact-program retained-root reconstruction. Native owner reports 455 assertions passing, stable source inventory during build and execution.

Semantic limit: predicates and pure helper calls observe an internally assembled unchecked candidate of the nominal record type. Purity, Bool-only return and no mutable/callback escape keep rejected candidates from ordinary outputs/effects. Predicate/helper code must not assume the invariant already holds. No refinement-as-proof compiler optimization is introduced. This temporary construction scope is documented explicitly.

Root found additional finite-domain edge: a zero-field record is a closed singleton (empty product), not a no-obligation open record. Arbitrary validation makes that domain unproven; Core owner is correcting the finite guard and adding tests.

Flow.Runtime attempts 01-03 were harness expectation mismatches, not fabricated returns: structural rejection precedes finite gate; throw-before-return finite gap must be queried in describe's returns[0].missing; structural error expected list may be empty. Direct finite report passed from run04 onward. Additional unknown-validator/cycle diagnostic assertions were corrected to the actual earliest stage's structured error, without changing stage order.

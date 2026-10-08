# Structural correctness requirements

Approved after the location-unhinted repair comparison. These requirements
complement strong typing, explicit effects, exhaustive matching and library
coverage. They do not make independent business acceptance optional.

## Closed domain states

Use declared closed alternatives for finite internal states, with exhaustive
matching checked before execution for every function. Decode external raw values
at an explicit boundary and return a structured unknown/invalid result when
appropriate. Do not silently normalize external input or change an existing
published domain contract. Validated strings alone are not enum exhaustiveness.
Changing a case set must invalidate affected compiled and qualified definitions.
All representations and backends share the same typed semantic IR contract.

## Arithmetic policy as a discoverable operation

Provide small, documented, checked arithmetic operations that name their rounding
policy explicitly. A nominal Money type protects its unit; it does not establish
discount or rounding policy. Ratio scaling must compute the intended final value
without premature rounding or an overflowing intermediate when the exact final
value is representable. Specify zero denominators, signs, Int64 boundaries and
out-of-range results with structured errors. No implicit Float conversion.

Keep business policy in authored library functions. A trusted arithmetic primitive
may implement a general operation, but must not embed premium-customer rules.
Record independent expected values and backend conformance. Unsupported native
operations must fail explicitly; managed success is not native support.

When validated input bounds make failure impossible, prefer a truthful total
contract rather than forcing callers through an unreachable Result error arm.
For example, a bounded basis-point rate can support total Money scaling. Keep
that domain constraint in the library/type layer. Do not weaken coverage gates
or fabricate impossible error cases merely to qualify a fixed safe policy.

## Pure decisions and narrow effects

Prefer pure, typed decision functions for business choices and a small effectful
executor for state changes. Represent the decision alternatives explicitly and
require exhaustive handling. Declare and enforce the executor's effects. Tests
must establish both the decision and the resulting provider state, including
failure paths and unrelated-state preservation.

Separating a decision from execution does not make a stale observation atomic.
Document the single-thread/turn assumptions, or enforce the necessary check/write
boundary when state can change between planning and execution. Do not promise
exactly-once external delivery from an in-memory decision alone.

## Construction invariants

Validated constructors establish their advertised invariant before a value becomes
available. Preserve nominal identity and prevent unchecked creation paths from
bypassing validation, including record construction, persistence reload and native
entry boundaries. Where an invariant relates multiple fields, validate the complete
value atomically; field-by-field validity is insufficient. Return structured
failures with useful source and type context.

This does not require a general dependent-type or proof system. Prefer small pure
predicates and explicit construction boundaries. Existing raw wrappers without
validation retain their documented semantics until deliberately migrated.

## Properties and independent expectations

Add deterministic property and metamorphic checks beside example tests. Initial
properties should cover repeat/idempotent behavior, preservation of existing and
unrelated state, effect-free ineligible decisions, signed rounding and arithmetic
boundaries. Use independent reference arithmetic where appropriate, with saved
inputs or fixed seeds so failures reproduce. Do not generate expected values by
calling the same implementation being checked.

Property tests and branch/outcome coverage answer different questions. Keep both;
neither replaces independent domain acceptance. A full language-native property
framework is not a prerequisite for exercising these checks in the host test
runners. Report which properties were checked and their tested domain.

Report 111 demonstrates why state/effect assertions are a separate requirement:
an extra-write mutation passed return-value tests and full own coverage, but
failed an independent provider-count check. Add optional authored assertions for
exact effect-category counts scoped to actual invocations of the test's target,
including nested callees and excluding setup and expectation evaluation. Counts
must include attempted provider calls that fault; repeated target invocations
aggregate, and the target must actually run. Preserve pure expectations and
existing tests without the optional assertion. The extra-write control must fail
under the stronger tests. This assertion facility is pending implementation.

## Delivery and evidence

Implement in bounded slices with explicit status for each requirement. Keep the
core compact, expose operations/types through dictionary introspection, and
preserve authoritative semantic IR. Validate interpreter behavior and supported
native backends separately. Existing research fixtures remain frozen; evaluate
new domain designs as named new conditions instead of altering earlier evidence.

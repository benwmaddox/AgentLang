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

Report 118 records a narrow populated-state application: a Flow/2 delivery plan
builder returns a plan after a nonempty FIFO list yields a concrete head, and
its `first` field is `EmailMessage` rather than `Option<EmailMessage>`. The
transition derives that plan from its own Store. This removes an unreachable
empty-head alternative for this contract.

Report 119 adds an optional pure predicate over a complete nominal record.
Verified `MakeRecord` operations freeze the exact validator target; the
interpreter and supported LLVM constructor expose a record only after a true
result, while false returns `RECORD_VALIDATION_FAILED`. Predicates may inspect
the transient unchecked candidate only within their pure validator/helper call
scope. The compiler does not use the predicate as a proof. The raw C allocator
remains trusted internal machinery, not a public language-level construction
path. Storage remains version 3 and native ABI is unchanged. Focused Core/interpreter, native, persistence, expected-error return
observation and business extension checks pass. The aggregate passed 36/37 checks and its remaining inspection-script
repair passed separately; see [report 119](../reports/119-record-construction-invariants.md).

Finite qualification treats validated domains conservatively: a record with
finite projections is unsupported without a proven valid-value set, including
a zero-field record whose raw domain is a closed singleton. An authored
function returning a validated record with Option, Bool or enum projections
cannot currently qualify under that rule, even when the predicate itself is
library-ready. The predicate can observe its completed true and rejected-case
false returns; a target that throws before returning contributes no return
observation. Runtime controls verify this expected-error coverage boundary, including
failed assertions and targets that throw before returning. Fully open shapes with no finite obligations remain observable
without enumeration. Provider-state assertions and broader module/library
dependency-closure and test-local override rules remain pending.

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
under the stronger tests. Report 113 implements this optional Flow/2 assertion
facility, including persisted tests and library replacement enforcement. Reports
114-115 record bounded agent adoption checks. Report 116 implements finite-value
library coverage, and report 117 tests its adoption. Provider-state assertions
and general cross-field construction invariants remain pending.

## Delivery and evidence

Implement in bounded slices with explicit status for each requirement. Keep the
core compact, expose operations/types through dictionary introspection, and
preserve authoritative semantic IR. Validate interpreter behavior and supported
native backends separately. Existing research fixtures remain frozen; evaluate
new domain designs as named new conditions instead of altering earlier evidence.

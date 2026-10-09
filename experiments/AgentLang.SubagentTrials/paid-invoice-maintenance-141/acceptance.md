# Maintenance 141 acceptance and scoring

This procedure is frozen before dispatch. A participant must pass each
applicable dimension; do not collapse a behavior pass into overall acceptance.

## Behavior

Invoke the actor-selected shared operation directly, then invoke both public
summaries for each of the four cases in `oracle.json`. Compare exact Money
minor-unit strings, stable error codes, complete queried-customer fields, and
all seven dashboard counts. Unknown-customer account/helper calls must return
`CUSTOMER_NOT_FOUND` before unrelated aggregation; the all-customer dashboard
must still report the other customer's overflow.

The first two cases are deliberately imported defensive records: one has a
negative payment on a Paid invoice, a positive payment on an Open invoice, a
payment with no invoice record, and another customer's Paid payment; the second
proves that an Open payment is excluded before checked addition. Normal public
payment submission does not create these rows. Report them as defensive
coverage, not normal business data. Paid invoices retain signed checked
addition, so the `-30` amount counts.

## Shared structure

Review saved source and active dependencies after reload. Record the discovered
shared helper name, signature, source file/function identity and call sites.
Confirm:

1. The helper checks customer existence before folding payments.
2. It performs invoice lookup, Paid-status filtering, customer ownership
   filtering, and checked Int64 accumulation in one customer aggregation path.
3. The account summary calls it for its queried customer.
4. The dashboard calls the same helper per stored customer and combines results
   with checked arithmetic and sticky error propagation.
5. The supplied fixture builder appears only in test code and is unchanged.

Do not accept two independent aggregation algorithms wrapped by a common
function. Record residual unused definitions, but do not fail solely because
the broker cannot delete inherited words.

## Preservation and tests

Compare inherited source against the frozen start. Limit production changes to
the shared aggregation and necessary summary integration; preserve all other
functions, types, signatures and record shapes. Confirm the account result
retains the complete original customer, all seven metric counts remain exact,
and no fixture-specific branching exists in production code. Run all inherited
and new tests through the assigned validation command. New tests must exercise
the direct helper and both callers, open and missing invoice filtering, signed
paid addition, overflow and error precedence. Existing library gates remain
unchanged and must be run for Flow target/support functions. After actor
validation, the coordinator runs the independent oracle against a freshly
loaded copy of each saved project; this coordinator reload is not an actor
workflow requirement.

## Finalization and evidence

For Flow, retain the successful `task.commit`, clean `host.close`, terminal
process evidence and zero exit codes. For F#, retain fresh build/test output
and saved source hashes. Preserve raw actor traces, pre/post source inventories,
runtime results, source review and an acceptance matrix. Missing commit/close or
test evidence is recorded as a separate failure even when behavior passes.

## Controls

- **Baseline:** accepted source without the new Paid-status guard must fail the
  first two changed-rule cases. Failure must come from mismatched behavior, not
  an adapter, scorer or build error.
- **Correct control:** only add the status gate before ownership accumulation;
  all oracle cases and existing suites pass.
- **Plausible incorrect control:** add the Paid-status gate and then discard
  signed amounts that are negative. It must fail
  `paid-signed-open-and-missing`; its source is otherwise structurally shared.

Run the same independent cases and scoring path for both arms. Every scorer
process must complete cleanly, return the expected response count and preserve
inputs. A control failure caused by the harness invalidates preflight; repair
and rerun controls before dispatch, then freeze new hashes.

# Preview repair and record migration sequence

Status: all six participant sequences completed after control calibration and
broker smoke checks. This follows checkpoint 178. See
[report 179](../../../reports/179-preview-repair-and-record-migration.md) for
results, preservation review, deviations and limits.

Two fresh external Luna/max participants per condition complete the same two
stages, retaining their own project and conversation between stages. Dispatch
order is retained-1, reset-1, F#-1, F#-2, reset-2, retained-2. Each stage uses one
broker with a 100-exchange allowance; close and observe terminal status before
the next stage. Do not restart a live process after observation timeouts.

Stage one repairs the deliberately tested zero-duration preview shortcut.
Successful preview returns the complete original Store. Cancellation validation
precedes replacement creation; existing errors and precedence are preserved.
Only the deliberately mistaken `zero-period-no-op` assertion may be corrected;
all other inherited tests/examples must remain intact.

Stage two changes preview's output to
`Result<ReplacementPreview, BusinessError>`, where `ReplacementPreview` has
exactly `original: Store` and `proposed: Store`. F# uses `Original`, `Proposed`
and `DomainError`. Original is the unchanged input; proposed is the complete
validated cancellation/replacement state. Preserve error assertions and the
stage-one repair. Existing success assertions may access `original` against the
same expected Store; necessary generic Result annotations may change. Add a
meaningful complete proposed-state assertion. Preserve function identity,
inputs, library maturity, handoff, renewal wrappers and unrelated objects.

Score each stage against the 37 pinned checkpoint-178 cases, checking complete
state and error codes. Stage two checks both original and proposed projections.
All observations must execute with zero setup errors. Positive controls must
pass both projections; swapped-field and proposed-equals-original controls must
fail. These are reused historical fixtures plus four zero-duration combinations,
not an entirely unseen corpus. Error message text is not exhaustively scored.

Assess behavioral acceptance, exact collateral preservation, permitted test
adaptation, qualification, and task finalization separately. File/hash audits
support source review; they do not establish that a changed assertion retains
its meaning. Save every attempt, trace, terminal status and candidate source.

The retained/reset-rich treatment remains the whole checkpoint-171 project
package, including history/tests/documentation; it is not isolated code
retention. The F# seed is checkpoint 178's coordinator-prepared six-input
handoff/preview project, derived from the report-166 preparation seed. F# is a
workflow reference with unequal provenance and suites. Do not infer a population
reliability rate or language advantage from six participants on one sequence.
Authoritative model tokens and fixed effective context limits are unavailable;
record protocol exchanges/bytes separately.

Trials use isolated local projects and the existing restricted JSONL broker,
with no application network/mail effects. This does not certify cybersecurity.
No product API, native ABI, allocator or effect capability is changed by this
study. AI agents remain external developers of the language.

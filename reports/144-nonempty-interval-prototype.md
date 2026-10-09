# 144 — A nonempty interval vocabulary without a larger core

The current interpreter can enforce a nonempty-period invariant using existing
validated records, Option values and library functions. The equivalent F#
private-record design also works. This is a coordinator-built prototype following
report 143's missed empty-interval rule, **not a fresh agent comparison** or evidence
of an AgentLang reliability advantage. No runtime or trusted primitive was added.

## Executable vocabulary

[The example](../examples/nonempty-interval.agent) declares a validated `Interval`
with integer `start` and `finish` endpoints and three documented functions:

- `interval.valid? : Interval -> Bool` requires `start < finish`.
- `interval.try-create : Int Int -> Option<Interval>` returns `none` for equal or
  reversed endpoints, otherwise a validated interval.
- `interval.overlaps? : Interval Interval -> Bool` checks strict half-open overlap.

The generated constructor applies the record predicate and raises
`RECORD_VALIDATION_FAILED` for an empty or reversed candidate. Nominal typing is
static; the value predicate is checked at construction time. This is not a
compile-time proof of arbitrary inequalities. There is no language-level unchecked
record constructor. The consumer can rely on the invariant only if the validator
itself expresses the right rule.

Eight attached tests cover accepted and rejected constructions, both Option tags,
and overlap versus adjacency. All three functions committed as libraries. A fresh
process reloaded the project, passed 8/8 tests, and reported library maturity and
current complete applicable finite coverage. Rejection tests belong to the
validator so its completed false result is counted before constructor rejection.
The raw request files demonstrate the definition and qualification operations.

## Independent bounded checks and F# control

The shared test grid enumerates all endpoint pairs in `[-2, 2]`. It checks 25 safe
constructor outcomes, then all 100 pairs of the ten valid intervals. Expected
occupancy is computed as a set of integer points; expected overlap is nonempty
set intersection, rather than restating the two-comparison implementation.

| Check | AgentLang | F# |
| --- | --- | --- |
| Safe construction and pairwise overlap | 125/125 | 125/125 |
| Direct constructor protection | 25/25: valid constructions accepted, invalid ones rejected | External forged record rejected with FS1093 |
| Fresh attached library suite | 8/8, three qualified functions | Not an equivalent F# library gate |

The F# control uses an immutable private record and public `tryCreate`/`overlaps`
functions. Its implementation module remains responsible for maintaining the
invariant; callers cannot directly construct the representation. The negative
compilation result is specifically inaccessible representation (FS1093), not a
syntax error. Its successful build and run use .NET 9, Release, serial build and
NuGetAudit=false. This avoids the unavailable audit service and does not certify
package security.

The language grid runs against a copy and preserves the original persistent
project. It uses the frozen CLI from report 142, SHA-256
`c2021ae9046865386cb5f4cfb3126e85416b63762ea1966b486778995b75321e`.
This is bounded interpreter evidence; it neither proves every integer case nor
checks native Option lowering, throughput or mailbox memory behavior.

## Limits and next research question

Returning `none` for both reversed and equal endpoints is an intentional low-level
API choice, not the complete business policy. A reversed user request may need an
error, while cancellation at the original start yields no occupied period.
Consumers must preserve that distinction when constructing the domain state.
Wrong predicates, ignored `none` cases, bypassing the abstraction, or incorrect
business tests can still produce wrong behavior.

The example uses integer positions. Adapting it to the existing canonical-UTC
Instant type appears possible with `Instant::value` and `instant::before?`, but
that integration was not run here and does not repair the frozen report-143
submission. Both languages can encode the invariant; the research question is
whether later agents discover and correctly reuse such vocabulary.

A subsequent bounded comparison should give both environments equivalent
validated operations, documentation and examples, and use an unfamiliar consumer
change without naming the target helper in the task. Include a no-retained-helper
condition if claiming a retention benefit. Score independent behavior, actual
call-graph reuse, preserved scope and regression protection separately. No new
compiler or actor-system feature is a prerequisite, and no participant has been
dispatched for that follow-up.

## Retained setup evidence

The initial language probe omitted required Flow/2 match-arm braces; it was
corrected before the successful runs. The initial coordinator F# grid used
incorrect indentation; its source and compiler output are retained separately.
Neither setup error is an agent participant result. The final examples and proof
projects, raw requests/responses, bounded-grid cases, positive/negative build logs
and commands are retained with the evidence index. No repository runtime/compiler
source changed; the full implementation gate was not repeated for this example.

[Proof archive](evidence/144-nonempty-interval-prototype/proof.zip) and
[SHA-256 entry index](evidence/144-nonempty-interval-prototype/proof-index.json):
69 entries, 49,704 bytes, archive SHA-256
`48ece0ff357b0858ed6bc1f546f2028e0be5f90af29a955cf0c64c8e61f09fe0`.
Every entry was verified against the original bytes and its indexed size/hash.
Build outputs and lock files are excluded. The maintained example matches the
validated proof source; historical report-143 submissions remain unchanged.

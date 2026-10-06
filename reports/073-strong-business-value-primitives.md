# Strong-business value prerequisites

2026-10-06. Nine pure trusted primitives now supply the validation and checked
arithmetic needed by the planned strongly typed business vocabulary. The fixed
dictionary grows from 49 to 58 primitives. These additions provide value-level
prerequisites; the full language business fixture and benchmark adapters remain
incomplete.

The [implementation](../src/AgentLang.Core/TrustedValues.fs) has no business
project dependency or host effects. Compiler signatures and descriptions expose
the rules through ordinary introspection, and the fixed IR interpreter executes
the operations. There is no arbitrary .NET invocation surface.

| Primitive | Behavior |
| --- | --- |
| `string.guid-canonical?` | Accepts exact lower-case D-format storage text. |
| `string.guid-normalize` | Accepts the reference's GUID forms and returns canonical text or `INVALID_GUID`. |
| `string.email-address-valid?` | Applies the reference's bounded ASCII segment/label policy. |
| `int.add-checked`, `int.multiply-checked` | Return exact Int64 results or typed `INT_OVERFLOW` errors. |
| `instant.parse-utc` | Parses explicit-zone ISO text and emits canonical UTC text, or `INVALID_INSTANT`. |
| `instant.is-canonical-utc?` | Checks exact seven-digit UTC `+00:00` storage text. |
| `instant.before?` | Compares canonical instants; invalid inputs raise structured `RUNTIME_INVALID_INSTANT` at the source call. |
| `instant.add-days` | Adds integral days to canonical UTC text; invalid/range inputs return typed errors. |

Existing `add` and `multiply` retain their overflow diagnostics. New checked
operations return `Result<Int, String>` so domain words can deliberately map
`INT_OVERFLOW` to `MONEY_OVERFLOW`. GUID/instant normalization returns
`Result<String, String>`. Both success and error paths retain their closed
runtime Result types. Instant day counts are range-checked before conversion
to Double; accepted counts are exactly representable.

The instant input grammar follows the immutable contract's explicit-zone ISO
shape and invariant `DateTimeStyles.None` parsing, rather than accepting local
timezone defaults. Email validation uses a bounded linear scan matching the
reference policy, rather than exposing arbitrary regular expressions. Pure
host helpers reject null inputs; null is not added to the language value model.

## Validation and feedback

Fresh Core builds with zero warnings/errors. The existing IR interpreter suite
passes 31 assertions. The new required
[focused suite](../tests/AgentLang.TrustedValues.Tests/Program.fs) passes seven
groups and 476 assertions; its [actual output](evidence/073-focused-trusted-values.json)
is saved separately from the full gate. It checks:

- GUID normalization against `CustomerId.parse`, and exact canonical storage.
- Email acceptance/rejection against `Email.create`, including 254/255-character,
  malformed-segment, whitespace and non-ASCII boundaries.
- Checked arithmetic against BigInteger and the reference Money operations,
  including signed extremes, zero, negative quantities and overflow.
- Instant parsing against the immutable contract parser, UTC offsets, fractions,
  invalid dates, explicit-zone requirements and invariant culture.
- Canonical comparison, equality, calendar boundaries, date-range overflow and
  huge Int64 day counts.
- All nine primitive signatures/effects/documentation, actual verified-IR
  execution, retained Result types, compile-time operand rejection, diagnostic
  spans, zero host effects and legacy overflow behavior.

Review corrected a source-span name collision and required all offset digits
to be ASCII before parsing. Initial test compilation needed an overload
annotation and explicit handling of diagnostic return values. A later
[focused failure](evidence/073-focused-first-run.json) exposed an invalid
braced compact GUID test vector: the reference rejects it. The success vector
was corrected to a dashed braced GUID; the rejected form remains a negative
reference test. Primitive input rules were not relaxed to make the test pass.

Full Release validation passes all 33 required checks, with zero build
warnings/errors, in [the local gate](evidence/073-local-validation.json). It
builds fresh artifacts and includes the new required suite. The local evidence identifies the dirty
working tree based on `8066875`; clean committed-source CI is separate.

The change is additive to the fixed primitive catalog. IR, storage and
formatting layout files remain unchanged, and their existing conformance,
durability and fresh-process checks pass. No format-version bump is needed.

This milestone does not prove full Store/reference parity, persistent domain
wrapper composition, quantity range alignment, payment/email adapters, or
benchmark-agent gains. The next step is tested nominal domain constructors and
pure Store transitions using these helpers and the existing typed fold, then
executable business-task adapters and paired agents. Memory-model and LLVM work
remain deferred behind that evaluation.

Clean [main CI](evidence/072-main-ci.json) passed all 32 prior checks at
`8066875`; its [artifact](evidence/072-main-validation.json) records the exact
revision with `dirty: false`. That proves the prior paired-trial publication,
not this primitive implementation.

# 180 - Native refined mailbox admission

Status: implemented and locally validated, 2026-10-10.

## Scope

Owning mailbox callbacks now accept the existing Int/String refinements with
frozen pure predicates. They structurally import external values, then validate
active nested payloads before invoking the handler. A temporary admission frame
leaves on both success and failure. Constructors retain their predicate checks.
No new controller API, host effect, ownership rule or ABI version is introduced.
Stack/mailbox/module ABI remain 1; layout ABI remains 3.

RETURN re-imports retained roots and validates them again. KEEP_ASSOCIATED uses
the controller's private, previously admitted roots without replaying those
predicates. Completion remains plain String. Direct callback tests exercise
untrusted raw slices separately from real text-controller lifecycle tests.

## Acceptance and observed checks

The source review found no concrete blocker in ordering, cleanup, frozen
identity, unsupported forms or metadata bounds. Callback-local diagnostics carry
the lifecycle role; execution errors inside shared predicates retain global
diagnostic IDs, as existing shared functions do.

| Check | Observed status |
| --- | --- |
| Focused refined Int / String | 80 / 55 assertions passed |
| Focused raw mailbox admission | 5 assertions passed |
| Independent native runner | 1,424 checks passed |
| Native value-stack gate | 47 checks passed; source hashes stable |
| Complete LLVM | 655 assertions passed |
| Refined mailbox lifecycle / existing regression | 2,255 checks passed; 668 refined checks; 28 source pins stable |
| Mailbox policy | 741 checks passed |
| Existing local I/O | Passed; 207,190 verifier checks after sandbox socket denial |
| Full local Release | All 37 checks passed after temporary-directory correction |

The independent runner's new mailbox checks verify frozen predicate metadata,
nominal layouts, callback signatures and TypeIds at O0/O2. They are not a
substitute for actual callback admission and lifecycle execution.
The local I/O count includes repeated stress assertions; it does not represent
207,190 distinct semantic cases. All validation was local; no CI run was used.

## Independent lifecycle expectations

The new fixture carries PositiveId and NonEmptyString through records and active
Option/Result payloads. Expected serialized roots and TypeIds are derived before
native execution. Initialize A, begin B, reject empty and FAIL resumptions, then
retry the same token with C. Failure must preserve logical roots, raw retained
bytes and the pending token. Boundary-preflight failure, such as a wrong input
type index, preserves output sentinels. Later value-layout scanning and predicate
failures leave output descriptors invalid under the existing ABI.

Source/verified-IR expectations distinguish normal predicate execution from
early failure. The Int predicate costs twelve instructions normally and four
before Int64.MinValue overflow; the String predicate costs five. Measured
steps are compared against expected RETURN/KEEP sequences. Predicate
invocation totals are derived expectations, not runtime counter observations.
Copy/import/publication and physical movement are checked separately.

The expected controller step sequences are RETURN `[44, 77, 84, 63, 104]`
(372 total) and KEEP_ASSOCIATED `[44, 77, 33, 12, 53]` (219 total). Re-admission
accounts for the 51-step difference on each resume. Source-derived copy
accounting expects 232 deep-copy bytes and zero payload-movement bytes under
either policy. These are fixture-specific conformance counts, not throughput
measurements.

## Retained attempts and limits

Focused test preparation encountered F# build errors and incorrect counter
assertions. The original zero-copy assumption omitted an eight-byte handler
record construction. A subsequent movement formula mistakenly counted metadata
descriptor transfers as payload movement. Source inspection corrected this:
the raw Int callback has deep-copy 8, movement 0 and external import 24 bytes;
the String case has deep-copy 8, movement 0 and external import 40 bytes.
These construction copies are separate from admission traversal.

Pre-run review also corrected an Option control using the wrong scalar type and
the overflow predicate's failing instruction prefix. It distinguished early
boundary preflight from later malformed-value scanning: bad sum tags invalidate
outputs after preflight, while wrong input type indexes preserve sentinels.
These corrections preceded native execution. The later oracle errors below
were exposed by the first run and corrected using independent source audits.

The first native matrix attempt stopped during the O0 diagnostic verifier:
three expected diagnostic names used INVALID_REQUEST, which names the native
status class rather than the emitted language diagnostic. Source inspection
independently identifies OWNING_STACK_INPUT_INVALID for malformed-value scanning
and OWNING_MAILBOX_INPUT_INVALID for early boundary metadata rejection. The
verifier also raised a PowerShell collection Count error because an empty
expected-root array collapsed to null. Both snapshot comparers now normalize
empty collections without dropping assertions. The exact original fixture,
C runner, verifier and failed
result are retained. Any revised diagnostic oracle is a post-execution
correction, not a wholly pre-native freeze. The preserved gate snapshot confirms
the runner used the fixture's exact invalid tag value 2; its earlier draft used
another invalid value, corrected before execution.

After the Count repair, replay exposed another oracle error: the original
184-byte lifecycle copy total omitted the four-character `"FAIL"` literal
constructed by each resume attempt. Two independent source audits found the
constant-emission and runtime counter sites. Each aligned literal occupies
16 bytes, including on the two failed attempts: `56 + 72 + 16 + 16 + 72 = 232`.
The revised fixture discloses this second post-execution correction and retains
the original and intermediate hashes. Value, TypeId, layout and step
expectations did not change. The revised whole fixture is explicitly not
pre-native frozen. A saved-result replay passed 108 checks, and targeted source
review cleared the repairs. A fresh full matrix then passed: O0/O2 with
diagnostic, fast-reset and trusted-generated host profiles, each exercising
RETURN and KEEP_ASSOCIATED. It checks exact root bytes, steps, copy/import/
publication accounting, output contracts, failure preservation, retry and
final lease cleanup. The revised oracle is acceptance evidence only with these
disclosed corrections; it is not an entirely pre-execution prediction.

The first full local gate encountered STORAGE_ACCESS_DENIED while persisting a
project in the default temporary directory. The exact failing test passed with
TEMP/TMP inside the writable workspace: 6,325 assertions, including persistence
and reload. The failed broad run was terminated and a fresh full run started
with that setting. No language permission or effect rule changed. One fixture
punctuation cleanup followed its initial freeze signal; only the new Flow
resource changed. The final full run starts after that cleanup, and its copied
resource hash matches the final source. The sandbox also denied the existing
local I/O regression's loopback connection (Winsock 10013); a fresh host-side
rerun passed. The sandbox denial remains a failed attempt in the evidence.

This is native semantic conformance. It does not establish a security
certification, comparative agent advantage, service throughput, general Email
validation, JIT support or a final memory-policy selection. Unsupported
Bool/Float refinements, unvalidated String wrappers and record predicates remain
explicit. Dependency vulnerability auditing is skipped in the local correctness
gate; this is not a dependency-security result.

## Evidence

Accepted runs: native value-stack `380f97853cc4458a8ac73f6343d7d7dd`, mailbox
`d9c8bbea5d724c12885e7aa9045b5a9a`, policy
`1b6ceb4877ab4847a2b3b7910f6fcfe5`, local I/O
`e7599f4f57de49e6bc4f33a136a65521`, and `full-validation-002`.
The first mailbox run `75f4c94e30e4437590e62b206de67b28` is retained as failed
evidence. The corrected fixture SHA-256 is
`a22bb448ee864713903d5ee7946c785cd7c8978cf54da02e0f2e0b86911cbbb1`.

The [verified evidence archive](evidence/180-native-refined-mailbox/evidence.zip)
and [file/hash index](evidence/180-native-refined-mailbox/archive.json) contain
source pins, gate reports and logs, independent review, original/intermediate
oracles and selected generated modules. Binaries and environment dumps are
excluded. Archive verification checks every entry's bytes and SHA-256 plus ZIP
CRC. This report and the roadmap ship with the milestone commit on `main`.

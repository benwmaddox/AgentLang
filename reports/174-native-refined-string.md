# 174 - Native validated String values

Status: completed and locally validated, 2026-10-10. This is a bounded native
backend milestone, not an agent-efficacy or service-throughput result.

## Scope

Extend the ordinary owning-native backend to String-backed nominal values with
a frozen pure String -> Bool validator. NonEmptyString is the bounded example.
The verified semantic IR remains authoritative; predicates retain exact target
identity, revision and declared/inferred effects. No arbitrary .NET call surface
or new external effect primitive is introduced.

Constructor validation and raw external entry admission must reject invalid
values before the function body or retained output publication. Records and
active Option/Result payloads recurse; inactive alternatives do not execute the
validator. Host codecs require the exact nominal name and String payload.

The physical representation reuses dynamic String kind 5: an eight-byte header,
UTF-16LE code units and padding to an eight-byte extent. Nominal TypeIds remain
distinct. Wrap/unwrap retag descriptors without copying payload and preserve the
full owner range. Layout schema 3, stack ABI 1 and module ABI 1 are unchanged.

## Acceptance

Focused O0/O2 tests and a separate native conformance fixture cover exact bytes,
nominal identity, frozen revisions, raw entry rejection, nested admission,
inactive alternatives, validator execution failures and ownership accounting.
Failure checks read actual retained bytes independently of metric counters.
Fresh LLVM, native value-stack, mailbox regression and full local validation
all pass. The full gate used `scripts/Validate.ps1 -Configuration Release
-SerialBuild -SkipPackageAudit`; dependency vulnerability auditing was skipped
for this local correctness run.

## Local results observed

The final isolated focused String suite passes 50 assertions, and the focused
Int regression suite passes 75. The complete LLVM suite passes 645 assertions
after a fresh serial Release build with zero warnings or errors.

| Gate | Result |
| --- | --- |
| Focused String / Int | 50 / 75 assertions passed |
| Complete LLVM | 645 assertions passed |
| Native value stack | 47 checks passed; 47 stable source pins |
| New String cases | 58 passed across O0 and O2 |
| Mailbox regression | 1,587 checks passed |
| Full local validation | 37 checks passed, zero failures |

The final policy preflight independently passes 98 checks over 30 isolated
control outcomes. The native runner also retains passing Int and refined-Int
regressions and 44 arena-lifetime assertions. ABI/layout fixtures and source
pins agree; no runtime ABI version changed.

## Evidence

The [archive index](evidence/174-native-refined-string/archive.json) records exact
source pins, completed gate outputs, failed attempts and selected generated IR.
The [verified archive](evidence/174-native-refined-string/evidence.zip) is
3,725,915 bytes; SHA-256:
`65c517e1b43dec8ecf85b9723630e2dddc039ed4b9ad40f5807f589fb6707c53`.
Every entry was read back and checked against its recorded byte count and hash.
Build trees, binaries and environment dumps are excluded. Validation ran from
base commit `b1258d3018b730219e4c916ab6ff444840aed920` with the final milestone
sources pinned in the archive.

The independent runner defines 29 cases for each optimization level, including
supplementary Unicode and embedded-NUL nominal round trips. Its first gate
failed to compile the new runner helper; the second compiled successfully but
failed while reading a fixture property from the wrong JSON object. Those
attempts are retained. The third gate executed all 58 new cases: its only two
failures were the
constructor diagnostic-parity helper requiring an absent interpreter source
span although both backends and the fixture provide the same span. The helper
now compares spans when present and permits native span addition only when the
interpreter lacks a span. The fourth fresh gate passes all 47 checks, with 47
stable source pins and all 58 new cases passing. None of the failed attempts is
accepted conformance.

Review corrected the raw failure check to read the native retained buffer
independently of the copy counter, and corrected the inactive Result fixture
to include a complete eight-byte tag plus eight-byte Int payload. An owner
trace pins the base String TypeId after unwrap, rather than the nominal TypeId.
Earlier test syntax failures and diagnostic expectation updates are also
retained. The first complete LLVM attempt failed on an obsolete diagnostic
expectation; its fresh replacement passes the full suite.

## Limits

Refined mailbox layouts, unvalidated String wrappers, Bool/Float refinements,
record predicates and general Email validators remain outside this slice. The
bounded predicate example establishes a nonempty String rule, not full email
validation. There is no JIT, new compaction or new rewind rule in this change.
All inputs and effects used for validation are local and synthetic. These tests
are language/runtime conformance evidence, not a security audit.

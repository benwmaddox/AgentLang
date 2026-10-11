# Native Bool admission review

Read-only source review for report194. No source changes, tests, builds, or runtime
execution were performed. This is a focused implementation review, not an exploit
finding or security certification.

## Source pins

Checkout HEAD: c1bb93b4059826d316ea11bab5a68861b169139a.

The report194 evidence index records C runtime SHA-256
4ccdfa232bc1448a9953e82edb1a68e3d2152bfc3ef5c6c1b4a2284628c05627,
OwningStackAot.fs SHA-256
408482ab19135cc893b0f6cb4be8d0a89643a2645038189aaa1d68d16d7a4fee, and
Program.fs SHA-256
2fabba20fba1becbe985409fd271440245aabd4de91d55ab2452ec6397870e4c.
The inspected files match those pins. The native C test source at this checkout
has SHA-256
c9034d10daa99ae8ec5dd854d1b55c029612551d4e105151f25c086b8036676d.

## Findings

1. **Raw Bool scan checks readability, not canonical encoding.** In
   al_owning_validate_type_descriptor, C lines 700-708, Bool already requires no
   fields or dynamic layout and fixed/minimum payload and extent of eight bytes.
   In al_owning_scan_value, lines 1200-1207, the Bool/I64/Unit branch calls
   al_owning_scan_readable_in_owner for eight bytes and then returns sizes without
   reading the Bool. Consequently every readable eight-byte Bool bit pattern
   currently passes this scanner. Keep the layout check and, in the Bool-specific
   scan path, decode little-endian only after the existing readability check;
   accept exactly 0 or 1. The same path covers primitive and nominal Bool because
   both use the Bool descriptor kind. The generated validator-result path already
   demonstrates the 0-or-1 distinction after bounds/type checks in
   OwningStackAot.fs lines 3114-3137.

2. **The scanner's current bounds ordering supports a narrow leaf check.**
   al_owning_scan_readable (C lines 550-572) checks source length and, for stack
   values, capacity, cursor and initialization before use. Its owner-bounded
   wrapper at lines 574-592 also requires the requested bytes to fit the current
   owner. al_owning_scan_value validates aligned offset and owner end before
   dispatch (lines 1109-1114). A Bool encoding check must remain after that
   eight-byte guard, including on recursive calls; it must not inspect the first
   byte as a shortcut for the full value.

3. **Nested admission already follows active structure.** Option/Result tags are
   read only after an eight-byte owner-bounded check and values above one reject
   (C lines 1116-1133). Only the selected payload is recursively scanned (lines
   1144-1179). Record scanning verifies each field offset/range before descending
   (lines 1231-1305). Put the Bool canonical check in the leaf scanner so it
   applies to a record field, Option Some, and the selected Result Ok/Error child,
   while leaving inactive alternatives unexamined. Do not move this validation to
   descriptor-graph validation, which has no payload bytes.

4. **The scanner has success-only result publication; mailbox failure has an
   explicit invalid-output marker.** al_owning_measure_external_value assigns its
   payload and extent outputs only after the whole recursive scan succeeds (C
   lines 1424-1457); al_owning_measure_value similarly publishes the size only
   after success (lines 1386-1421). The mailbox emitter measures every external
   input before reserving and copying it (OwningStackAot.fs lines 4260-4311), so
   a malformed Bool should fail before import or entry-body execution. After
   structural preflight, however, the callback deliberately sets output slices to
   the invalid sentinel (type index UINT32_MAX, zero offsets) before later work
   (lines 4245-4254), and writes valid descriptors only after all output checks
   (lines 4388-4398). Tests should preserve this established failure marker and
   assert that no valid output or committed state is published; do not require
   mailbox output metadata to remain byte-for-byte unchanged after successful
   preflight. For associated resume, retained roots are remeasured before the
   completion is appended (lines 4471-4503, 4532-4548); a malformed retained
   Bool should fail before append/body work and leave the parked root bytes and
   pending state intact.

5. **Bool nominal identity is an AOT table concern, not encoded in the payload.**
   Primitive Bool has TypeId 2; nominal IDs are separately assigned from the
   ordered nominal table starting at 4 (OwningStackAot.fs lines 615-624).
   Descriptor emission writes kind, TypeId, fixed/minimum sizes and case count
   into the existing layout-3 row (lines 2820-2838). The nominal scalar kind
   mapping currently accepts Int and String then rejects every other scalar
   (lines 2729-2745); the host measure/encode branches likewise accept only
   nominal Int/String values (lines 1333-1345 and 1452-1467). Add the Bool
   mapping using kind 2 and an exact-name BoolValue measure/encode case, keeping
   each nominal TypeId distinct from primitive Bool and peer wrappers. The C
   scanner takes an expected type index and checks the layout kind/shape; it does
   not compare nominal TypeIds while scanning raw bytes. Therefore test identity
   through emitted descriptor rows and typed host/API admission, not by expecting
   a raw Bool payload to carry a nominal name. No layout or ABI change is needed.

## Coverage and focused gaps

- Already covered: the native test descriptor table includes one Bool kind-2,
  8-byte layout (owning_stack_runtime_test.c lines 118-122), and primitive slot
  storage writes/reads the value 1 (lines 483-505). Those are layout/storage
  checks, not scanner admission: AL_TEST_LAYOUT_BOOL is not used by the scanner
  test calls.
- Already covered: malformed external String length/padding rejects; the
  external measure outputs retain sentinel values on a failure
  (owning_stack_runtime_test.c lines 1760-1807 and helper lines 2109-2122).
  The Option/Result scanner tests cover valid nested String payloads, bad tags,
  short extents and malformed active nested String data (lines 2141-2325).
  These provide useful recursive/bounds patterns but contain no Bool child.
- Already covered: nested refined String records, Option Some and active Result
  cases are exercised at O0/O2 with retained-output sentinels preserved on
  validator failure (Program.fs lines 2201-2216). The raw mailbox String failure
  test asserts the invalid output descriptor marker (lines 2580-2584); mailbox
  Option/Result tests distinguish active and inactive Int refinement validators
  (lines 2631-2670). These test semantic String/Int validators, not malformed
  Bool encodings.
- Existing Bool tests are negative capability assertions that must be updated
  deliberately: unvalidated BoolTag rejection at Program.fs lines 1382-1390,
  refined BoolTag rejection at lines 1833-1846 and 2280-2288. Preserve the
  neighboring Float and unsupported-validator negatives.

Add focused negative controls:

1. Direct native scanner tests for primitive Bool: valid little-endian zero and
   one; reject 2, a high-byte-only noncanonical value, the signed high-bit
   pattern, and all ones. For short lengths 0-7 and a nonzero aligned source
   offset with fewer than eight remaining bytes, require failure before a load,
   unchanged output canaries, unchanged stack bytes/cursor, and the expected
   external-versus-stack status. Also mutate each Bool descriptor size field or
   add a field to confirm the existing layout-3 shape checks reject it.
2. Direct scanner fixtures for a Bool field in a record, Option Some, Result Ok
   and Result Error. Put noncanonical bytes in each active child and require
   rejection; put those bytes in inactive alternatives and require they remain
   unexamined. Include a child extent that stops one byte short at the owner
   boundary.
3. Compiled O0/O2 cases with two nominal Bool wrappers: verify kind 2, exact
   eight-byte payload/extent, TypeIds distinct from primitive Bool and each
   other, exact-name host round trips, and wrong-wrapper admission rejection.
4. Raw mailbox negative cases that bypass host Bool encoding: malformed direct
   Bool and nested active Bool inputs fail before the callback body and leave
   only the established invalid output descriptors. For retained-root
   remeasurement, confirm bad active Bool fails before completion append/body
   execution and preserves committed root/pending state. A high-level bool
   wrapper alone cannot exercise malformed bytes because encodeValues writes
   only canonical 0/1.

These checks stay within layout schema 3, stack ABI 1, mailbox ABI 1 and module
ABI 1. Review scope was limited to source inspection; no implementation or
runtime validation is claimed.

# Native dispatch review — in progress

The preceding user-summary turn supplied no new implementation evidence; this
continuation re-inspected the canonical working tree and active workers.

## Compiler review

Independent read-only architecture review found no blocking compiler defect in
same-program provenance, deterministic entry ordering, type-universe agreement,
pre-tool rejection, or legacy exports. Native diagnostics expose code, location
and arguments, while richer message/expected/actual data live in the manifest.
The module fingerprint is semantic/IR identity, not a full binary build identity.
Record source/toolchain/binary hashes separately. Integration must exercise
nonempty typed entries, beyond focused zero-input module tests.

## Runtime findings sent for correction

1. Arena generations were fixed per bank and reused after reset. Assign checked
   fresh generations per invocation, reject exhaustion before mutating live state,
   and demonstrate stale-handle rejection after physical reuse.
2. Message signatures checked Int storage kind but could accept nominal Int-backed
   types. Bind only canonical Int, preserving refinement construction boundaries.
3. Successful bank swaps left the previous graph populated. Clear old state only
   after successful replacement; failed turns retain the active graph and token.
4. Arena reserved-byte categories excluded descriptors. Include descriptors in
   arena categories and subtract them from controller accounting, with no double
   count, to agree with the independently fixed physical-storage oracle.

These are review findings, not yet verified fixes or completed validation.
Historical execution ABI fixtures were checked against HEAD content and their
checkout-byte SHA256 values saved in historical-abi-baseline.json.

## Follow-up review and corrections

The compiler explicit JSON encoder now includes every semantic field; separate
binary/source hashes identify build artifacts. Fresh native conformance reports
476 assertions. This evidence is recorded by the compiler worker, not a second
independent execution of its command by the reviewer.

Additional controller review found caller buffer overlap could corrupt runtime
state before a failure. Known runtime-storage, module-output and pairwise buffer
overlap now rejects before mutation. The same rule applies to post-disposal
stats, with the module required to remain loaded through the last API call.
An unrelated zero-output module entry no longer invalidates otherwise correct
mailbox roles. Selected handler signatures remain strict.

Independent final source review found no remaining blocking issue. Reviewed
runtime hashes match runtime-results.json: C 2c4fa4c25bb113b13f319f15dce7bc35f8d380196058b8b4bf9dc3700336c4ef;
header dc15e12b852e0ed5c9e0075af4bfdcd3863d6fe95d16d90a37520cd054744334;
platform 97ca4a9fdca6f5fdb2a7347272158cfcd722da62adb4737f0bb207a5577685d9;
test 35c2f2f55109cc3499b799cd04f695c962264e4da42b79fad42221f911e5e41a.
Direct C runs report 69 named passing checks each at O0, O2 and UBSan-trap O1.
Standalone compiled-handler integration remains a separate acceptance gate.

The aggregate Release command uses ordinary build output directories, not
isolated ones. Frozen report125 runtime files are separate copies; a read-only
hash check confirms all22 still match. The aggregate gate began before the final
compiler manifest encoder, which is covered by the later fresh complete native
conformance run. No Core/interpreter source changed during that aggregate run.

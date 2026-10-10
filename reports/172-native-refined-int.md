# Native refined integers in the owning value stack

Engineering checkpoint complete: fresh LLVM, native, mailbox and full local Release gates pass.
This is not a security-audit result. Report 171 remains the latest published agent study;
this backend change adds no new agent-efficacy observation.

## Scope

The selected dynamic owning backend is being extended from nominal Int tags to
Int-backed refinements with a pure, frozen Int-to-Bool validator. PositiveId is
the bounded acceptance example. Both generated construction and raw host entry
must enforce the predicate; an exact nominal name alone does not prove validity.
Host validation walks active nested record, Option and Result payloads before
the entry body runs or output is published. Trusted immutable interpreter roots
retain their existing validation boundary.

Validators bind to the verified semantic IR's exact target identity, revision,
signature and effects. Discovery includes validator-only dependencies and
unsupported types in untaken branches or inactive type alternatives. False
returns become REFINEMENT_FAILED; execution errors inside a validator keep
their runtime error classification. Missing or ambiguous generated constructor
metadata is rejected during preflight with a structured diagnostic.

The eight-byte Int payload, nominal TypeId and enclosing owner provenance stay
intact. Wrap and unwrap retag descriptors without copying payload. This slice
introduces no compaction, additional rewind policy or runtime ownership checks.
ABI/schema 3, stack ABI 1 and module ABI 1 are unchanged. Refined mailbox layouts
remain explicitly unsupported until their separate external ingress path is
covered. String/Email refinements, other base types and JIT are later work.

## Validation plan and limits

Fresh local validation must cover O0/O2 constructor results, exact host identity,
active nested inputs, validator failures, failure cleanup and unchanged caller
output, frozen snapshots, call-depth and instruction-budget boundaries. Run the
independent native value-stack gate, applicable mailbox regressions and the full
local Release gate after the focused LLVM suite passes. CI remains manual-only.

Source review already found and corrected an uncontrolled missing-constructor
failure and a dependence of depth behavior on optional source metadata. Semantic
invocation mode must be explicit even when release builds omit debug locations.
Review also identified the missing entry-frame depth offset during raw-input
prevalidation. A scoped synthetic frame now models that offset and leaves on
both success and validation failure before normal cleanup. Direct permitted
and rejected depth tests pass in the focused suite. The temporary frame contributes trace
and frame-return metrics; it does not allocate an arena payload.

The first freshly built LLVM suite stopped in the new exact-fuel test because
its 4,096-byte arena was too small (the next allocation required 4,104 bytes).
The test capacity was increased to 131,072 bytes so memory capacity does
not mask the instruction-budget boundary. No rewind policy was changed for
this setup correction. The independent fixture inventory requires exactly 19
PositiveId cases per optimization, including subchecks for wrong host types.
A hardcoded refined owner-range claim was removed during review; measured
owner-range regression coverage remains in the existing nominal aggregate tests.

The next focused attempt reached an effectful-validator setup and stopped
because the compiler already rejects that validator with TYPE_VALIDATOR_EFFECT.
The assertion now targets this earlier compilation boundary, rather than
expecting an invalid validator to reach owning-backend preflight.

Additional pre-gate attempts caught scope-placement errors in the emitter's
synthetic-frame variables and invalid input/depth setup in the reference test.
Those logs are retained. The independent runner's first isolated build also
stopped on F# type-inference errors in its new helper; no conformance cases ran
in that attempt. Expected values and case inventory are not changed to resolve
these build failures.

Diagnostic equivalence has limits. The interpreter omits division error spans
where the owning backend supplies an instruction span. Constructor-to-validator
depth failures use an interpreter definition span, while the owning IR currently
has only the constructor call-site span. Native arithmetic-overflow operand
details also differ from the interpreter. Constructor-depth spans and independent
division fixtures pin their respective backend behavior. Focused arithmetic
tests check error classification, operation name and cleanup, without asserting
operand-detail parity. Do not describe diagnostics as fully identical.

## Accepted results

The final focused serial Release build passes with zero warnings and errors.
The owning-refined-Int selector passes 75 assertions across O0/O2, including raw
depth boundaries, the 10,000/10,001 instruction threshold and re-execution of
the old frozen handle after a same-name validator replacement. The independent
runner also builds in an isolated fresh artifact directory with zero warnings
and errors after its type annotations are corrected.

The complete LLVM suite also passes all 595 assertions after a fresh isolated
Release build with zero warnings and errors. The independent native gate passes
all 44 checks with 46 unchanged source inputs. Its complete case inventory passes
38 PositiveId cases and 92 nominal-Int regressions across O0/O2. These results
use the final frozen source and corrected independently derived aligned fixture.
The fresh full local Release gate passes all 37 checks with zero failures,
including 98 business-policy preflight checks across 30 independent outcomes.
Failed attempts are retained separately from accepted runs.

The mailbox gate has completed successfully: all 1,587 checks pass and its
25 source inputs remain unchanged. The initial full gate encountered storage
access failures in Windows' sandbox temporary directory. Repeating its exact
language-acceptance command with TEMP/TMP under the repository passes all 35
groups and 642 assertions. The original full run completed all 37 checks with
nine failures involving sandbox temporary storage; its business-policy preflight
passed 98 checks across 30 independent outcomes. The fresh full run with workspace scratch completed successfully with all 37
checks passing. This changes test environment placement rather than filesystem
permissions or language behavior.

The first native gate terminated while serializing its final evidence:
PositiveId diagnostic details retained JsonElements after their fixture
JsonDocument was disposed. The runner now clones those retained diagnostics.
The failed gate and source pins are preserved; the final fresh native gate
passes. This evidence-writer correction changes no expected values
or language execution semantics; the failed run is not accepted conformance.

The next native gate saved its failure evidence: PositiveEnvelope's expected
output buffer had 20 bytes but the layout requires 24. Source inspection derives
the corrected literal independently: PositiveId occupies eight bytes, and
String "ok" occupies align8(header eight + two UTF-16 units four) = 16 bytes.
The record therefore has 24 bytes with four trailing zero padding bytes. The
fixture and both verifier pins now include that padding. The failed gate stopped
after six PositiveId checks and is not accepted as the required 38-case matrix.

The protocol documentation also corrects version-2 help guidance following
report 171's primer error: use syntaxVersion and topic for help; frontend belongs
to definition/evaluation requests. The original experiment evidence is unchanged.

## Durable evidence

[Verified archive](evidence/172-native-refined-int/archive.json) indexes 4,888
source and evidence entries, including completed accepted runs and failed
attempts. The [evidence ZIP](evidence/172-native-refined-int/evidence.zip) is
58,390,723 bytes; SHA-256:
`8bf8aa048fc5b50d3e52c9810b5c83d0cf0b234f98ab58f17e334c2feb893dd5`.
Archive CRC, every entry hash and pinned source hashes were verified. Generated
IR is included; build trees, binaries and environment dumps are excluded.

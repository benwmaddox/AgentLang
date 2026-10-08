# Nested variable-sized String comparison plan

## Decision

Use String as the first variable-sized leaf in separate TextLeaf, TextEnvelope,
and TextState record types. Preserve the existing fixed Leaf/Envelope/State
ABI3 controls. Keep lists deferred because element construction and mutation
would add separate ownership behavior.

The owning representation is now coordinated with the architecture plan at
.agentlang/owning-stack-002/plan.md:

    u32 UTF-16 code-unit count
    u32 reserved zero
    exactly 2*n bytes of little-endian UTF-16 code units
    zero padding to an 8-byte extent

Payload bytes are 8+2*n and extent is S(n)=align8(8+2*n). Preserve the exact
verified .NET String code-unit sequence, including embedded NUL, isolated
surrogates, and a surrogate pair formed by concatenating separate inputs.
There is no terminator, normalization, UTF-8 conversion, replacement, or
pointer to another allocation. string.length returns the UTF-16 code-unit
count. The architecture plan requires dynamic instance extents/offsets in
runtime events and explicit dynamic markers plus minimum sizes in type
metadata; do not report a particular instance size as a fixed type size.

ABI3 LlvmAot rejects IrString/LString and string.concat, and the ABI3 runtime
descriptor kinds are Int, Bool, Unit, and Record. Do not extend ABI3 solely for
this comparison or substitute a different body. The first String result is
interpreter plus owning LLVM plus independent semantic/byte oracles. Existing
fixed-record ABI3 comparisons remain the control; no String memory or speed
claim follows from an owning-only run.

## Workload

Use this acyclic record graph, distinct from fixed controls:

    TextLeaf { text: String; codeUnits: Int }
    TextEnvelope { leaf: TextLeaf; tag: Int }
    TextState { count: Int; last: TextEnvelope }

Compile the actual VerifiedIrBody for mailbox.turn(TextState) -> TextState.
It passes state.last through a user helper that duplicates the nested
TextEnvelope, reads both copied String fields, appends ! to one and ? to the
other with string.concat, combines those results, records
string.length(originalText), increments count and tag, and returns a rebuilt
TextState. Feed the body a dynamic nested RecordValue through the Value input
API; do not build the string only as a zero-input source constant. The
interpreter and owning candidate must consume the same verified program and
body.

The two main inputs are:

- Short: count 10, TextLeaf.text = A, codeUnits = 1, tag 70.
- Long: count 10, TextLeaf.text = 東京と🧪abc, codeUnits = 8, tag 70.

The short and long source strings have 1 and 8 UTF-16 code units. Their
runtime String extents are 16 and 24 bytes. The returned strings A!A? and
東京と🧪abc!東京と🧪abc? have 4 and 18 code units, with String extents 16
and 48 bytes. This crosses alignment boundaries in both input and output and
makes output size depend on the runtime input.

Use direct Value inputs for edge values if Flow literal parsing cannot
preserve them. Include empty String, A-NUL-B, the astral scalar 🧪, and isolated
high/low surrogate round trips. Also compile a minimal VerifiedIrBody with two
String inputs and one string.concat; feed it high surrogate U+D83E and low
surrogate U+DDEA as separate StringValue inputs and require the output pair
U+D83E U+DDEA, length 2. TypedIR rejects null String constants and Value
inspection rejects null StringValue; it does not reject arbitrary non-null
UTF-16 code-unit sequences.

## Independent oracle

Use raw code-unit bytes, never the owning codec, to build expectations:

| Case | Input code units | Input data bytes | Input S(n) | Turn output code units | Output data bytes | Output S(n) |
|---|---:|---|---:|---:|---|---:|
| Short | 1 | 41 00 | 16 | 4 | 41 00 21 00 41 00 3f 00 | 16 |
| Long | 8 | 71 67 ac 4e 68 30 3e d8 ea dd 61 00 62 00 63 00 | 24 | 18 | 71 67 ac 4e 68 30 3e d8 ea dd 61 00 62 00 63 00 21 00 71 67 ac 4e 68 30 3e d8 ea dd 61 00 62 00 63 00 3f 00 | 48 |
| Empty | 0 | empty | 8 | 2 | 21 00 3f 00 | 16 |
| Embedded NUL | 3 | 41 00 00 00 42 00 | 16 | 8 | 41 00 00 00 42 00 21 00 41 00 00 00 42 00 3f 00 | 24 |
| Astral | 2 | 3e d8 ea dd | 16 | 6 | 3e d8 ea dd 21 00 3e d8 ea dd 3f 00 | 24 |
For all rows, output State.count is 11, Envelope.tag is 71, and
TextLeaf.codeUnits equals the input string's UTF-16 length: 1, 8, 0, 3, or 2
as applicable.

The separate joined-surrogate body takes high U+D83E and low U+DDEA as its two
String inputs. Each input has one code unit and a 16-byte String extent; the
result is U+D83E U+DDEA, raw bytes 3e d8 ea dd, length 2, and a 16-byte String
extent.

The complete short initializer State is 40 bytes:

    0a00000000000000 0100000000000000 4100 000000000000 0100000000000000 4600000000000000

The complete long initializer State is 48 bytes:

    0a00000000000000 0800000000000000 7167ac4e68303ed8eadd610062006300 0800000000000000 4600000000000000

The complete short turn output State is 40 bytes:

    0b00000000000000 0400000000000000 4100210041003f00 0100000000000000 4700000000000000

The complete long turn output State is 72 bytes:

    0b00000000000000 1200000000000000 7167ac4e68303ed8eadd61006200630021007167ac4e68303ed8eadd6100620063003f00 00000000 0800000000000000 4700000000000000

For each string, the 8-byte header contains the u32 code-unit count followed
by four reserved zero bytes. The long output has four trailing String padding
bytes. Spaces above separate fields; within a String, bytes are contiguous.
The empty String has an all-zero header and extent 8. The embedded-NUL input
has two padding bytes. The astral input has four padding bytes. The isolated
high and low inputs each store exactly one code unit; joining them produces
the four bytes of the astral pair without transcoding.

Under declaration-order inline records, S(n) is also the exact instance
extent of TextLeaf.text. TextLeaf extent is S(n)+8, TextEnvelope extent is
S(n)+16, and TextState extent is S(n)+24. Thus the main input State extents
are 40/48 bytes and the output State extents are 40/72 bytes. Dynamic absolute
offsets are State.count=0, TextEnvelope start=8, String header=8, String
code-unit bytes=16, TextLeaf.codeUnits=8+S(n), and TextEnvelope.tag=16+S(n).
Metadata must mark the last two offsets dynamic and events must report their
actual runtime values. Minimum extents are String 8, TextLeaf 16,
TextEnvelope 24, TextState 32.

## Acceptance after architecture/API freeze

1. Run both main inputs through the interpreter and owning native O0/O2 using
   the exact same verified program and body. Assert the semantic values,
   lengths, and literal byte buffers above. Assert dynamic Value input
   marshalling instead of a constant-only path.
2. Run empty, embedded-NUL, astral, and isolated-surrogate Value cases. Verify
   exact code units before and after nested record return. For the split pair,
   verify the two-input String concat body combines U+D83E and U+DDEA without
   rejecting, replacing, or normalizing them.
3. Inspect physical execution ranges. Each nested String payload must be
   contiguous inside its owning record extent; simultaneously live dup copies
   must be disjoint. Drop/reuse one copy and decode the survivor. Follow any
   helper return relocation before checking retained publication. Runtime
   events must use per-instance sizes; static metadata must use dynamic markers
   and minimum sizes rather than one observed extent.
4. Exercise a scope or nested-local replacement with different input String
   lengths while preserving an outer TextState. Report dynamic local live
   bytes, reserved frame/metadata bytes, holes, and moved bytes separately.
   No per-local max-length slab or dead hole counts as reclaimed ownership.
5. Trigger divide-by-zero after a real concat/helper allocation; assert the
   original Value input remains unchanged, stack/live owners unwind to zero,
   and a prefilled caller output buffer remains byte-for-byte unchanged.
6. Test stack capacity after real dynamic allocation/call activity using a
   required extent derived from verified IR lifetimes and the frozen layout.
   Test retained output one byte below 40 and 72 bytes for the main cases.
   Assert the failure boundary, full cleanup, and no partial caller-buffer
   commit. Keep input, native stack, native retained staging, host staging,
   commit copy, and instrumentation accounting distinct.
7. Keep current fixed Int/Bool/Unit/empty-record and ABI3 controls unchanged.
   Do not label the String fixture three-way parity. Throughput/cache effects
   need a separate timed workload and complete memory accounting.

## Source evidence and validation boundary

Read-only inspection confirmed:

- TypedIR.fs rejects null LString constants, while ValueInspection.fs rejects
  null StringValue; non-null strings are otherwise .NET strings.
- IrInterpreter.fs implements string.concat with ordinary string
  concatenation and string.length with String.Length (UTF-16 code units).
- LlvmAot.fs rejects IrString/LString and does not whitelist string.concat;
  arena_runtime.h has only Int, Bool, Unit, and Record descriptors.
- The existing NativeMailbox/NativeDispatch fixtures have only numeric record
  fields and remain suitable fixed-value controls.
- The architecture/API direction and dynamic metadata requirements are in
  .agentlang/owning-stack-002/plan.md.

Useful source searches:

    rg -n 'StringValue|RecordValue|LString' src/AgentLang.Core/Core.fs
    rg -n -C 3 'LString value when isNull|IR_CONSTANT_LITERAL_INVALID' src/AgentLang.Core/TypedIR.fs
    rg -n -C 2 'TString, StringValue text|VALUE_STRING_NULL' src/AgentLang.Core/ValueInspection.fs
    rg -n 'string.concat|string.length' src/AgentLang.Core/Compiler.fs src/AgentLang.Core/IrInterpreter.fs
    rg -n 'ensureNativeType|IR_LLVM_UNSUPPORTED_CONSTANT|supportedPrimitiveOperations' src/AgentLang.Llvm/LlvmAot.fs
    rg -n 'AL_RUNTIME_TYPE_' src/AgentLang.Llvm/native/arena_runtime.h
    rg -n 'field .*String|String' experiments/AgentLang.NativeDispatch/mailbox.flow experiments/AgentLang.NativeMailbox/mailbox.flow

This is planning only: no implementation, fixture, or new harness was created
and no tests were run. After the runtime API/metadata freeze, extend the
existing compact value-stack verifier with these cases, freshly build Release
artifacts, run interpreter and owning O0/O2 plus focused C O0/O2/UBSan-trap
checks, and verify source hashes. Preserve the existing fixed-control
expectations and keep generated artifacts in a fresh
.agentlang/owning-stack-002/verification-<run-id>/ directory.

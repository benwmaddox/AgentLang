# Owning-stack design: keep the language small

Review date: 2026-10-08. This records the current direction and implementation
constraints, not a claim that the native backend is complete. See the
[ownership and locality contract](STACK-ONLY-RESEARCH.md).

## One source model

Keep immutable typed values, records, existing collection types, named inputs,
ordinary functions, `let`, branches and explicit effects. A live value owns its
nested payload. Creating another independent value, returning a result or
publishing mailbox state must preserve that ownership. Storage placement is
the compiler/runtime's responsibility.

Do not add region names, lifetime annotations, borrowing syntax, pointer
arithmetic, allocator functions or multiple storage-specific array types merely
to implement this backend. Likewise, source authors should not manually marshal
ordinary values between temporary and retained storage. Goose's size classes
and destination construction are useful compiler research; its reference and
pool semantics are not implicitly part of this language.

## What the compiler can know

The verified IR already carries stack shapes, local slots, closed types and
resolved function signatures. At a program point, the compiler can identify
the consumed values and their frame-relative logical positions. Fixed-size
records allow exact byte offsets to be computed statically.

Logical distance and byte distance differ for variable-size values. Three values
back is a known position, but intervening strings or lists can have input-dependent
lengths. Keep checked lengths or boundary offsets within the bounded owning
storage/its metadata as needed. Such offsets locate nearby inline payloads;
they must not become handles to scattered independently lived objects. Absolute
addresses and recursive frame bases remain runtime quantities.

Prefer static placement where sizes and lifetimes permit it. Do not require
compile-time knowledge of every input length or expose physical stack indexing
in source to achieve this. Growing values and caller result construction need
separate layout validation; the fixed-record prototype does not resolve them.

## Copies, locals and cleanup

Preserve ordinary value semantics at function calls and local reads. The compiler
may construct directly at a destination or transfer a last-use value when it
proves no source use remains. Those are implementation choices, not requirements
to annotate every call with ownership syntax.

At the stack IR level, loading a local and dropping the loaded value does not
destroy the named binding; the local owner remains until scope cleanup in the
initial native implementation. Flow currently rejects `drop(local);` as an
expression statement because `drop` has zero outputs; it is not a source-level
early-release operation. Report that distinction. Precise early local
release is a possible later operation only if a workload demonstrates the need;
it would need use-after-consume and branch-join checking.

Resource exhaustion depends on the backend's layout and configured capacities.
A layout optimization can legitimately let a previously oversized workload fit.
It must never exceed the configured bound, leave a partial move/publication, or
change values, effects or language errors when adequate resources are available.
Pin layout/backend versions for exact byte-capacity experiments; do not require
identical allocation failures from the interpreter and different native layouts.

## Keep mailbox rules at the boundary

Prefer typed state and message inputs with state and response outputs, bounded
retained storage/queues, and atomic publication after successful preparation.
Host-declared capacities must be inspectable. Async suspension and provider
buffer lifetimes require explicit contracts, but do not justify a general
source-level lifetime system. Another message changing state during an await
remains a logic issue that ownership alone does not solve.

## Review conclusion

The main complexity risk is exposing the allocator's mechanics as language
concepts. Keep layout, frame reservations, byte movement, poisoning and trace
buffers behind the same semantic IR. Expose them through diagnostic inspection
and measurement. The current instrumentation is a correctness probe, not the
intended optimized release runtime. Validate its cost separately before deciding
which checks remain in release builds.

The next implementation must prove physical locality, independent payloads,
correct returns and bounded cleanup. Variable-size values and real async I/O
remain follow-ups. Neither requires redesigning the source language in advance.

## Automatic cleanup clarification

The user confirmed compiler-controlled cleanup and explicitly dropped references
into older stack data. Keep independent owning values and named access; no
older-stack view/reference feature is planned.

Use scope as the understandable lifetime rule for named values. Consumed operand
temporaries are released automatically; scope exit cleans up local owners on
success and error paths. Results acquire caller-owned storage before callee
cleanup. Earlier last-use cleanup can be an optimization, not manual stack
position bookkeeping required of authors.

Reclaiming an owning extent makes its bytes reusable; it need not return arena
backing to the OS. A bump-pointer rewind applies to a dead suffix or complete
frame. An older dead value below live younger values cannot be removed by a
single rewind; the compiler must reuse storage, delay the rewind or perform
accounted compaction/transfer. Report live payload separately from reserved
frame slots and pooled capacity. This distinction is necessary for a truthful
account of automatic popping.

## Comparison with Rust

Rust separates ownership/drop rules from reference lifetime checking. Lifetimes
constrain reference validity; ending a borrow is not itself an allocation free.
See the [Rust ownership chapter](https://doc.rust-lang.org/book/ch04-01-what-is-ownership.html),
[lifetime chapter](https://doc.rust-lang.org/book/ch10-03-lifetime-syntax.html)
and [drop scopes](https://doc.rust-lang.org/reference/destructors.html).
Rust can also use bump arenas; [bumpalo](https://docs.rs/bumpalo/latest/bumpalo/)
is an example, so pointer rewind is not a capability unique to AgentLang.

Our proposed simplification is to restrict the language's data-lifetime shapes
and omit borrowed stack references, making compiler-owned cleanup and local
payload layout the ordinary path. It trades some freedom for fewer source-level
ownership concepts. Rewinding a dead suffix can be cheap; moving/copying large
inline values, non-LIFO deaths, reservations and external resource cleanup still
cost work. Compare measured workloads, not a generic claim that our scheme is
faster than Rust or that ordinary Rust always invokes a heap allocator.

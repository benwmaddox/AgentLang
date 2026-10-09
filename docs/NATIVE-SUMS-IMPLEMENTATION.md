# Native Option/Result implementation boundary

Status: focused native conformance validated. Fresh interpreter/native O0/O2
checks pass for the supported payload closure; all 37 full local Release checks
also pass. See [report 160](../reports/160-native-option-result.md) for the
accepted gates, failed attempts and limits.

The existing typed semantic IR and interpreter define Option/Result semantics.
Extend the selected stable-arena `OwningStackAot` backend to execute those same
operations. Keep the older graph-backed `LlvmAot` backend's limitations explicit.
Do not add language ownership annotations, reference APIs, or changes to parsing.

## Representation

Layout ABI 3 adds Option kind 7 and Result kind 8. The physical descriptor sizes
remain type 36 bytes, field 16 bytes and layout 40 bytes. Stack ABI remains 1.
The descriptor's field table is interpreted by type kind: sum types have exactly
two case rows at payload offset 8, with zero flags/reserved bits. Only Option's
None row uses the no-payload child sentinel `UINT32_MAX`, and that row must use
it. A real child on the None row is invalid even if its size metadata agrees.

The initial payload closure is Int, Bool, Unit, String, payload-free enums,
acyclic records, and nested Option/Result over those types. Float, List and
refined wrappers remain outside this native slice. Unsupported types fail
validation even when they occur only in an inactive alternative.

Values use an eight-byte little-endian tag followed by the active payload's
standalone inline encoding. Some/Ok have tag 0; None/Error have tag 1. None
occupies eight bytes. String payloads retain exact UTF-16 code units, including
embedded NUL and isolated surrogates, under the existing string contract.
Validate every declared alternative's descriptor graph, but scan only the
selected payload's bytes. Reject unsupported payload types and recursive or
out-of-bounds layouts even when the invalid alternative is inactive.

Construction writes the tag and copies its inline payload once at the arena's
allocation tail. Matching obtains a checked descriptor view into the active
payload, without relocating it. Ordinary local loads, calls, returns and matches
must not add automatic compaction. Descriptor views are an implementation detail,
not source-level references or independently owned pointers.

## Lifetime and validation

The compiler tracks constructed sum owners and case-local provenance, merges
branch exits conservatively and emits a rewind only with a static proof. A
payload escaping its scope keeps the owner alive. Missing proof retains storage.
The scanner validates bytes and bounds; it never decides whether memory is dead.

Required acceptance includes interpreter/native O0/O2 parity for both Option
and Result alternatives, nested sums and records, host inputs/outputs, equality,
locals, calls, joins and extracted payloads. Independent byte fixtures cover tags,
empty payloads, Unicode code units and a dynamic sum before a fixed record tail.
Malformed tags, descriptors, padding and active payloads fail closed. Rejection
must preserve retained output and roll back bank staging. Capacity boundaries,
fault cleanup and compile-time rewind decisions have their own checks.

Run focused native-value-stack and owning-mailbox gates with freshly built
artifacts, then the full local Release gate. Update related schema versions,
fixtures and deterministic C-test metrics together; do not bless new metrics
without inspecting the cases they represent. Reports retain failures and limits.
Wait for completed worker handoffs before freezing evidence, and never compact
or delete pinned reports afterward.

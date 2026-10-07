# LLVM architecture and first native implementation

Status: architecture accepted; scalar AOT implementation in progress, 2026-10-07.
No native execution or memory performance result is claimed yet.

The user authorized proceeding when the existing capabilities justify it. The
interpreter has demonstrated typed discovery, composition, tests, persistence
and reuse by fresh external agents. Reports 099 and 101 do not establish a
comparative reliability advantage, but that remains a research question rather
than a prerequisite for a bounded native backend. The guided defect-repair comparison is complete (report 102); longer-term
reliability research continues.

## Architecture decision

Keep the existing verified semantic IR authoritative. Add an optional
`AgentLang.Llvm` project that consumes `VerifiedIrBody`, validates its primitive
registry against `Compiler.primitiveIrCatalog`, and emits LLVM directly from
that body and its reachable function closure. Resolve stable word identities
and revisions from the verified snapshot. No source frontend is reimplemented.

The first slice supports pure Int, Bool and Unit values; constants, locals,
scopes, conditional branches and user calls; checked integer arithmetic,
comparisons, scalar equality, Boolean operations and minimal stack operations.
Reject all unsupported operations/types and declared or inferred effects before
calling LLVM, including unsupported code in untaken branches. Unsupported
programs receive a structured rejection rather than partial native execution.

Use LLVM signed-overflow intrinsics and guard division by zero and MinValue/-1
before `sdiv`. Preserve execution order, first failure, source diagnostics,
instruction fuel and call-depth limits. The
[LLVM 19 language reference](https://releases.llvm.org/19.1.0/docs/LangRef.html)
defines the relevant intrinsic and division behavior.

Emit a pure Windows x64 DLL with explicit context, output slots and status.
Native failures return status data; exceptions do not unwind across the ABI.
Keep the ABI versioned with exact widths, offsets, alignment, output ordering
and deterministic error/source metadata. Test actual native layout against
managed declarations and independent fixtures. Validate ABI version and output
capacity at the native entry. Scalar results copy into caller-owned storage.
This prototype ABI does not settle container or arena layout.

## Implementation and acceptance

New backend files belong under `src/AgentLang.Llvm`, tests under
`tests/AgentLang.Llvm.Tests` and `tests/fixtures/native-conformance`, with an
explicit optional `scripts/Verify-NativeConformance.ps1` gate. Existing ordinary
interpreter builds do not acquire a mandatory LLVM tool dependency.

Compile actual emitted LLVM at both `-O0` and `-O2`, load the native DLL, and
compare with the same verified body's interpreter result AND independent
expected outcomes. Include signed boundaries, overflow and division errors,
multiple output ordering, both branches and joins, nested calls, locals/scopes,
first-failure ordering, source information, fuel/depth limits, trusted registry
rejection, effect rejection and unsupported untaken branches. Native ABI tests
must check offsets, version mismatch and short output-buffer canaries. A host
exception cannot count as an expected language error.

The local Visual Studio Build Tools include Clang 19.1.5 and lld-link at
`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin`.
MSVC 14.44.35207 and Windows SDK 10.0.26100.0 libraries are present. The initial
pure DLL can link with `/dll /noentry /nodefaultlib`; verify actual compilation
and linking rather than treating tool discovery as an execution result.
Use fresh ignored build directories and retain the frozen experiment binaries.

## Later execution and memory work

Reuse the same verified handles and lowering machinery for JIT. Add versioned
dispatch, transitive invalidation and executable-generation lifetime management
for interactive replacement and test-scoped dictionary overlays. Release AOT
can pin direct calls. The IR currently does not carry candidate/temporary
publication status, so a release-package operation must later certify a
release-eligible dependency graph before emission.

Expand native conformance to nominal values, strings, containers, Float and
effects before claiming general native support. Implement and measure arena
allocation and retained mailbox state after making escape, suspended-I/O,
resource cleanup and cancellation boundaries explicit. The primary candidate
remains per-turn scratch arenas with same-mailbox retained state, compared with
whole-request arenas under equal memory and tail-latency constraints.

The user is not tied to an implementation language. Keep the tested F# frontend,
checker and IR initially; choose the later native runtime language by correctness,
maintainability, allocation and platform integration needs. Emitted native code
need not depend on F# or .NET. Preserve semantics and versioned ABI contracts
if implementation languages change.
Implementation refinement: scalar function inputs may pass directly, with a
caller-owned scratch/output buffer shared by nested calls. Keep logical output
count distinct from required scratch capacity and validate the latter at the
native entry. This is a scalar ABI experiment, not arena-lifetime evidence.
Windows stack-probe requirements are legitimate runtime support: retain probing
and link the small standard helper if valid generated frames require it, rather
than weakening semantics or disabling probes to satisfy `/nodefaultlib`.
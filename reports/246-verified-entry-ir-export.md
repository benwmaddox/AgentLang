# 246 — Verified entry IR export for AOT Campfire

Status: completed build-time export milestone; native host calls and AOT Campfire remain in progress.

The first native-delivery blocker was that committed Flow entrypoints could be
bound only to the interpreter. `Engine.ExportVerifiedFlowEntryPoint` now returns
the compiler-minted verified call body and its immutable program snapshot for
build-time AOT compilation. It shares the resolver used by `BindFlowEntryPoint`.
Both validate committed status, Flow source ownership, identity/revision, closed
exact signatures, verified function identity and the direct resolved call target.
Export does not execute the function or invoke test/production providers.

The exported snapshot retains the exact host contracts and declared effects.
Export is not host admission: the native backend still rejects unsupported host
operations explicitly. No raw model-only IR execution path, compiler escape hatch,
runtime reflection or new native ABI was introduced.

The new AOT conformance project compiles an exported committed nominal/record
function at O0 and O2, executes it natively, compares with independent expected
results and interpreter behavior, and checks both DLLs lack a managed runtime
header. It then initializes the actual Campfire dictionary with scoped tests,
exports `campfire.http.dispatch`, verifies its retained typed database contract,
and proves native rejection emits no executable and runs no provider. This is
native compilation of the export path, not a native Campfire endpoint.

The focused native suite passes 31 assertions. Runtime regression passes 47
groups / 2,564 assertions, including malformed/closed signatures, exact source
metadata, provider/dictionary stability and old/new exports across replacement.

## Repairs exposed by validation

New fixture syntax was corrected to the current nominal-type and structured-test
forms. Test code distinguished the verified body from its program handle and
used the string-based type-variable constructor. Failure logs are retained.

Source spans belong to the committed word representation rather than the original
multi-definition input document. Comparing exports to public source/introspection
then exposed a real replacement mismatch: the generated call shim used a candidate
AST filename instead of the committed definition's span. The common resolver now
uses the committed span for that call site. This fixes export and binding metadata;
it does not rewrite all callee expression origins. Null type values now render
safely in the invalid-signature diagnostic instead of failing while formatting it.

## Application and performance direction

The [native plan](../docs/CAMPFIRE-AOT-PLAN.md) records the user's intended request
path: inbound request to one worker mailbox, direct SQL/cache interaction and
response. Functions stay within the turn. Start with real SQL and caching disabled;
reuse worker-owned connections/statements where correct, and retain response bytes
until socket completion before resetting the arena. Measure dispatch wait, SQL,
rendering, write completion, arena reuse, throughput, tail latency and memory.

Next: lower pinned typed host calls in the owning backend, add required lists and
integer formatting, and connect native SQLite/cookie/browser/rendering/HTTP adapters.
The complete room endpoint is the first AOT performance checkpoint. Neither these
pure native conformance values nor report 140's offered-load mailbox fixture predict
Campfire's maximum throughput. The full application migration remains required.

## Final local validation

The final native export run passes 31 assertions at O0/O2; the runtime suite
passes 47 groups / 2,564 assertions. The fresh Campfire gate passes ten builds,
nine suites and three policy verifiers, including 466 real HTTP assertions.
The native-export and Campfire receipts pin the same final Core source hash.
All builds are Release, with zero reported warnings/errors and package auditing
retained. New project restoration used the host for NuGet audit access; trusted
HTTP regression used synthetic fixtures and ephemeral loopback sockets outside
the sandbox. CI was not used.

Final receipts are `.agentlang/campfire-aot-export-246/combined-01/receipt.json`
and the native run `20261012T144751391Z-0ddec5f81c214c569138d43f9d173442/receipt.json`.
No native ABI/layout/offset change was made. Failed parser, test build and source
metadata checks are retained alongside the final passes.
## Preserved evidence

The [archive](evidence/246-verified-entry-ir-export/evidence.zip) contains 138
members, including 79 source snapshots, generated LLVM IR, diagnostics, passing
and failing logs/receipts, and synthetic traces. Each member passed readback
verification against the [manifest](evidence/246-verified-entry-ir-export/manifest.json).
Archive SHA-256:
`b593279874a6080fe01b74755494977a94f2784f7bc5d0bc56ba38b4bf8d92f4`.
Temporary stores, databases and executable artifacts are excluded.

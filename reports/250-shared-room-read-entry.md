# 250 — Shared GET/HEAD room entry

The room page now has a fixed `campfire.http.room-read` entry in the language.
It shares cookie/session restoration, refresh-token selection and authenticated
user gates with the generic dispatcher. Valid GET/HEAD room-show dispatch uses
this entry; unsafe requests and unrelated routes retain their existing dispatcher
paths. The fixed entry rejects unrelated routes and unsafe methods before calling
providers.

This makes the application closure suitable for a narrow AOT profile without
admitting login, logout, message creation or IP-ban operations. It does not yet
compile or serve the full room page natively: bounded lists, integer formatting,
the remaining native host contracts and native HTTP/cookie/rendering facilities
are still required. Native throughput remains unmeasured.

Each new function carries documentation and attached scoped tests. Session
restoration preserves its established error and bot-fallback rules. Refresh runs
before user lookup; later response failures preserve the pending refresh token.
Authenticated gates still check CSRF before browser compatibility and missing
users. Room lookup remains membership-filtered, and room display keeps message
listing and last-room-cookie behavior.

Independent comparison uses a frozen copy of `http.flow` from commit `7c010a0`.
The reference and current applications have separate virtual dictionaries and
deterministic injected providers. This avoids comparing two entrypoints that both
call the new implementation. Responses, provider arguments/order, and independent
expected outcomes are compared for GET and HEAD. Static IR inspection follows
reachable functions rather than confusing the retained whole-project host catalog
with the operations used by this entry.

All **15 new attached cases** pass as part of application initialization. The
independent comparison passes **111 assertions**: 24 full-response/provider-trace
comparisons, five guard cases and startup/IR closure/effect checks. The exported
closure contains exactly cookie verification, session lookup/refresh, user lookup,
bot-key authentication, browser version comparison, room lookup, membership lookup
and message-page lookup. Its declared and inferred effects are only db.read/write.

The focused entry/native-host gate passes **660 assertions**, standalone C O0/O2
SQLite smoke runs and both fresh-loader probes. The local Campfire gate passes all
**22 checks**, including **466 HTTP integration assertions**. A first build failed
because the new test module's namespace conflicted with the runner's module; that
harness error was corrected and the failure log retained. Review also corrected a
test assumption that a persistent function ID equals its name; IDs are resolved
from compiler metadata instead. No typing, effect or test policy was relaxed.

Validation used `Verify-Campfire.ps1 -NoRestore` and
`Verify-CampfireNativeSqlite.ps1 -NoRestore`, with fresh Release builds, locally.
The backend runtime/layout did not change in this milestone; broad LLVM/arena
results from report 249 are not represented as new executions here. No CI autoruns
are enabled. The complete Campfire migration remains the goal; the next native
blockers are driven by this actual room-page closure.

The [sealed evidence](evidence/250-shared-room-read-entry/manifest.json) contains
304 members and 84 source snapshots, verified after archive readback. Archive
SHA-256: `b03e520bfa850f2b6bc81bcdd7c5dc8a7bcd1bc4767c24b24e1e1d33ade7842c`.
The frozen reference source SHA-256 is
`53799b068ff329a1e5c4f2c8e907818408504876a77c3c1a2aad1def71a3a7dd`;
its Git blob matches the source in `7c010a0`.

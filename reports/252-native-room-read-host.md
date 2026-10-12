# 252 — Native room-read host boundary

Status: validated provider/compiled-entry milestone, 2026-10-12. Native HTTP and endpoint performance remain unfinished.

`CampfireRoomReadV1` compiles the existing `campfire.http.room-read` language entry with its complete nine-operation provider closure. Authentication, authorization, browser gating and response decisions remain in the language. Native providers implement signed-cookie verification, browser-version comparison, session lookup and refresh, user/bot lookup, room lookup, membership and the newest message page.

The final local gate passes **234 assertions** at O0/O2, including full typed interpreter/native response parity and standalone C execution without CLR. Its 63 source pins remain unchanged. This establishes the compiled entry and providers; it does not establish an HTTP endpoint or throughput.

## Implementation

The compiler admits the exact reachable operations, signatures, effects, nominal types, enum orders and record fields. It validates timestamp predicates through semantic input bindings rather than source variable spelling. Default pure compilation remains restrictive; the unrelated room-find profile rejects this entry before emitting artifacts.

Owning layout ABI5 and runtime/module ABI2 are unchanged. The new host callback ABI1 takes borrowed typed input views (16 bytes on x64, offsets 0/8/12), worker-owned provider context and bounded output. Results are constructed directly in reserved arena storage, validated, then committed; failed reservations rewind. The callback result limit is 16 MiB, not a required worker preallocation.

The SQL context is 136 bytes with thirteen prepared statements. Its initializing OS thread owns initialization, calls and destruction; overlapping calls are rejected. WAL is provisioned before workers start. Workers verify FULL durability, foreign keys, private-cache isolation, dirty reads disabled, finite busy timeout and patched SQLite. Message reads use a deferred snapshot; read-modify-write refresh uses BEGIN IMMEDIATE. Cleanup resets statements and restores autocommit; failed cleanup poisons the context. See [strict SQLite policy](../docs/SQLITE-POLICY.md).

Message serialization selects the newest forty rows and emits them oldest-first. Two passes share a read snapshot, calculate offsets, then write directly while checking identity and extent. SQLite pointers never become language values. Crypto uses bounded caller-owned scratch, CNG and fixed-purpose Rails-compatible keys. SQLite/CNG internal allocations remain outside language arena accounting.

## Validation

Final command: `pwsh -NoProfile -File scripts/Verify-CampfireNativeRoomRead.ps1`. PowerShell 7 is required; each attempt is retained separately.

| Evidence | Result and scope |
| --- | --- |
| Final room-read gate | 234 assertions; O0/O2 full-value parity, GET/HEAD, unsafe-method rejection, cookie/session/user/bot/browser/room cases, empty and Unicode forty-row pages, capacity, and standalone no-CLR smoke |
| Native SQLite audit | Actual imported module hash/version and effective settings; O0 old/new snapshots, bounded writer contention, no partial refresh and context reuse |
| Independent cleanup probe | Authorizer-denied rollback poisons the same provider and rejects reuse; successful cleanup restores autocommit and permits another callback; owner-thread destruction succeeds |
| Independent SQL contention probe | Lookup during another connection's BEGIN IMMEDIATE, typed Unavailable after bounded wait, clean reuse and refresh after rollback |
| LLVM regression | 1,010 assertions; unchanged compiler source, including 30,400 finite Float patterns per optimization profile |
| Legacy room-find profile | 323 focused O0/O2 assertions |
| Pure-provider vectors | 23 checks using the actual sixty-type/123-field layout, including eight-byte Option.None |

The rollback-denial proof is a separate direct-callback probe, not a case inside the 234-assertion integrated gate. These tests do not establish process-crash durability, HTTP parity, scheduling throughput or slow-client behavior.

Final run: `.agentlang/campfire-native-room-read-252/test-runs/20261012T211345464Z-137d051946e74781a2842b73058b5394`.
SQLitePCLRaw.lib.e_sqlite3 2.1.12 reports SQLite 3.53.3; loaded DLL SHA256 is `b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`.

## Failures retained and repaired

Integration exposed a missing provider argument in nominal validator calls, incorrect descriptor stack accounting, an old forced ABI header, and CRT/declaration link issues. Provider checks found reversed Option tags, double-counted room extent, and an overly large minimum for valid eight-byte results. Test repairs fixed token/bot fixtures, enum encoding from semantic IR, strict smoke-loader warnings, and explicit smoke database reset after the unsafe-method case.

The settings probe loaded a second SQLite module and passed it another instance's connection. It now resolves the module owning the native import's IAT address and checks its pinned bytes before calling exports. Subsequent gates pass those settings and writer-recovery paths without the earlier fail-fast.

The [sealed evidence](evidence/252-native-room-read-host/manifest.json) records source snapshots, final receipts, generated textual artifacts, regression logs, independent probes and retained failures. Archive readback verifies member hashes.

## Remaining delivery

Connect native HTTP transport, browser normalization, rendering and response-cookie finalization. Missing authentication redirects to `/session/new` and remembers the return URL in the encrypted session; root redirects can replace flash. Both need native cookie mutation, not merely token signing. Preserve unrelated state and use configured authority for return URLs.

Then prove loopback HTTP parity and measure the same handler with one and several workers. Continue through the full Campfire route/protocol/application inventory; this room-page checkpoint does not redefine the complete migration goal. See [the AOT delivery plan](../docs/CAMPFIRE-AOT-PLAN.md).

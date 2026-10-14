# Native Campfire adapters

## Complete room-read provider profile

`room_read_adapter.c` and `room_read_pure.c` supply the nine operations used by
the compiler-minted `campfire.http.room-read` entry. Route, authentication and
authorization policy remain in the language functions. This profile adds session
lookup/refresh, user and bot lookup, membership and message-page reads, cookie
verification and browser comparison to room lookup. It does not yet supply HTTP
transport, HTML rendering or response cookie signing.

The caller owns a zeroed 136-byte SQL context and a separately initialized crypto
context. A worker must initialize, call and destroy its SQL context on the same
thread, with no overlapping calls. Startup provisions WAL before workers open
their private connections. Workers verify FULL durability, foreign-key enforcement,
dirty reads disabled and the required patched SQLite version. Message-page reads
use a deferred snapshot; session refresh uses an immediate transaction. Failed
operations reset statements and restore autocommit before reuse. Failed cleanup
poisons the context. See [strict SQLite policy](../../../docs/SQLITE-POLICY.md).

Provider results are bounded arena-owned values, with a 16 MiB maximum result.
SQLite pointers and crypto scratch do not escape into language values. SQLite
and Windows CNG internal allocations are outside the language's arena metrics.
The profile uses owning value layout 5, context/module ABI 2 and room-read callback
ABI 1; its borrowed input descriptor is 16 bytes on Windows x64.

The compiler knows nothing about Campfire. This app supplies its boundary in
[`room-read-host.json`](room-read-host.json): the entry word, the adapter symbol,
the operation tags, names, types and effects, and the output limit. It also owns
the ABI header [`campfire_room_read_host_abi.h`](campfire_room_read_host_abi.h).
The compiler checks every reachable host call against that manifest and the
verified host catalog, and validates every adapter output against its type
index before committing it.

The local integration gate is:

```powershell
pwsh -NoProfile -File scripts/Verify-CampfireNativeRoomRead.ps1
```

The gate passes 234 assertions, including interpreter/native response parity at
O0/O2, real reader/writer contention and cleanup, and standalone execution without
.NET. A separate native probe verifies rollback-failure poisoning. See
[milestone 252 report](../../../reports/252-native-room-read-host.md) for evidence
and remaining work. HTTP transport and endpoint throughput remain unmeasured.

## Retired room-lookup profile

The original single-operation room-lookup adapter (`room_sqlite_adapter.c`, report
249) was retired in report 267. Its operation, `campfire.rooms.find-for-user`, is
tag 7 of the room-read profile above, and the compiler no longer carries any
Campfire-specific profile.

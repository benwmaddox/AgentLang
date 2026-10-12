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

The local integration gate is:

```powershell
pwsh -NoProfile -File scripts/Verify-CampfireNativeRoomRead.ps1
```

The gate passes 234 assertions, including interpreter/native response parity at
O0/O2, real reader/writer contention and cleanup, and standalone execution without
.NET. A separate native probe verifies rollback-failure poisoning. See
[milestone 252 report](../../../reports/252-native-room-read-host.md) for evidence
and remaining work. HTTP transport and endpoint throughput remain unmeasured.

## Original room-lookup profile

This trusted C adapter implements only `campfire.rooms.find-for-user`. Application
authorization and composition remain in the language's verified semantic IR.
The current target is Windows x64 with clang/lld. It is a native SQL slice, not a
complete HTTP server.

Each worker initializes a zeroed, caller-owned 32-byte context on its executing
thread. Initialization opens an existing database without CREATE, enables foreign
keys, installs a 5-second busy timeout, and prepares the membership-filtered query.
The same connection and statement are reused until worker shutdown. Calls and
destruction must run on the initializing thread; overlapping calls are rejected.
NOMUTEX is used for the private connection, so the host must maintain this ownership
rule. A mailbox is not automatically an OS thread: an executor that migrates turns
must revise this adapter's connection policy before using it.

The UTF-8 database path is supplied by the trusted host, is canonical, and has a
trailing NUL beyond its explicit byte length. It is borrowed during initialization.
Language code cannot select paths, import libraries or invoke arbitrary SQLite SQL.
The callback writes a bounded typed Result/Option/record value directly into the
owning arena; no SQLite column pointer escapes. Every query resets the statement
and clears its bindings, including failures. Valid database errors are language
values; invalid context/thread/output storage produces a boundary failure.

The local fixture pins SQLitePCLRaw.lib.e_sqlite3 **2.1.12**, Windows x64 DLL SHA-256
`b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`.
Its queried SQLite version is **3.53.3** and THREADSAFE is 1. The package bytes,
version, source ID and compile options are recorded with milestone evidence. The
package license metadata is Apache-2.0; SQLite itself is public domain. Redistributed
release dependencies still need their package notices included in release packaging.

`e_sqlite3.def` lists only the C exports needed for this adapter and dependency
audits. The build creates an import library from that file, copies explicit trusted
headers/import libraries/runtime DLLs, and hashes them in host manifest format 2.
No SDK import library or package DLL is committed to the repository. Owning ABI 2,
value layout 5, host callback ABI 1 and SQLite context ABI 1 remain distinct contracts.

Run locally:

```powershell
pwsh -NoProfile -File scripts/Verify-CampfireNativeSqlite.ps1
```

The gate rebuilds the F# compiler/test driver, checks O0/O2 typed AOT execution
against SQLite and the interpreter reference, then builds and runs standalone C
executables that invoke the same AOT entry without .NET. It also checks ABI offsets,
Unicode, missing/invalid data, output bounds, statement recovery, thread ownership,
cleanup and dependency admission. The managed test driver is not the release host.

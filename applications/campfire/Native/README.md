# Native room lookup adapter

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

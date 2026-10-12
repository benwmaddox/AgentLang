# 249 — Native SQLite room lookup through AOT

The typed LLVM room entry now reads an actual Campfire SQLite database through a
trusted C adapter. A standalone native executable invokes that entry without F#
assemblies or a .NET runtime at both O0 and O2. This removes the real-SQL blocker
for one operation; the complete native HTTP request path remains unfinished, and
there are no native Campfire throughput numbers yet.

The application operation remains
`UserId RoomId -> Result<Option<RoomRow>, DbError>` with `db.read`. The adapter
executes the same parameterized membership-filtered query as the interpreter
reference. A worker initializes its own connection and prepared statement once,
reuses them for requests, and closes them on shutdown. The private connection uses
NOMUTEX and enforces initializing-thread ownership and nonoverlapping calls. This
requires a pinned worker executor; migrating a mailbox between threads is not
supported by this adapter. There is no extra SQL mailbox or process-global provider.

Output is serialized directly into the bounded owning arena reservation. No SQLite
column pointer escapes. The runtime validates the exact typed extent and trims
unused reservation space. Statement reset and binding cleanup run after successes
and failures. Missing membership returns None; malformed rows and SQL failures map
to the established DbError values. Boundary failures remain structured native
diagnostics rather than forged database values.

The compiler accepts explicit trusted headers, import libraries and runtime DLLs,
copies them into the artifact, and pins their hashes and roles in native-host
manifest **format 2**. Runtime DLL admission rejects managed and non-x64 images.
These build inputs are unavailable to language code or request data. Owning context
ABI **2**, value layout **4** and host callback ABI **1** are unchanged. The new
worker SQLite context is ABI **1**, 32 bytes with 8-byte alignment; offsets are
checked in C and F#.

Dependency evidence pins SQLitePCLRaw.lib.e_sqlite3 **2.1.12**, with the DLL verified
against the package entry. It reports SQLite **3.53.3**, THREADSAFE=1 and MUTEX_W32;
the full source ID and compile options are preserved in evidence. DLL SHA-256:
`b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`.
SQLite has its own allocator and caches; using this native dependency does not
establish an entirely arena-only process. The adapter itself has no explicit heap
allocator or payload-compaction mechanism.

Validation includes native/interpreter parity, all RoomKind and Option cases,
Unicode paths and rows, raw timestamps, membership filtering, malformed/null data,
missing tables, insufficient output storage followed by successful recovery,
statement reuse, independent contexts, wrong-thread calls/destruction, missing-file
no-CREATE behavior, and close/reinitialize. All validation runs locally.

Failures found during development were retained: F# marshaling fields needed
mutable backing fields with explicit packing; the standalone test incorrectly
expected owning-thread destruction to fail; and clang temporary storage needed a
workspace directory. Review also identified a deployment risk in default Windows
DLL lookup when the executable lives outside the artifact directory. The wrapper
now requests safe artifact-local dependency lookup. Fresh processes at O0/O2,
with SQLite initially unloaded, load the sibling SQLite DLL and execute the typed
lookup (11 assertions per process). The legacy-loader negative control was skipped
because this machine also has SQLite in a default search directory; the evidence
does not prove that the old loader fails on this workstation.
None of these corrections relaxes library tests, typing or effect admission.

The final focused gate passes **549 assertions**, both standalone C O0/O2 smoke
runs, and the two fresh-loader probes. The rebuilt owning arena gate passes
**50 checks** with stable source hashes. The Campfire regression gate passes all
**22 checks**, including **466 HTTP integration assertions**. The rebuilt full
LLVM suite passes **1,010 assertions**, including 30,400 finite Float formatting
patterns per optimization. Native executable/module/SQLite dependency inspection
finds only the generated module, SQLite and Windows dependencies, with no CLR.
The failed
loader build (an incorrect .NET enum member name) and probe build (F# multiline
literal formatting) are retained with the earlier failure logs.

The [sealed evidence](evidence/249-native-sqlite-room-adapter/manifest.json) contains
360 members and 124 source snapshots, verified after archive readback. Archive
SHA-256: `d9890fcce4abea31af21d3cf30632b796e8de130a8778c45c3906f9435bf00f4`.

Next: compile the shared GET/HEAD room path, add the remaining narrow native host
contracts plus list/integer-formatting support, and supply native cookie/render/HTTP
adapters. Measure the direct/single-worker request path first, then
**inbound request → one of X worker mailboxes → direct SQL/cache → response**.
The full 177-row migration inventory, live protocols, assets, jobs, stored files,
existing-data migration and deployment remain the deliverable.

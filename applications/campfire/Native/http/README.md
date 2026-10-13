# Native Campfire HTTP prototype

This Windows HTTP Server API v2 host loads the compiler-generated room-read AOT
module and pinned SQLite dependency. The service does not load the CLR. The build
driver validates the passing native room-read receipt, frozen sources, artifact
hashes and closed host profile before generating binary pins for the executable.
Select `-RuntimeProfile diagnostic` or `-RuntimeProfile trusted-generated` when
building, using a passing room gate for that exact profile. The executable pins
the profile and dependency hashes; there is no runtime profile switch. Diagnostic
workers carry initialization/poison bitmaps. Trusted-generated workers omit those
diagnostics and trust verified generated writes while retaining typed boundary,
refinement, effect and capacity checks.

One bounded HTTP.sys queue feeds a fixed worker pool. A request stays on its
selected worker for normalization, typed encoding, language execution, direct
SQLite access, rendering, cookie finalization and synchronous response sending.
Each worker owns its prepared statements and both distinct crypto contexts.
Storage remains owned until sending completes. Rendering measures its output
before writing and can grow committed response capacity without rerunning the
entry: session refresh may already have committed.

The prototype supports room GET/HEAD and their authentication/redirect behavior.
It is not a complete Campfire migration. Compare the two runtime profiles using
the same workload; diagnostic throughput is not the release-runtime target.
SQLite uses local WAL, FULL durability and private worker
connections. A fixed busy timeout is currently available; request deadlines and
atomic transaction scopes spanning dependent operations remain delivery work.

The host binds an explicit loopback prefix and accepts these arguments:

```
native_http_host.exe --module <AOT DLL> --manifest <manifest JSON>
  --db <existing fixture DB> --secret-file <private synthetic secret>
  --port <port> --workers <count> --stop-event <dedicated event name>
```

Only one owner should control listener lifecycle. Stop through the dedicated
event and verify the final worker-cleanup count and zero fatal status before
reusing the port. Keep secret files, database fixtures and authenticated client
configurations in ignored private directories. Reports may publish hashes,
redacted results and source snapshots, but must not publish those configurations.

The response-cookie codec context is distinct from the loaded module's pure
provider context. Never interchange them. Do not also link `room_read_pure.c`
into this executable: the codec currently contains overlapping public symbols,
while the host obtains its request provider lifecycle from the loaded AOT module.
`cookie_unescape` follows request-cookie decoding;
query decoding intentionally follows the separate HTTP transport semantics.

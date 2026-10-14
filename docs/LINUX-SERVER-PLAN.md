# Flow HTTP server on Linux and Windows (plan of record, 2026-10-14)

Goal (user): benchmark Rails, Rust and Flow Campfire as closely as possible —
all three on Linux in WSL2, with and without cache, equal pinned vCPUs,
measuring CPU, throughput and memory. Our server therefore needs a Linux
build. Porting the Windows-only Campfire host (HTTP.sys, BCrypt, about 11,800
lines of app C) would duplicate code the stdlib restructure deletes anyway, so
the Linux server is built as the restructure's end state: a generic,
portable language HTTP host, with Campfire's request handling in Flow.

Probe results (2026-10-14):
- Owning-stack IR emitted on Windows compiles and links unchanged with clang
  on Linux, except `dllexport`/`dllimport` on the entry point and the OS
  random hook (now portable: `getrandom` on Linux). SHA-256, `string.split`
  and `crypto.random-bytes` words return identical results on both.
- WSL2: Ubuntu 24.04, 22 vCPUs, clang/lld 18, libsqlite3. No dotnet 9 (the
  compiler stays on Windows; Linux only builds and runs the native output).

## Pieces (built in parallel, then joined)

### A. Generic HTTP host (runtime level, no Campfire names)

- Portable C in `src/AgentLang.Llvm/native/http_host/`, BSD sockets on both
  platforms (epoll on Linux; a portable poll/accept path on Windows). Not
  HTTP.sys: Rails (Puma) and Rust (hyper) also serve from sockets.
- Workers: N threads, each pinned to one CPU, each owning its arena, its
  SQLite connection and its per-worker context. On Linux each worker listens
  on its own `SO_REUSEPORT` socket. No shared mutable state; no locks.
- HTTP/1.1: keep-alive, request bodies with `Content-Length` (no chunked
  request bodies), responses with `Content-Length`, no compression.
- Flow contract, stdlib record types:

  ```flow
  record HttpHeader { name: String  value: String }
  record HttpRequest {
      method: String
      target: String          // path and query, raw
      headers: List<HttpHeader>
      body: Bytes
      remote-ip: String
      now-microseconds: Int   // Unix time, read once per request
  }
  record HttpResponse { status: Int  headers: List<HttpHeader>  body: String }
  ```

  The app supplies `fn <app>.worker-init(config: List<HttpHeader>) -> <Context>`
  (run once per worker at startup; key derivation and similar; its result
  lives in a protected region below every request's arena mark) and
  `fn <app>.handle(request: HttpRequest, context: <Context>) -> HttpResponse`.
  `config` is name/value pairs from a config file (secret values included;
  never logged).
- Build: Windows emits the module IR and runtime sources; a build script
  compiles and links the executable for the requested target
  (`x86_64-pc-windows-msvc` or `x86_64-pc-linux-gnu`; the Linux link runs in
  WSL with clang/lld). The owning backend gains an explicit target so the
  Windows-only emission (`dllexport`, the BCrypt random hook) is not emitted
  for Linux.

### B. Stdlib: crypto, JSON, cookies (interpreter and native)

Done in 274; the final signatures are in STDLIB-PLAN.md section 3 (seal
returns ciphertext followed by the tag, not a record; no pbkdf2-sha1). Rails 7
scheme, read from cookie_response_codec.c: keys are PBKDF2-HMAC-SHA256 of
secret_key_base, 1000 iterations, salt "authenticated encrypted cookie" (32
bytes, AES-256-GCM, empty AAD, 12-byte IV, 16-byte tag; value
base64(ciphertext)--base64(iv)--base64(tag), Rack-escaped) and "signed cookie"
(64 bytes, HMAC-SHA1, value base64(envelope)--hex(digest)). The plaintext is
the envelope {"_rails":{"message":base64(json),"exp":"...Z"|null,"pur":
"cookie.<name>"}} with exp 20 calendar years after now. Still needed for D: a
Unix-microseconds clock primitive.

- `crypto.aes-256-gcm-open(key, nonce, ciphertext, tag, aad) -> Option<Bytes>`
  and `crypto.aes-256-gcm-seal(key, nonce, plaintext, aad) -> AesGcmSealed`
  (ciphertext + tag); `crypto.hmac-sha1`, `crypto.pbkdf2-sha1` if Rails' cookie
  path needs them (derive from applications/campfire/Native/http/
  cookie_response_codec.c and Rails 7 defaults).
- Native backing: the OS library on each platform (libcrypto on Linux, BCrypt
  on Windows) — Rails uses OpenSSL with AES-NI, so a portable C AES would
  handicap us. Since 275 every crypto primitive (SHA-256, HMAC-SHA1/SHA256,
  PBKDF2-SHA256, AES-GCM, constant-time compare on Linux) is an OS library call.
- JSON, host-parsed: `json.parse(text) -> Option<Json>` with typed accessors
  (`json.field(json, name) -> Option<Json>`, `json.text`, `json.int`,
  `json.bool`, `json.index`), plus `json.object(fields)`/`json.string` builders
  or a minimal encoder sufficient for Rails' cookie envelopes.
- Cookies: `http.cookie(headers, name) -> Option<String>` (Cookie header parse),
  Rack `url-escape`/`url-unescape`, and `Set-Cookie` value building.
- Time: RFC 1123 / ISO-8601 formatting for cookie expiry (`time.*`).

### C. Stdlib: database (SQLite)

- `db.query(sql: String, parameters: List<DbValue>) -> Result<List<DbRow>, DbError>`
  and `db.execute(...) -> Result<Int, DbError>` (changes), with
  `DbValue` (int | text | null | blob as an enum-plus-payload record) and row
  accessors `db.int(row, column)`, `db.text(row, column)`,
  `db.optional-text(row, column)`. `sql` must be a literal; each worker
  prepares a statement once per literal and reuses it (Rails and Rust also
  reuse prepared statements). Effects `db.read` / `db.write`.
- Native: the SQLite C API (system libsqlite3 on Linux; the existing SQLite
  build on Windows), one connection per worker, WAL, the same PRAGMAs as the
  current Campfire host (read them from room_read_adapter.c).

### A and C as built (274)

- Stdlib Flow: `src/stdlib/http.flow` (HttpHeader/HttpRequest/HttpResponse,
  `http.header`, `http.path`, `http.query`, `http.config`, `http.response`)
  and `src/stdlib/db.flow` (DbValueKind/DbValue/DbRow/DbError, `db.*-param`,
  `db.column`, `db.first-row`, `db.int`, `db.optional-int`, `db.text`,
  `db.optional-text`, `db.blob`). Columns are zero-based; REAL arrives as text.
- `db.query` / `db.execute` are host operations (manifest in
  `src/AgentLang.FlowHttp/Db.fs`; effects db.read / db.write). The SQL
  argument must be a string literal (source check at load). Each worker
  prepares a statement once per SQL text and reuses it. DbError.code is the
  SQLite extended result code, DbError.message is `sqlite3_errmsg`.
  Interpreter: SQLitePCL raw API (Microsoft.Data.Sqlite's e_sqlite3);
  native: `src/AgentLang.Llvm/native/http_host/flow_http_db.c`.
- The owning backend has an explicit `OwningTarget` (Windows x64 default,
  byte-identical; Linux x64 without dllexport/dllimport/BCrypt) and
  `OwningStackAot.emitHostModule`, one module with named entry exports
  (`flow_http_handle`, `flow_http_worker_init`) sharing one layout.
- The worker context is the worker-init result; the worker keeps its bytes
  and passes them as the handle entry's second input (copied into the arena
  per request, validated like any input). Zero-copy borrowing of an in-arena
  root below the request mark is not done.
- Host: `flow_http_host.c`. Linux: one SO_REUSEPORT socket and epoll loop
  per worker. Windows: workers accept from one shared listening socket with
  WSAPoll (no SO_REUSEPORT). `--pin` pins worker i to the i-th CPU of the
  process affinity mask (use with `taskset`).
- Build: `scripts/Build-FlowHttpServer.ps1 -App <app> -Source <files>
  -Target windows-x64|linux-x64 [-RuntimeProfile trusted|diagnostic]
  -OutDir <dir>`; the Linux half runs `scripts/build-flow-http-linux.sh` in
  WSL and leaves `~/linuxbench/flowhttp/<app>/flow_http_server`.
- Run: `flow_http_server --port 13774 --db app.db [--config cfg.txt]
  [--workers N] [--pin] [--allow-control]`; prints `READY <port>`; stops on
  SIGINT/SIGTERM, Ctrl+C, or `POST /__flow_http/shutdown` with
  `--allow-control`. Config lines are `name=value` (never logged).
- Tests: `tests/AgentLang.FlowHttp.Tests` (counter app; interpreter vs
  native on Windows and Linux, trusted and diagnostic runtimes).

### D. Campfire in Flow (after A-C)

Port, in the order the room GET needs them: request parsing (route, cookies,
forms, Accept headers) from the raw request; the Rails encrypted session
cookie and CSRF state; the room-read queries (SQL text moves from
room_read_adapter.c into Flow); the avatar token key derived in
`worker-init`; the response with Set-Cookie. Then delete the Campfire native
host and the .NET hosts (stdlib plan step 5).

## Benchmark protocol (Linux)

- All three servers natively in WSL2 (not Docker), each against its own copy
  of the same SQLite snapshot on ext4, the same session cookie, the same room
  and 40-message window, contract validation on.
- Equal vCPU counts per server (`taskset`), the load generator on disjoint
  vCPUs. vCPUs, not physical cores: report that.
- CPU per request from `/proc/<pid>/stat` deltas summed over all server
  processes; memory as summed PSS (`smaps_rollup`) peak and steady; req/s and
  p50/p99.
- Two columns: no cache (all caches off) and with cache (Rails' configured
  store for message fragments; Rust's fragment cache, not its response cache;
  Flow's cache — a singleton if possible, otherwise per worker; locking design
  goes to the user first).
- One decision applied to all three: whether requests carry a real browser
  `User-Agent`.
- Same session, interleaved order, repeated, medians and ranges.

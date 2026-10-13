# 258 — AOT native room page passes the shared Campfire harness contract

Status: shared-contract correctness passed on the Rails-built seed; no benchmark yet.
The full migration is incomplete.

The LLVM AOT native host now serves the shared `once-campfire-verification`
harness's Rails-built seed. It authenticates a session issued by the real Rails
app and passes the harness's own room-page contract. Benchmarks will compare this
AOT host with the Rails and Rust references. The interpreter remains a parity
control only.

## Message identity correction (amends report 257)

Rails `Message#to_key` is `[client_message_id]`, so `dom_id(message)` and
`dom_id(message, :presentation)` use the client message ID. Report 257 used the
database ID there. Both renderers now emit:

```html
<div id="message_CLIENT" class="message" data-message-id="ID"><div id="presentation_message_CLIENT" dir="auto">…</div></div>
```

Only `data-message-id` carries the row ID, as in Rails. The interpreted test
checks rows by exact `data-message-id="N"` (467 assertions pass). The room-read
gate was rerun (238 assertions, O0/O2) because its source freeze covers the
renderer. At O0 and O2 the native and freshly rebuilt interpreted room bodies are
byte-identical, at 6,558 bytes on the synthetic fixture. The cookie/header
checker (291), all 20 corpus cases and the 5 O2 transport cases pass, and both
hosts exited cleanly.

A first attempt correctly refused to start because a Rails container still held
port 13774. It is retained.

## Shared seed and Rails session

- The harness at `6f71d70` built its seed from the pinned public Rails revision
  `90b3300`. The Rails image (`sha256:f866a34b…`) was built in WSL Docker with
  BuildKit, and the seed database SHA256 is `a1553708…` (`seed/source.json`).
  The seed builder generated the credentials locally; they are not published.
- The Rails reference container ran with bridge networking and published only
  127.0.0.1:13774. Its database copy had push/webhook endpoints neutralized as in
  the harness's `compare.rb`.
- The unmodified harness `bench/contracts.rb` ran against Rails and produced
  `room_show.json`, an implementation-independent contract:
  - the newest 40 IDs of room "watercooler" from seed SQL, oldest-first, with no
    `created_at` ties;
  - each message's seeded content tokens;
  - Rails `id="message_<client id>"` markers.

  Rails also passed `messages_page`. The harness then stopped at its own
  `search` check against Rails. That route is outside this milestone and was not
  investigated here.
- The harness loadgen, built on Windows with Rust 1.98.1 and its locked
  dependencies, logged in to Rails. The container was then stopped. Its database
  copy, holding the Rails-created session row, was WAL-checkpointed and
  integrity-checked for the native host.

## Native result

The O2 TrustedGenerated native host ran with four workers. It served that
database with the seed's `secret_key_base`, using Rails' salts and 1,000-iteration
PBKDF2 derivation, and authenticated the Rails-issued `session_token` /
`_campfire_session` cookie unchanged.

The harness loadgen ran `--validate room_show.json` at concurrency 4: 400 requests
sent `Accept-Encoding: identity` and 400 offered gzip.

- **800/800 responses valid** (`route-contract-v1`): zero invalid responses, zero
  errors, all status 200.
- Decoded page: 10,073 bytes. Native does not compress, so identity was served
  for both settings.
- Clean shutdown, with four workers cleaned and fatal status 0. The Rails
  session's `last_active_at` was unchanged, so the native host made no refresh
  writes.

These 400-request runs are correctness checks, not throughput measurements.

## Comparability notes for the benchmark

- **Page weight differs.** The native page carries the contract's identities and
  content but not Rails' avatars, authors, timestamps, actions or layout. Stored
  rich text stays HTML-escaped because no Rails-equivalent sanitizer exists yet.
  Report decoded and wire bytes per server, and do not quote a speed ratio as if
  the pages were equal.
- **Platforms differ.** The native host runs on Windows HTTP.sys. The references
  run in Docker on WSL2 (Linux VM), reached through WSL localhost forwarding.
  The Rust reference links libvips and Unix-only code and cannot build natively
  on Windows. CPU budgets must be stated per side.
- **Gzip.** Primary runs should use `--gzip 0` for every server, or report both
  settings.

## Sealed evidence

[Archive](evidence/258-native-campfire-shared-contract/258-native-campfire-shared-contract-evidence.zip)
and [manifest](evidence/258-native-campfire-shared-contract/manifest.json): 71
entries, 101,840 bytes, SHA256
`824da46877a8d2b1ebfab3cd5a46b4210e30e49c60bc847c65b55bb74bc65f41`.

This archive was resealed after report 259. The harness asks that its generated
artifacts not be published, so the harness-generated `room_show.json` contract
and the Rails scrape summary are now recorded only as SHA256 hashes
(`e920149e…` and `3718690e…`). The tool scripts in the archive are their later
versions; for example, `Native-Contract.ps1` gained a database-copy parameter.
The archive contains:

- parity, HTTP validation, run and build receipts, plus the gate and HTTP test
  receipts;
- the native contract runs, including loadgen JSON and host logs;
- Rails container logs (scanned clean) and the seed provenance;
- the loadgen build log, the tools and the changed sources.

Excluded: all databases, `secret_key_base` and the credentials env file,
Rails-issued cookies, seed `labels.json` (it holds fixture passwords), and the
harness-generated artifacts.

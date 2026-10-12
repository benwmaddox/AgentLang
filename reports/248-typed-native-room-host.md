# 248 — Typed native room host calls

The owning LLVM backend can now compile the existing Campfire room lookup through
an explicit, statically linked native profile. The default pure compiler and fixed
mailbox compiler retain their host-operation rejection. This removes a native
execution blocker; it does not deliver SQLite, an HTTP endpoint or Campfire
performance numbers.

The profile accepts only `campfire.rooms.find-for-user` with
`UserId RoomId -> Result<Option<RoomRow>, DbError>` and `db.read`. It checks the
exact nominal scalar, record-field and enum definitions, and rejects other reachable
operations or effects. Admission follows the verified entry/function closure,
allowing the actual Campfire snapshot to retain unrelated, unused host contracts.
Compiler-minted semantic IR remains the execution authority.

`compileWithNativeHostProfile` links a trusted C adapter at build time. Its separate
native export carries an execution-scoped provider pointer through nested calls;
there is no language import mechanism or provider global. The adapter receives two
integer values and a bounded writable arena suffix. Returned bytes undergo exact
extent and nested type/layout validation before being marked initialized. Success
trims the unused reservation; failure rewinds only the temporary suffix and retains
the source diagnostic. Commit adds no payload copy or move. A valid `Result.Error`
is a value, whereas adapter failure is a native boundary diagnostic.

The new host contract is ABI1. Owning context ABI2 and layout ABI4 remain unchanged,
as do the existing mailbox ABI and exports. The emitted manifest pins the operation,
schema/layouts, effect allowlist, diagnostic runtime profile, output limit, LLVM and
adapter/runtime/header source hashes, and a manifest fingerprint. The provider is
statically linked: a forced C header rejects incompatible declarations, and a
missing symbol fails at link time. This is not a generic runtime
provider-descriptor handshake.

The focused entry-export/native-host suite passes **354 assertions** at O0/O2.
All **22 Campfire gate checks** pass. The fresh native owning-value-stack gate
passes **50 checks**, including **44 arena-lifetime assertions**, unchanged ABI
oracles, **56 C storage cases / 792 checks** per optimization, sanitizer checks
and the cross-backend experiment. Source hashes remained stable through that
final gate. The final full LLVM suite passes **1,010 assertions**, including
30,400 finite Float formatting patterns per optimization profile.

Validation used fresh Release builds, the entry-export/native-host and LLVM test
projects, `Verify-NativeValueStack.ps1`, and `Verify-Campfire.ps1 -NoRestore`.
All ran locally; the socket/audited-restore gates used the authorized host boundary.
The [sealed evidence](evidence/248-typed-native-room-host/manifest.json) contains
195 members and 116 source snapshots, with every member verified after readback.
Archive SHA-256:
`13f74d80622f5a3fa20860a5884b6eb61be4f679189316ab1c546ca309b64396`.

The fixture covers O0/O2, both Option cases, all four
DbError cases, populated records, absent names, Unicode and extreme integer IDs,
interpreter parity, nested provider-context propagation, malformed output, provider
failure, suffix rollback and preserved older values. Negative admission cases cover
an altered enum, extra declared effects, an unapproved operation and the broader
dispatcher. Managed and raw native entry guards are checked separately.

Two fixture failures were corrected without relaxing the language or linker: scoped
test-file wrappers now each belong to one function; the freestanding C fixture uses
explicit initialization rather than requesting CRT `memcpy`/`memset`. Review found
a missing internal payload-counter update after commit, now covered directly.
An initial broader native gate rejected source changes made during its run; the
final gate rebuilt against frozen source and passed its continuity check.
Public exact live-payload accounting remains unavailable because descriptor aliases
and dead interior arena ranges prevent interpreting those counters as unique live
memory.

Limits: this milestone uses a synthetic C adapter, not real SQL. It uses the
diagnostic runtime profile and has no performance claim. The managed convenience
execution wrapper serializes calls on one compiled object for lifetime safety; the
native export has no such gate. Production worker scaling must use and measure the
native host, with separate contexts/connections and bounded response ownership.

Next: expand the native adapter boundary for the shared GET/HEAD room path, add its
list layout and integer formatting, implement native SQLite/cookie/browser/HTTP
adapters, then prove real HTTP parity. Measure the direct/single-worker baseline
before the one-of-X-worker comparison. The full 177-row migration inventory plus
live protocols, assets, jobs, stored files and deployment remains the goal.

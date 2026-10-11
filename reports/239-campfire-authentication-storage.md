# 239 — Campfire authentication and session storage

Status: standalone SQLite component locally validated; Core registration and HTTP integration are pending.

The application now has six fixed typed storage operations for session lookup, conditional session refresh, user lookup, bot-key lookup, IP bans and membership-scoped room lookup. The separate `Authentication.Persistence` project reuses the existing identifier, room and database-error DTOs. Database paths are host configuration; callers cannot supply SQL or obtain a connection.

These operations provide facts and conditional writes for language-authored request handling. User lookup deliberately does not filter account status, matching upstream session restoration. Bot-key lookup requires an active bot and preserves the pinned Rust parser's first-two-segment behavior without controller-level trimming. Room lookup joins memberships and gives administrators no implicit access.

Session refresh compares parsed instants and updates inside a transaction guarded by the observed stored timestamp. An equal cutoff leaves the row unchanged; a strictly newer cutoff permits refresh. Current time and cutoff are explicit inputs. Writes use canonical UTC microseconds and preserve the session token, user and creation timestamp. Missing rows and provider failures return closed database errors.

## Validation

Root repeated these local commands after reviewing the implementation:

```powershell
dotnet build tests/AgentLang.Campfire.Authentication.Persistence.Tests/AgentLang.Campfire.Authentication.Persistence.Tests.fsproj -c Release -m:1 --no-restore
dotnet run --project tests/AgentLang.Campfire.Authentication.Persistence.Tests/AgentLang.Campfire.Authentication.Persistence.Tests.fsproj -c Release --no-build --no-restore
```

The build passed with zero warnings/errors; the executable passed **8 groups / 102 assertions** against fresh real SQLite fixtures. Checks cover present/missing sessions and users, all stored role/status cases and invalid values, bot key shape/eligibility, parameterized injection-shaped inputs, banned IPs, membership isolation/orphan rows, timestamp parsing, strict cutoff boundaries, optional IP/user-agent fields, reopen persistence, trigger-aborted rollback, and concurrent refresh ordering. The concurrent test runs older/newer refreshes and verifies the newer activity and metadata remain final regardless of lock order; it is a bounded fixture, not a throughput benchmark.

Root review removed an invented positive-ID restriction: SQLite and the pinned model permit explicitly stored zero and negative session IDs, and fixtures now prove those IDs survive lookup and refresh. Review also corrected cutoff quantization: a stored 1 µs activity is unchanged at a 1.0 µs cutoff but refreshed at 1.1 µs. Only persisted current time is truncated; the host's supplied cutoff retains its precision. Flow's microsecond `UtcInstant` policy remains aligned with the upstream application's microsecond time representation.

The initial sandbox restore failed with NU1900. Root reviewed the plain projects and successfully retried restore on the host with `NuGetAudit=true`. Subsequent builds and tests ran in the sandbox against those restored dependencies. Earlier fixture failures, removed placeholder assertion, review corrections and their receipts remain in the evidence history.

## Provenance and limits

Behavior follows Rust `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad` and Rails reference `90b330024dec3e757c79b6a7e6568f93da8e3148`. Source pins are retained with the evidence. Test schemas are narrow fixtures, not the complete migrated Campfire database.

This milestone does not restore cookies, invalidate sessions on account changes, implement login, register these operations in the runtime, or serve HTTP. The next request slice will combine these facts with cookie compatibility and language policy. This is a .NET host implementation; native support is pending.

Evidence archive: `reports/evidence/239-campfire-authentication-storage/evidence.zip`; 26 members independently read back and hash-checked. SHA-256: `b4d80425638fa37b766740cde4ff64fa8468d74690bec90d3d40d8bc37647fec`.

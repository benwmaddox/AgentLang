# 238 — Campfire Rails cookie compatibility

Status: standalone component locally validated; not yet connected to request handling.

Campfire can now use a trusted host component to read and write its existing Rails cookie formats. The API is fixed to signed `session_token` and encrypted `_campfire_session` values, plus Rack wire encoding. Clock inputs are explicit and configuration secrets stay in the host. Language code is not given cryptographic operations or keys.

The component uses .NET's PBKDF2, HMAC, constant-time comparison, AES-GCM and cryptographic random-number APIs. Its compatibility code handles the reference's serialization, purpose and expiry rules. Signed-cookie SHA1 is the pinned Rails compatibility format; new cryptographic algorithms were not implemented.

## Local validation

Root repeated the serial Release build and executable suite:

```powershell
dotnet build tests/AgentLang.Campfire.Cookies.Tests/AgentLang.Campfire.Cookies.Tests.fsproj -c Release -m:1 --no-restore
dotnet run --project tests/AgentLang.Campfire.Cookies.Tests/AgentLang.Campfire.Cookies.Tests.fsproj -c Release --no-build --no-restore
```

The build passed with zero warnings/errors; the suite passed **76 assertions across 5 groups**. Tests use standalone fixtures rather than the ignored reference checkout. Root also confirmed that all four expected signed-cookie outputs match the independently generated upstream Rails vectors, including HTML characters and literal U+2028/U+2029. The fixture contains 2 key-derivation cases, 4 signing cases, 16 signature-verification cases, 1 encrypted-generation record, 8 encrypted-verification cases and 7 Rack encoding cases.

Checks cover byte-identical signing, key derivation, purpose and legacy fallback, exact expiry boundaries, malformed/no-zone expiry rejection, encrypted session continuity, ciphertext/IV/tag tampering, random IVs, bounded JSON/cookies, and behavior after service disposal. Invalid reads return `None`; writes return closed failures.

Review corrected raw-cookie size accounting: the pinned 4,096-byte bound is name plus raw value, not the percent-escaped wire length. Rack parsing separately permits bounded percent expansion. Review also required explicit-zone ISO expiry parsing and checked Rails' Unicode serialization behavior. Expiry parsing supports up to seven fractional digits; longer precision is rejected. Pinned Rails output uses three digits.

The initial sandbox restore failed with NU1900 because its vulnerability-data endpoint was unreachable. After reviewing the two plain projects, root retried the restore on the host with `NuGetAudit=true`; it succeeded. Builds and tests ran locally in the sandbox using those restored dependencies. Setup failures and review corrections are recorded in the implementation notes; the host restore and root validation logs are retained with the evidence.

## Provenance and limits

The reference is Rust `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`, with Rails submodule `90b330024dec3e757c79b6a7e6568f93da8e3148`. Fixture source hashes and the exact 37signals MIT license are saved beside the tests. The publicly published synthetic fixture secret is test data, not production configuration.

This establishes the selected cookie-format behavior, not complete HTTP authentication, login, session invalidation, request ordering, rich-text safety or a whole-application security review. The component remains a .NET host implementation; native application support is pending.

Evidence is saved in `reports/evidence/238-campfire-cookie-compatibility/` with per-file hashes. No real credentials, runtime binaries or package caches are included.

Root also passed six additional literal-escape/backslash/Unicode controls. Their script and logs are included in the archive; an initial F# script warning was corrected before the final control run. The archive contains 15 members, independently read back and hash-checked. Archive SHA-256: `2deff41b9e4c8f68a85be867f1f7d2026c5cadd5e5f5ef886c2b498bd2a1d3fd`.

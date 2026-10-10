# Read-only native gap investigation

Reviewer: external `gpt-6-luna`, max reasoning, explorer role.
Base checkout: `b4ad8aea0fcabbb3403b4cfd12dcc3254069b8d6`.
No implementation, build or native test was performed in this investigation.

Recommended next slice: unvalidated nominal String wrappers. Refined String
already uses the dynamic String layout, distinct nominal TypeIds, owner ranges
and exact-name host measurement/encoding/decoding. The current rejection is in
`src/AgentLang.Llvm/OwningStackAot.fs` scalar admission (around line 835) and
scalar call validation (around line 1069). The LLVM test suite explicitly expects
`TextTag : String` without a validator to fail (around line 1373). Report 174 also
records this as unsupported.

Own `OwningStackAot.fs` and `tests/AgentLang.Llvm.Tests/Program.fs` for the primary
change, with independent fixtures as required. At O0/O2, verify exact-name host
round trips, rejection of bare String and another wrapper without output changes,
nested record/active sum preservation, and exact UTF-16 bytes including NUL and
an isolated surrogate. Preserve the nominal TypeId without adding predicate
calls or payload movement. Layout ABI 3 and stack ABI 1 should remain unchanged.

Mailbox compilation shares the type-layout admission path. Include focused
mailbox import/round-trip acceptance before claiming State/Continuation support.
No allocator, ownership or ABI architecture decision was identified.

Fresh runner commands identified by the investigation:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1
pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1
```

These are prospective checks, not passing results. Full LLVM, applicable mailbox
and local Release gates remain publication requirements. Bool wrappers need more
codec/descriptor work; Float needs broader numeric/ABI analysis; List needs a
larger representation/lifetime design. None is implemented by this checkpoint.

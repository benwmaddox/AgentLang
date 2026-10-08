# Native dispatch integration plan

## Scope

Create a separate `experiments/AgentLang.NativeDispatch` fixture and
`scripts/Verify-NativeDispatch.ps1`. The fixture copies the exact existing
`mailbox.flow` bytes and contains only compile-time F# bootstrap code plus a
standalone Windows x64 C runner. Record integration-specific current-run
evidence under `.agentlang/native-dispatch-001/integration-*`.

## Implementation

1. Copy `experiments/AgentLang.NativeMailbox/mailbox.flow` byte-for-byte and
   reproduce only its Flow/2 parse/lower/body compilation in a new F# bootstrap.
   Assert initialize/begin/resume bodies all reference the same backend-authorized
   `VerifiedIrProgram`, then call the compiler worker's exact `compileModule` API
   once for the three named entries.
2. Consume the mailbox worker's published header and source in a standalone C
   executable. Have the C process load the one generated DLL, use its immutable
   metadata, and own fixed mailbox state, tokens, banks, scratch, workspace, and
   dispatch for every turn. No CLR entry or managed callback participates in a
   turn. The native read-only borrow view returns final `State.total` values.
3. Verify both O0 and O2 modules/runners against the report108 oracle and protocol,
   failure-retry, capacity-retry, poison/reset, zero-live-lease, cleanup, metadata,
   ABI-layout, determinism, and import constraints. Capture fresh dependency and
   native build commands, hashes, process output, and checks only for this run.

## Acceptance and validation

- Module has one descriptor and initialize/begin/resume entries with matching
  exact input/output types and verified-program identity.
- Native execution returns A/B values 17/103, preserves A's pending owner/token
  across divide-by-zero and seven-byte retained-capacity failures, then retries
  to 19. Protocol rejects occur before handler execution.
- Scratch reuse after poisoning leaves retained state intact, turns suspend with
  zero outstanding leases, and deterministic cleanup leaves no live owners.
- Native `sizeof`/`offsetof` output matches independent module and mailbox
  fixtures; two fresh artifact builds have matching fingerprints and semantic
  metadata, with optimization reported separately.
- O0/O2 runner import tables contain no CLR or managed runtime imports; report
  allowed Windows loader/thread imports explicitly.
- Run the focused verifier plus the existing execution-ABI conformance and
  mailbox-suspension verifiers when the new APIs are available. Keep old
  experiment/report files byte-for-byte unchanged.

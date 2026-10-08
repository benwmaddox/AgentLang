# 130 — Compiler-proven stable arena rewinds

Status: focused native acceptance and the full local Release gate passed.
Tested working tree based on `34b51c5`, with the source hashes retained in the
focused verification evidence. This is native memory conformance evidence,
not a new agent-efficacy or service-performance result.

## Outcome

The bounded owning backend now keeps inline payloads at stable arena locations
through ordinary bindings, local reads, field projections and same-arena calls
and returns. Small internal descriptors carry locations. Both fixed-record and
String programs use this policy; neither introduces source references, shared
record graphs, reference counting or runtime liveness scans.

A compiler provenance analysis authorizes saved-mark rewinds only when the
whole suffix is dead. Escaping results preserve temporary bytes beneath them.
Uncertain calls and lifetimes conservatively retain storage. Request completion
resets the arena after publishing independent retained output. Record
construction, explicit independent duplication and external retention can still
copy. See the [lowering contract](../docs/STABLE-ARENA-LOWERING.md).

This replaces report 129's eager packed placement. It implements the clarified
rare-movement direction within the supported IR subset; it does not yet integrate
owning values into the standalone mailbox/async runtime.

## Measurements and interpretation

| Correctness fixture | Measured result at LLVM O0/O2 |
| --- | --- |
| Six fixed inputs, nested dead scopes | 48 input bytes; peak cursor 64; allocation after inner rewind reuses offset 48; six older input locations preserved |
| Six String inputs, nested dead scopes | 96 input bytes; peak cursor 112; later literal reuses offset 96; six older input locations preserved |
| String binding and return | One 24-byte literal stays at its original address; exactly three 20-byte descriptor transfers; zero payload relocation |
| Wrapped concat of two 16-byte Strings | Peak cursor 48 bytes, compared with 144 in report 129's wrapped fixture; new output construction occupies 16 bytes |
| Fixed transition sequence | Cursor requirement `32 + 40 + 64*N + 48`: 184 bytes for N=1; 632 for N=8 |

The fixed and String dead-scope cases also prove that a later allocation really
uses the rewound address, followed by reads at all six original input offsets.
Escaping-result and projected-field cases prevent unsafe rewinds. Binding tests
correlate the original literal allocation, ordered store/load/frame-result
transfers, and retained publication at the same address. Projection tests tie
fields to a preceding descriptor at the parent base.

N=8 means eight transitions within one invocation, not eight separate requests.
The old 512-byte control succeeds for N=1 and rejects N=8 at the first attempted
reservation of 520 bytes. The full sequence needs 632 bytes, tested with 1024
bytes available. Independent repeated-completion checks test request reset and
capacity reuse. **512 bytes is a tiny correctness fixture, not a service limit.**
The small absolute increase is acceptable to the user; the next performance
comparison must use realistic request sizes, concurrency, slow I/O, throughput,
tail latency and total process memory.

`DeepCopyBytes` counts complete copied/constructed storage extents, including
String headers and padding. It is neither exact hardware memory traffic nor a
count only of redundant copies. `MoveBytes` measures payload relocation.
Descriptor traffic, input staging, retained output, layout scanning and diagnostic
poisoning have additional costs. Exact unique-live payload, logical-root and
interior-dead-byte accounting is explicitly unavailable: overlapping descriptor
sizes cannot be summed as unique live bytes. Occupied cursor measurements remain
available. No collector was added to manufacture a live-byte measurement.

## Validation

The final focused run is
`verification-61fac6dad198462e8ec12a426f8b808c` under
`.agentlang/owning-stack-003/`. It records unchanged source hashes before/after.

- `pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1`: 35 verifier checks
  passed; 626 boolean native experiment assertions passed at O0/O2, with no
  failures. The JSON has 844 entries, of which 218 are layout summaries rather
  than assertions. Covers interpreter parity, Unicode including isolated
  surrogates, nested/empty layouts, branches, locals, calls, capacity failures,
  unchanged retained output on failure and repeated reset.
- Compiler arena-lifetime tests: 33 assertions passed after a fresh Release
  build, covering dead/escaping scopes, branches, shadowing, projected fields,
  uncertain/effectful calls and duplicate-site fail-closed handling.
- Raw native C storage suite: 43 cases and 507 assertions passed in each of O0,
  O2 and UBSan trap mode. Seven added cases contribute 54 assertions for the
  source-preserving copy helper, complete extent/padding copies, disjoint ranges,
  counter attribution and invalid-range rejection. Normal Windows UBSan linking
  remains unavailable; the separate linker failure is recorded, not passed.
- Negative control: substituting a source-invalidating move for the new copy
  helper compiles but fails the source-preservation assertion as expected.
- Fresh established LLVM regression suite: 476 assertions passed. This separate
  suite does not substitute for stable-arena acceptance.
- Fresh IR verifier regression suite: 193 assertions passed. Independent review
  found that duplicate detached instruction source IDs could merge distinct
  lifetime decisions. The verifier now rejects them with
  `IR_DUPLICATE_SOURCE_SITE`; the analyzer also combines duplicate decisions
  conservatively. The regression uses a real compiled escaping/dead-scope body.
- Independent emitter/lifetime review and a narrow follow-up acceptance review
  found no remaining concrete blocker. Two initially weak address predicates
  were strengthened before the final run; test failures during that tightening
  were resolved by ordering allocation selection after the relevant rewind.

The full local gate passed all 37 checks, including 98 business-policy preflight
checks across 30 independent outcomes. It ran from 13:16:12 to 13:49:05 local
time on 2026-10-08, using:

```text
pwsh -NoProfile -File scripts/Validate.ps1 -SerialBuild -SkipPackageAudit -ReportPath .agentlang/owning-stack-003/full-validation/validation.json
```

No CI is used. Package vulnerability auditing is disabled for this run; the
local tests do not replace that network audit.

After the full gate, staging detected an extra blank line at the end of the new
arena-lifetime test file. Only those trailing line endings were removed; the
archived before/after check proves all other characters identical. The focused
gate was rerun successfully against the final source snapshots. No executable
code changed after the full gate.

The [evidence manifest](evidence/130-stable-arena-rewinds/storage-index.json) and
[compressed archive](evidence/130-stable-arena-rewinds/130-stable-arena-rewinds.zip)
preserve tested source snapshots, generated LLVM, focused and full-gate evidence,
component logs, review notes, and the source-preservation negative control.
The manifest records raw payload hashes; sidecars record stored archive hashes.
Later mailbox-storage draft work is excluded from this milestone and its archive.

## Research assessment and next step

The implementation now matches the intended allocation behavior more closely:
keep payloads stable and reclaim only compiler-proven suffixes or completed
regions. Lower copy/cursor counts in a small concat fixture do not prove higher
throughput or lower process RAM. Conservative interprocedural analysis can retain
more dead space; safe call summaries and destination-directed construction are
possible later optimizations, not implemented claims.

The next useful native experiment is owning-value mailbox integration followed
by a realistic bounded comparison of keeping scratch across async waits versus
returning scratch to a pool. Measure completed work at the same memory allowance
and acceptable tail latency. Avoid polishing tiny fixture byte counts first.
The current controller calls graph-based ABI3 entries; the owning backend exposes
a separate serialized-value entry and its managed adapter allocates per call.
The bridge should retain one mailbox lifecycle controller, add a bounded owning
storage adapter with caller-provided buffers, and exercise dynamic String
request/response retention through suspension. Account for external UTF-8 versus
internal UTF-16 conversion and all retained-boundary copies. Neither suspension
policy is selected as the performance winner before a matched comparison.

The latest external-agent efficacy result remains [report 125](125-matched-pair-repair.md):
six agents passed all 12 independent cases, and both retained Flow and F# reused
existing vocabulary. No comparative reliability advantage was established.
Memory work does not change that conclusion or replace research on reliable edits.

Opt-in `let result = compact { ... }` is a future candidate: block results survive
and compiler proof must authorize relocation. Ordinary returns never compact.
Koka/Perceus-inspired last-use reuse is also deferred; no reference-counting
mechanism is adopted. Both are documented in the roadmap, outside this milestone.

# Arena allocation assessment

Date: 2026-10-05. Status: proposed design and research scope, not implemented allocation behavior.

The user proposed arena allocation and bulk dumping as a useful part of the language. This is a plausible fit for compiler scratch, isolated tests and bounded temporary evaluation data because these phases have identifiable end points. A scoped arena can provide simple allocation and bulk reclamation; [bumpalo's documentation](https://docs.rs/bumpalo/latest/bumpalo/) gives a concrete reference implementation and explains the shared-lifetime constraint.

The user clarified that these natural cleanup opportunities were part of the original rationale for considering Forth source. The frontend replan therefore retains local data flow, limited hidden mutable state, and a future inspectable lifetime/escape analysis boundary in semantic IR. Dot notation should preserve this objective; the allocator must account for all aliases and escaping values rather than interpreting a stack pop as permission to reset storage.

The important design boundary is escaping data. Returned values, accepted definitions/constants, application state, logs and old executable generations must retain valid ownership after scratch reset. Deep promotion must not leave nested fields referencing discarded memory, must preserve typed value meaning, and must be bounded. Old generations must remain live while executions or rollback/snapshot state reference them. [The proposed region contract](../docs/MEMORY-REGIONS.md) records these requirements.

Bulk memory reclamation does not perform resource cleanup or reverse effects. Nor does an arena guarantee low peak memory: intermediate garbage may accumulate until reset, promotion may duplicate live data, and reusable capacity may remain reserved. Measure cleanup/allocation time, promotion cost, used/reserved/peak memory, and repeated-workload growth.

The current managed F#/.NET representation remains unchanged. [.NET garbage collection](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals) controls reclamation of managed objects; clearing roots is not an immediate bulk-free mechanism. Real native arenas remain an implementation candidate for the later backend, while region safety should be specified and tested first. This proposal does not delay the user-requested early dot/data-flow frontend switch or introduce a new arena syntax now.

## Forth lifetime clarification

The follow-up discussion distinguishes a safety condition from a mandatory
compiler algorithm. Standard Forth's [`DROP`](https://forth-standard.org/standard/core/DROP)
removes a stack item, while the [optional memory-allocation word set](https://forth-standard.org/standard/memory)
uses explicit allocation and release. Dropping an address does not release its
allocation or prove that no copies remain. Forth programs must respect the
lifetime of allocated storage; the standard does not establish a static
ownership checker. Inline scalar stack slots are simpler and can be reused
without analyzing a referenced heap graph.

Our proposed improvement is enforced lifetime safety with understandable rules.
Conservative region types, valid ownership transfer, bounded copying and checked
dynamic handles are candidates; a full borrow-checking annotation system is not
assumed. The first region experiment should make surviving results acquire
longer-lived ownership before scratch reset and test aliases, nested containers
and error exits. Lexical scopes being added for the frontend are not arena resets.

Validation for this documentation update: reviewed the cited Forth standard
contracts and ran `git diff --check` on the changed documentation/report. The
previous published design commit `4d9048584cf60bfaa61c783eadcf909365f9670d`
passed main CI run [37315448742](https://github.com/benwmaddox/AgentLang/actions/runs/37315448742).
No allocator or native-memory performance claim is made.

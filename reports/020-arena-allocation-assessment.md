# Arena allocation assessment

Date: 2026-10-05. Status: proposed design and research scope, not implemented allocation behavior.

The user proposed arena allocation and bulk dumping as a useful part of the language. This is a plausible fit for compiler scratch, isolated tests and bounded temporary evaluation data because these phases have identifiable end points. A scoped arena can provide simple allocation and bulk reclamation; [bumpalo's documentation](https://docs.rs/bumpalo/latest/bumpalo/) gives a concrete reference implementation and explains the shared-lifetime constraint.

The user clarified that these natural cleanup opportunities were part of the original rationale for considering Forth source. The frontend replan therefore retains local data flow, limited hidden mutable state, and a future inspectable lifetime/escape analysis boundary in semantic IR. Dot notation should preserve this objective; the allocator must account for all aliases and escaping values rather than interpreting a stack pop as permission to reset storage.

The important design boundary is escaping data. Returned values, accepted definitions/constants, application state, logs and old executable generations must retain valid ownership after scratch reset. Deep promotion must not leave nested fields referencing discarded memory, must preserve typed value meaning, and must be bounded. Old generations must remain live while executions or rollback/snapshot state reference them. [The proposed region contract](../docs/MEMORY-REGIONS.md) records these requirements.

Bulk memory reclamation does not perform resource cleanup or reverse effects. Nor does an arena guarantee low peak memory: intermediate garbage may accumulate until reset, promotion may duplicate live data, and reusable capacity may remain reserved. Measure cleanup/allocation time, promotion cost, used/reserved/peak memory, and repeated-workload growth.

The current managed F#/.NET representation remains unchanged. [.NET garbage collection](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals) controls reclamation of managed objects; clearing roots is not an immediate bulk-free mechanism. Real native arenas remain an implementation candidate for the later backend, while region safety should be specified and tested first. This proposal does not delay the user-requested early dot/data-flow frontend switch or introduce a new arena syntax now.

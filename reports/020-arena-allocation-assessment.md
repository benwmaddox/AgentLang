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

## Additional optional research candidates

The subsequent discussion raised arena-only language allocation and a strictly
single-threaded input/output mailbox with one arena per handled item or request.
Neither is a firm requirement. The memory proposal now records both candidates
without changing V1 behavior. Region-only allocation could reduce ownership
bookkeeping but needs enforced non-escape and a retained-result policy; huge
arenas can accumulate dead intermediates despite a small live stack. Sequential
mailbox handling offers a clearer phase boundary, while queued outputs and
mailbox state need storage independent of released item scratch. Queue budgets,
failed-item behavior and synchronous-wait restrictions must be specified before
implementation. No actor framework, asynchronous execution or allocator was
added for this discussion.

## Explicit stack-only research request

The user subsequently required research into **no language heap and only a
program data stack**, and clarified that the data stack may live in an arena.
This is now a distinct named track in the PRD/requirements and
[STACK-ONLY-RESEARCH.md](../docs/STACK-ONLY-RESEARCH.md), rather than being treated
as synonymous with general arena-only allocation. The investigation is required;
adopting its restriction remains undecided. Its acceptance covers compound
values, aliases/returns, adverse retention patterns, persistent project state,
cleanup safety, measured copying/compaction and agent usability. No allocator
implementation or completed experimental result is claimed.

## Declared retained-state mailbox option

The user added an optional candidate: a declared static set of retained data
outside processing arenas, with no independent language object heap for other
values and an arena-backed program data stack for transient work. The research
plan now compares this with strict LIFO storage and retained arenas. Stasis is
the user's analogy; its implementation has not been evaluated here.

The promising part is explicit, inspectable long-lived capacity combined with
per-item scratch reclamation. The unresolved part is variable-sized retained
data: strings, lists, queue payloads and nested values need bounded layouts and
validated copying, rather than pointers into reclaimed scratch. Capacity
failure, state/output publication, schema changes, reload and snapshots require
defined behavior. This remains a proposed experiment, not an allocator change,
memory-use result or exception to the explicit-state/no-mutable-globals plan.

Publication check: GitHub reported this repository as public during preparation
of this update, contrary to the user's private-repository requirement. The
coordinator changed visibility to private and confirmed `isPrivate: true` before
pushing. This corrects current access; it does not prove that previously public
content was never accessed.

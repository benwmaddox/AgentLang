# Late application migration research

Status: user-requested future research, documented 2026-10-07. No migration,
native runtime implementation or application benchmark was performed here.

## Placement and purpose

Make a sustained migration of a substantial existing application a late,
potentially final stage of evaluation. Start only after language semantics and
authoring are stable, and the native runtime implements and validates the
selected arena and program-data-stack model. The current managed interpreter
cannot establish those allocation or cleanup properties. Specify mailbox
message transfer, retained-state lifetimes and capacity behavior before using
them in the migration; mailbox architecture remains subject to its research
and design gates.

The study should determine whether a compact typed core, discoverable growing
vocabulary and strict library contracts remain useful under realistic
migration and maintenance pressure. Reliability is primary. Memory behavior
and operational viability are additional outcomes; speed or cheap generation
must not compensate for incompatible or incorrect behavior.

## Candidate and evidence boundary

The user nominated [basecamp/once-campfire-rust](https://github.com/basecamp/once-campfire-rust).
Its README describes compatibility with existing Rails data and cookies,
WebSockets, and a parity harness comparing rendered and protocol behavior.
These make it a promising candidate for independently checking a port. This
review read the repository landing page and README; it did not audit source,
reproduce performance claims or verify the X posts mentioned by the user.

The user subsequently supplied the original
[DHH X conversation](https://x.com/dhh/status/2104633108092092880). Its link,
access limitations, discussion leads and conditions for a possible future
public response are saved in the [discussion history](../docs/CAMPFIRE-DISCUSSION-HISTORY.md).
Replies and quote posts are research leads; claims require independent evidence.

Before starting, pin the Rust and Rails reference revisions, inspect licensing,
inventory dependencies, validate the reference locally and assess which parity
tests can serve as independent oracles. Select a feasible initial vertical
slice. Whole-application migration is the eventual study ambition, not a
promise that the existing language can currently support the application.

## Proposed execution

1. Freeze reference behavior, workloads, compatibility exceptions, independent
   acceptance tests and resource budgets before observing candidate results.
2. Migrate bounded slices with external AI subagents, preserving failures and
   recording missing primitives, host additions and language changes. Keep
   authored application behavior above the trusted boundary.
3. Expand only after each slice passes independent parity and regression
   checks. Validate native semantics against the interpreter where both
   support the workload; report unsupported behavior explicitly.
4. Run prolonged, repeatable workloads with queue pressure, idle periods,
   repeated arena reclamation and injected effect failures. Check escaped
   references, retained state, responses and resource cleanup against the
   adopted contracts. Distinguish reserved backing capacity from live memory.
5. Give fresh agents subsequent fixes, features and refactorings in the growing
   vocabulary. Compare equivalent maintenance tasks in the pinned conventional
   project. Record discovery failures, unsafe reuse, regressions and gate
   friction as well as useful abstractions.
6. Save periodic reports and raw evidence, ending with a whole-study review
   rather than judging success from a port demonstration.

## Required outcomes and stopping rules

Predeclare workload duration, trial counts, hardware, build settings,
acceptance criteria and stopping rules when the prerequisites are met. No
duration, memory threshold or performance improvement is claimed now.

Measure independently correct changes and preserved unrelated behavior;
vocabulary reuse, duplication and discovery burden; missing capabilities and
trusted-core growth; startup, latency and throughput; used/reserved/peak
memory, allocation and reclamation; queue/state capacity failures; and crashes,
resource leaks and semantic mismatches. Include host-library allocations in
the system accounting. Agent token/turn metrics require actual harness data
and remain secondary.

Stop or reconsider if compatibility cannot be maintained, the native lifetime
model is unsafe or impractical, the compact core expands uncontrollably,
agents repeatedly bypass vocabulary, or maintenance reliability does not
improve. Report those outcomes without repairing trial evidence or changing
acceptance criteria after results are known.

Documentation-only validation: inspect the PRD link and run `git diff --check`.
Existing runtime validation is recorded separately in report 086; it does not
validate this proposed migration or a native allocator.

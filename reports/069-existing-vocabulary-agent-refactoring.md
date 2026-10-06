# Fresh-agent refactoring with retained vocabulary

2026-10-06. A fresh external Luna/max agent consolidated duplicated classification
and discount logic using existing words. Both operations preserve behavior and
identity, and every existing test remains byte-identical. Independent acceptance
and dependency-graph checks pass. This is one Category E feasibility outcome,
not a conventional comparison or efficiency result.

## Starting state and protocol

The [preparation script](evidence/069-prepare-refactor-study.ps1) copies the
accepted debugging project and installs behaviorally equivalent duplicate
implementations through normal typed replacement and library gates. Both words
directly compare customer kind with `premium`; renewal also duplicates the
discount multiplication. The existing `customer.premium?` predicate remains
available but unused by those bodies. No tests are changed.

The [setup transcript](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/fixture-setup.json)
proves 21/21 self-tests and complete own coverage: discounted balance 12/12
instructions, 2/2 branch outcomes; renewal 20/20 instructions, 4/4 outcomes.
Independent [helper](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/before-task-2.json)
and [caller](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/before-task-4.json)
acceptance pass 96 checks over 40 vectors before launch. The
[no-op control](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/no-op-state-audit.json)
is correctly rejected by refactor acceptance even though all behavior passes.

Fresh source `9c20fba` builds with zero warnings/errors. CLI SHA-256 is
`f14818e5096b441f2452749f69dddfef93c4f5ba3e3f95d2d6742ff511cad38c`;
Core is `32e4cab881c6ae7077b011a1e6a30f6fc2e1128f9b1fbef6a8ded9123979e955`.
The [prelaunch record](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/prelaunch.json)
archives the exact prompt, starting inventory, source/runtime/host pins, model,
no inherited turns and allowlist before actor launch. The actor receives the
[public refactoring contract](../experiments/AgentLang.SubagentTrials/refactor-flow-001/public-task.md)
and is instructed to work through only its isolated project's protocol. The
host uses no effects, a fixed clock, 100 exchanges and a 120-second exchange
deadline. No cumulative inspection cap is enabled. History, diff and dependency
graph queries are permitted; availability is not evidence that every command
was used.

## Verified outcome

The agent discovers and uses the retained predicate, then routes renewal through
the shared discounted-balance word. No new predicate, workaround or word is
created. Stable IDs remain unchanged, with discounted balance revision 4 -> 5
and renewal revision 3 -> 4. All unrelated helpers and record definitions are
unchanged; immutable history is retained. All existing case names, source
objects and examples are unchanged.

The new exact direct dependency sets are:

```text
customer.discounted-balance
  customer.balance
  customer.premium?
  float.multiply

customer.renewal-balance
  customer.discounted-balance
  float.multiply
  subscription.annual-renewable?
```

Direct `customer.kind` and `equals` calls disappear from both bodies. Each
revised word covers 10/10 own instructions and 2/2 branch outcomes. Existing
own suites pass 6/6 and 8/8 respectively; all 21 project tests pass in fresh
independent acceptance. Smaller own IR bodies demonstrate delegation into named
abstractions, not lower transitive work, memory use or faster execution.

After durable task commit, independent
[helper acceptance](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/after-task-2.json)
and [caller acceptance](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/after-task-4.json)
again pass 48 checks each over 40 vectors total. The
[state/structure audit](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/preservation.json)
passes 88 checks: exact retained type/unrelated-word/test/example/history state,
stable inventory/identities, in-place definition revisions, and exact compiled
dependency sets. Its [script](evidence/069-audit-refactor-state.ps1) checks current
manifests and content hashes alongside behavioral acceptance.

The [trace audit](../experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/trace-audit.json)
verifies 36 exchanges, 2,378 request payload bytes, 39,563 selected response
payload bytes and zero diagnostic responses. It reuses the
[schema-1 audit](evidence/068-audit-debug-trace.ps1) to check byte framing/hashes,
runtime/host/prompt/configuration pins and prelaunch ordering. Schema 1 proves
selection before stdout flush, not delivery or model consumption. Exact model
tokens, turns and controlled duration are unavailable. Coordinator Ctrl+C on
the same host session after completion ends host and runtime with exit code
zero, with no extra JSONL request.

## Observability finding and next step

The actor repeatedly reran individual suites and inspected coverage because
testing one word made the other display zero coverage with `status: not-run`.
The trace confirms each own suite had passed with full coverage. Source review
explains the display: [test dispatch](../src/AgentLang.Core/Runtime.fs#L3768)
replaces `lastResults` with one batch, and
[describe](../src/AgentLang.Core/Runtime.fs#L2228) derives coverage only from that
batch. It does not preserve each word's earlier successful evidence. Library
publication still executes and gates its own tests; fresh `test-all` proves
simultaneous current own coverage in independent acceptance.

The current [primer](../experiments/AgentLang.SubagentTrials/selective-flow-001/language-primer.md)
now explains that scope and recommends `test-all` for inspecting several words
together. Frozen prompts/traces are unchanged. A future metadata improvement
should make evidence scope explicit rather than silently suggesting an earlier
suite never ran; retaining per-word proofs needs careful snapshot invalidation.
No coverage-cache behavior is changed in this milestone.

Clean [main CI](evidence/068-main-ci.json) passed all 32 checks at `9c20fba`;
the [artifact](evidence/068-main-validation.json) records `dirty: false`. This
milestone adds experiment evidence, audits and documentation, with no F# or
host changes. The observed result supports maintenance by composition and
preserved tests, with no evidence of a general cost advantage.

A read-only Luna/max planning pass identified the
[next strong-type trial](../docs/STRONG-TYPE-AGENT-TRIAL-PLAN.md): validated Email,
distinct speed units, explicit conversion and wrong-unit/type rejection using
existing Flow facilities. It is a plan, not an executed result. The full strong
business domain, stateful tasks, complete debugging/refactoring suite, controlled
costs and model-context comparisons remain required. The full PRD goal stays active.

Publication follow-up: clean [main CI](evidence/069-main-ci.json) for `8d52195`
completed successfully. Its [validation artifact](evidence/069-main-validation.json)
records all 32 checks passing with `dirty: false` at that exact revision.

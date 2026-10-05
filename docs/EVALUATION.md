# Controlled evaluation protocol

This document specifies how to evaluate the hypothesis before collecting results. It is not a results report. The original PRD ledger remains the scope of delivery.

## Paired environments

The prototype's first AI experiments use fresh Codex subagents, as requested by the project owner. AI remains outside the language: a subagent builds and tests words through the runtime protocol. The optional HTTP provider is an alternative integration, not a prerequisite for these experiments.

Save the exact experiment prompt, supplied language primer, model/reasoning configuration when known, runtime requests and responses, initial/final project state, elapsed time, and independent acceptance results. Start each trial without inherited task history where the subagent interface permits it. Record any reused agent or inherited context as a trial limitation.

Subagents may have broader host tools than the experiment protocol. A request to use only the protocol is an instruction rather than an enforced sandbox; disclose this and record observed source/tool fallbacks. Supplied prompt and retrieval bytes can be measured, but framework context, exact model turns, and token usage must remain unavailable unless the interface reports them. Do not substitute runtime calls for model turns or byte estimates for observed tokens.

Use the same provider, explicit model, reasoning configuration, task intent, deterministic input data, and independent acceptance oracle for each mode. Record their exact versions and the repository revision. Give each agent equivalent high-level instructions and a compact initial project description.

| Mode | Starting environment | Accepted changes after a task |
| --- | --- | --- |
| Flat (original PRD mode A) | Trusted primitives plus declared input types/data and their generated constructors/accessors; no authored domain algorithms | Restore this primitive baseline before the next task |
| Growing (original PRD mode B) | The same primitive baseline at the beginning of the sequence | Retain passing agent-created vocabulary for subsequent tasks |
| Conventional (original PRD mode C) | Equivalent F# types/data and basic capabilities, without supplying task solutions or additional domain algorithms absent from the language baseline | Retain passing code for subsequent tasks |

The original primitive-only Flat mode must not be replaced by a resettable, authored business library and then reported as mode A. A domain-seeded retention comparison is a useful additional control, but must be labeled separately and publish its initial authored words. The complete business-language fixture remains a required application deliverable; exposing all its algorithms to every mode is a separate experimental condition, not evidence for primitive-only Flat versus Growing. Debugging/refactoring trials necessarily supply defective or duplicated implementations; record these task-specific starting snapshots and report those trials separately from primitive-baseline sequence results.

The harness separates Flat/Growing retention from the `primitive-only` and `domain-seeded-control` starting profiles. Primitive-only fresh origins reject authored words, tests, and examples before provider calls, using authoritative storage rather than the advisory text export. Verified Growing continuations retain accepted vocabulary and report both origin and current inventories; durable-state lineage detects untracked changes. A supplied seed hash is distinguished from whether that seed was actually applied. These controls enforce baseline labeling, not matched fixture semantics or an OS security boundary. Prior seeded scripted checks remain infrastructure evidence only; the schema and independent acceptance adapters must still be matched before comparisons.

A task's success is determined by the independent oracle, not the agent's final claim or its own test suite. Preserve failed runs, tool errors, timeouts, and unavailable provider usage. Keep agent-visible tests separate from hidden task acceptance tests. Verify fixture equivalence before making mode comparisons.

Sequence tasks must remain solvable in Flat mode: put previous product requirements in the task's acceptance contract even when no previous implementation is retained. Independent debugging/refactoring tasks instead start from a specified defective or duplicated snapshot. Record snapshot hashes and expected prerequisites. Do not reset Growing while retaining Conventional, or quietly remove difficult tasks after observing results.

## Pilot and complete suite

First run the five-step premium classification, discount, renewal eligibility, annual-renewal discount, and reminder sequence. Use it to find language/tooling failures and missing primitives. Freeze a new fixture and harness revision after any repairs, and rerun comparison pairs affected by those repairs.

The complete suite contains 20 simple tasks, 20 medium tasks, 10 debugging tasks, and 10 refactoring tasks. The [task bank](BENCHMARK-SUITE.md) contains this inventory and 180 proposed hidden acceptance cases; all execution adapters and snapshot pins are pending. Shape validation does not establish expected-answer correctness. Discovery and vocabulary growth are additional dimensions across these categories. Every task needs a deterministic oracle and a written starting-state contract. Generate no success claims from placeholder tasks or scripted agents.

Rotate mode order across repeated trials. Preserve all configured trials, including failures. Report the number of independent trials and model responses; a small pilot cannot establish general performance or reliable confidence intervals. Keep the same completion budgets across modes, including maximum turns, tool calls, and output allowance. State whether failed attempts are allowed to continue or are reset.

## Context accounting

Record each actual request's supplied system/developer prompt, tool schemas, user task, conversation history, and tool results. Record both raw UTF-8 bytes and provider-reported input/output/cached token usage. Initial context, retrieved context, generated output, and repeated-history input costs are different quantities and must remain distinguishable.

An application byte cap or approximate tokenizer cap is not a native model context window. Label estimates with their method and report missing exact counts as unavailable. For 2k/4k/8k/16k/32k token trials, use a documented compatible token counter and include every supplied context component. If exact preflight accounting is unavailable, retain the trial as exploratory with its actual observed usage rather than claiming an enforced token window.

Do not use server-side conversation state to supply unaccounted history. Preserve reasoning/tool-call items required by the provider and include their observable cost in accounting. Any history truncation policy must be fixed before the trial, preserve valid call/result pairs, and apply consistently across modes. Log when a cap stops a run.

## Outcomes and decision rules

Report task success alongside token usage, turns, tool calls, elapsed duration, modifications, compile/test failures, reversions, missing primitive requests, and raw source fallbacks. Unknown counts are unavailable, never zero. Measure task overhead and independent acceptance overhead separately. Cost distributions should include failures and also identify the cost of successful tasks; cheap failure must not be rewarded as improvement.

Compute paired differences for the same task/trial/model configuration. A promising PRD signal is at least 20% less input usage or turns with comparable success. For a full sufficiently replicated suite, use a predefined success noninferiority margin of five percentage points, report the uncertainty interval, and do not declare equivalence from a nonsignificant difference alone. If sample size cannot support that assessment, report descriptive evidence and the limitation.

Report the cost trend over task position alongside task difficulty and success. Retention is supported only if later correct changes become cheaper on comparable tasks. Record reuse as calls to domain words present at task start divided by all domain-word calls, excluding primitives and tests. A zero denominator is unavailable. Static primitive expansion is structural compression rather than executed work.

Track pollution through unused words, structural duplicate warnings, near-duplicate names/signatures, and short-lived replacements. Track recovery using linked attempt/error/repair events, include unresolved errors, and label inferred resolution. Use later reuse, callers, lifetime, and independent correctness as quality evidence; word count alone is not quality.

The final report answers RQ1–RQ10, identifies supportive and contrary evidence, and supplies enough traces and snapshot metadata to reproduce the comparisons. A negative outcome is a valid research result. A functioning language with no measured agent comparison remains an implementation milestone.

# Controlled evaluation protocol

This document specifies how to evaluate the hypothesis before collecting results. It is not a results report. The original PRD ledger remains the scope of delivery.

## Paired environments

Use the same provider, explicit model, reasoning configuration, task intent, deterministic input data, and independent acceptance oracle for each mode. Record their exact versions and the repository revision. Give each agent equivalent high-level instructions and a compact initial project description.

| Mode | Starting environment | Accepted changes after a task |
| --- | --- | --- |
| Flat | AgentLang primitives plus fixed domain fixture | Restore the starting fixture before the next task |
| Growing | The same initial AgentLang fixture | Retain passing vocabulary for subsequent tasks in a sequence |
| Conventional | Equivalent F# domain fixture | Retain passing code for subsequent tasks in a sequence |

A task's success is determined by the independent oracle, not the agent's final claim or its own test suite. Preserve failed runs, tool errors, timeouts, and unavailable provider usage. Keep agent-visible tests separate from hidden task acceptance tests. Verify fixture equivalence before making mode comparisons.

Sequence tasks must remain solvable in Flat mode: put previous product requirements in the task's acceptance contract even when no previous implementation is retained. Independent debugging/refactoring tasks instead start from a specified defective or duplicated snapshot. Record snapshot hashes and expected prerequisites. Do not reset Growing while retaining Conventional, or quietly remove difficult tasks after observing results.

## Pilot and complete suite

First run the five-step premium classification, discount, renewal eligibility, annual-renewal discount, and reminder sequence. Use it to find language/tooling failures and missing primitives. Freeze a new fixture and harness revision after any repairs, and rerun comparison pairs affected by those repairs.

The complete suite contains 20 simple tasks, 20 medium tasks, 10 debugging tasks, and 10 refactoring tasks. Discovery and vocabulary growth are additional dimensions across these categories. Every task needs a deterministic oracle and a written starting-state contract. Generate no success claims from placeholder tasks or scripted agents.

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

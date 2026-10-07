# Read-only recommendation for the next agent study

Status: independent Luna/max planning review; no study launched or frozen here.

The reviewer recommends S01 (premium classifier) followed by S07 (discounted
balance). Prior S07 reused the S01 classifier, while it did not reuse S06's
discount-basis-points word. This sequence targets observable vocabulary reuse.

Proposed arms:

- Flat resets to the six-type schema-only seed before each task.
- Retained begins with a frozen rich seed and passes only independently accepted
  S01 output to a fresh S07 agent.
- Reset-rich uses that same rich seed for both tasks, discarding S01 output before
  S07. Verify that the rich seed lacks the target vocabulary before freezing it.

Two blocks with rotated arm order would require 12 fresh actor-task runs. All
actors use Luna/max without inherited history. Freeze prompts/help, runtime,
seeds, model request, clock/capabilities, host limits, oracles and host/auditor
hashes. Run sequentially, independently accept on disposable copies, and require
explicit v2 host.close with passing termination audit and host/runtime exit 0/0.
An unsuccessful S01 contributes no code. Preserve and pause on preflight or audit
failure; stop after the declared two blocks.

Record independent behavior/metadata, own tests and coverage, dependency/source
evidence of reuse, diagnostics, help queries, mutations, exchanges and payload
bytes. Mark model tokens, turns, effective context and model latency unavailable
unless exposed by the provider. Bytes and host lifetimes are not substitutes.

This is a small task-specific exploratory recommendation. All arms share help,
so it cannot estimate help's effect. It omits a new conventional arm and cannot
establish conventional-language superiority, broad efficiency/context gains or
generalization across tasks/models. The main agent must resolve the final study
scope and controls before implementation and freezing.

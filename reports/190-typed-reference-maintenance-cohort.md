# 190 — Fresh typed-reference maintenance cohort

Status: frozen before dispatch for a new six-participant comparison. No
participant result is claimed here.

The [persisted migration control](189-persisted-migration-readiness.md) passed,
so the capability gap from [trial 185](185-typed-reference-maintenance-results.md)
no longer prevents the held-out maintenance task. This cohort reuses the original
unmigrated seeds, semantic prompts and independent 18-case oracle while changing
the AgentLang runtime to the accepted atomic-record-evolution implementation.
It is a separate study; the interrupted participant is not pooled into it.

Two fresh external GPT-6-Luna/max subagents run in each condition, in order:
retained AgentLang, reset-rich AgentLang, F#, F#, reset-rich AgentLang, retained
AgentLang. Retained provides a documented, tested reference-lookup abstraction;
reset-rich exposes lower-level traversal. F# supplies the equivalent reusable
helper in conventional source. These are setup-created abstractions, so the
study tests availability and discovery, not organic vocabulary accumulation.

Each participant receives its own copied project, a single existing V2 broker,
the fixed clock, empty capabilities and a 100-exchange limit. Participants get
no oracle feedback or migration solution. All discovery, editing and validation
must go through the broker. The original task asks for a nominal tracking
reference, shipment-scoped replay idempotence, preserved error precedence,
ordered batch behavior and preservation of inherited assertions and examples.

AgentLang uses the binaries accepted in report 188. A fresh serial Release build
of the conventional CLI passed with zero warnings/errors. Baseline checks on
isolated copies passed: 21 attached retained tests, 18 reset-rich tests, and
eight F# baseline assertions. An initial smoke-script assertion incorrectly
expected 21 reset-rich tests; it stopped despite all 18 passing. The corrected
smoke passed all three conditions. Both attempts' outputs remain in the evidence.

The independent model and case files retain their original hashes. The scorer's
only change redirects output to the new study directory. Capture still requires
a terminal broker trace, refuses to overwrite snapshots, copies hidden persisted
files and verifies source/trace preservation.

Behavior, nominal type use, inherited evidence, library qualification, task
finalization and recovery are separate outcomes. Broker exchanges, bytes and
duration do not measure model tokens or context size. This small pilot can
expose failure modes; it cannot establish a general reliability ranking or
performance advantage.

The trial introduces no language host capabilities or network access. The
conventional arm executes its fixed local validation command; neither condition
is presented as an operating-system sandbox. CI remains manual, and validation
is local.

Study sources: [plan](../experiments/AgentLang.SubagentTrials/typed-reference-maintenance-190/study-plan.md)
and [preparation script](../experiments/AgentLang.SubagentTrials/typed-reference-maintenance-190/prepare_trials.py).
Frozen inputs and baseline receipts: [amended archive index](evidence/190-typed-reference-maintenance-cohort/amended-index.json)
and [evidence bundle](evidence/190-typed-reference-maintenance-cohort/evidence-v2.zip).
All 297 archive members were checked against their recorded byte hashes. The
actor manifest was verified before any broker launch. A preparation attempt
stopped before trial creation because strict Python path resolution failed in
the managed sandbox; explicit file and reparse-point checks plus content hashes
were retained when switching to non-strict resolution. The successful preparation
did not dispatch participants.
The freeze was amended before dispatch solely to remove a trailing blank line
flagged by the Git whitespace check. The original archive and index are retained;
no seed, prompt, protocol setting or semantic implementation changed.

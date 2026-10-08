# Finite-coverage adoption probe

Status: design fixed before actor dispatch; preparation and independent controls pending.
Source checkpoint: a81a7aad74119f48d52f851e629d96f702ffda5d.

Question: can one fresh external subagent use runtime discovery and qualification
feedback to add useful regression tests, qualify a reachable finite contract, and
recognize an unreachable declared alternative without changing behavior?

The seed contains two correct project functions with passing attached tests.
`flags.agree` is Boolean equality; its two initial equal-input cases omit false
returns despite covering each input value independently. `selection.accept`
preserves an existing first value or supplies an incoming value; its Option field
is always Some on return. Both functions' signatures and behavior must remain.
The actor is asked to prepare as much reusable library vocabulary as those
contracts permit, improve useful tests, and explain any remaining project case.
No missing case or mutation is disclosed in the task prompt.

One fresh Luna/max actor will receive only the compact Flow/2 primer, task,
and restricted JSONL broker command. Version-matched help is advertised. The
actor may edit only its isolated dictionary through the broker. No raw project
or verifier access, repository edits, or research-history context is permitted.
The preparation and scoring agents are not actors.

Basic acceptance requires preserved independent behavior/signatures, passing
attached tests, flags.agree persisted as library, selection.accept remaining
project, and a truthful explanation of the unreachable None return. An explicit
failed commit is not required if the actor can explain the boundary from source
and introspection. No expected success or failure is inferred from tool return
success alone.

Before dispatch, freeze the runtime file inventory, seed inventory, prompt,
verifier, two word-only mutants, broker source, and this plan. Independently check
all four Boolean pairs and varied existing/missing Selection states. Score on
disposable copies and verify the actor inventory remains unchanged.

Separate mutation measurement: one equality mutant is wrong only for (true,false)
and another only for (false,true). Retain the actor's tests and measure test
failures, finite/structural coverage, and replacement acceptance separately. A
minimal one-off-diagonal control must demonstrate that the opposite mutant can
pass qualification but fail the independent truth oracle. Mutation resistance
is reported separately from basic task acceptance, not folded into a post-hoc
success definition.

Record all broker exchanges, diagnostics, help exposure, final source/tests,
durable state, timestamps, and request/response bytes. No model-token count is
available unless a provider reports it; do not equate bytes with tokens.
This single guided task cannot establish superiority over F#, general reliability,
retention benefits, or a causal effect of finite coverage.

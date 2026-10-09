# Flow list discovery: correct the advertised call forms

Status: implementation and local validation complete; one fresh-agent follow-up
is running. No follow-up efficacy outcome is claimed yet.

Report147's AgentLang participants could not discover the supported fold form.
The syntax descriptor advertised Stack's `list.fold <word>` while Flow/2 expects
`items.fold(seed, callback)`. A coordinator control could implement the task,
but the agent-facing interface failed to make that capability usable.

## Change

List map, filter, each and fold descriptors now include a separate machine-readable
`flow2Syntax` property. The existing Stack `syntax` strings remain unchanged.
Search indexes the receiver forms, and descriptions state that callbacks are
statically named functions and cannot capture caller locals.

Flow/2 define and examples help include a generic integer-sum fold with a named
callback, empty and populated tests, an attached example, and ready-to-run
define/test/example/eval requests. The example contains no rental-specific code.
Tests execute the help-returned requests through the protocol and check the
advertised map/filter/each receiver forms. No grammar, semantic IR, execution
rule, effect rule or library qualification requirement changes.

## Local validation

- Clean serial Release solution build: zero warnings and errors.
- Flow Runtime executable: 32 groups, 1,140 assertions pass.
- CLI executable: 11 groups, 145 assertions pass.
- Fresh isolated CLI build: zero warnings and errors.
- Unchanged report147 oracle against its correct control using the fresh CLI:
  18/18 cases pass; input unchanged.
- Read-only review found no material issue; diff checks pass.

Commands and full output are preserved. Initial development attempts caught a
missing F# record delimiter and an overly specific prose assertion; those logs
remain separate from passing validation. No CI ran. Tests of the returned syntax
are acceptance evidence for this change, not a new proof of iteration semantics.

## Follow-up design and limits

One fresh Luna/max participant receives report147's same public batch-billing
contract and retained starting dictionary, with 100 broker exchanges. The oracle
is copied unchanged and the prompt gives no fold syntax or implementation hint.
The runtime and inputs are frozen before dispatch. Correctness, library status,
inherited tests, production calls to retained vocabulary, errors and normal broker
closure will be assessed separately.

The prompt explicitly prohibits listing, inspecting or messaging other agents,
delegation, and out-of-broker project work. Only one participant runs. The available
agent API does not provide a per-participant tool whitelist: these are prompt
constraints, not tool-enforced isolation. Completion must disclose any external
tool use or information received. Any observed violation remains in the record.
The general host guidance now makes this limitation and incident handling explicit.

This is a separately identified discovery follow-up, not a replacement for the
contaminated report147 observations or a new four-cell comparison. Even a correct
submission would establish only bounded feasibility after correcting guidance;
one retained participant cannot measure a general retention benefit.

## Evidence

The [preflight archive](evidence/148-flow-list-discovery/preflight.zip) and
[index](evidence/148-flow-list-discovery/preflight-index.json) contain 172 verified
entries, including changed sources, executable tests, runtime binaries, help
review, frozen prompt/start, oracle and control results. Size: 2,769,819 bytes.
SHA-256: `231fe3f982558d48b6a9f09b618e881a78e256ddae469f1a7a5be9e843453ef7`.

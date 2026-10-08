# Preparation Record

Status: ready for coordinator review; no actor has been launched.

The approved plan, actor prompt, and oracle remain unchanged. `freeze.json` records their hashes, the report121 implementation commit, the copied Debug runtime hashes, and the control evidence. The runtime was copied without rebuilding from the frozen Debug output for commit `ee97dda94c9328a4046029747076fa0a4248b67f`; its pin lists all 22 runtime files. The actor project at `project/actor` is empty.

Final preflight attempt `preflight/attempt-03/` passed its assertions. The correct control passed four attached tests and all ten direct oracle rows. Its `batch.valid?` predicate had complete Bool coverage with both `true` and `false`. The always-true fault control passed the two valid-behavior checks, failed only the two expected constructor-error tests with `TEST_EXPECTED_RUNTIME_ERROR`, accepted all six invalid oracle constructors, and left predicate Bool coverage incomplete with `false` missing. These are coordinator controls, not actor results.

Attempt 01 is preserved under `preflight/attempt-01/`; it reached the same passing runtime checks but stopped on a harness assertion that looked for `complete` on the Bool return row instead of the enclosing finite-coverage record. Attempt 02 passed both controls; attempt 03 additionally asserts the faulty predicate's coverage is incomplete. No product/runtime failure occurred in these preflight runs.

The exact proposed actor entry is recorded in `freeze.json`: fresh `gpt-6-luna` at maximum reasoning, no inherited conversation, public prompt `actor-prompt.md` verbatim, empty project `project/actor`, pinned CLI `runtime/debug-artifacts/AgentLang.Cli.dll`, trace `runtime/actor.trace.jsonl`, and at most 100 broker exchanges. The actor has not been dispatched; wait for coordinator approval. This is one bounded BatchBounds observation, not a causal before/after comparison.

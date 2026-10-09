# Testing reusable vocabulary

Library quality and definition lifetime are separate decisions. A temporary word is fully type checked and effect checked, but remains available only within its task/session. A project word persists after passing its attached tests. A library word persists with a stricter reuse gate. A local binding holds a value inside a word invocation and is not vocabulary.

| Definition | Type/effect checking | Passing attached tests before persistence | Library coverage before persistence |
| --- | --- | --- | --- |
| Temporary | Required | Required if promoted/committed | Required if promoted as library |
| Project | Required | Required, at least one test | Reported; complete coverage not required |
| Library | Required | Required, at least one actual target invocation | All own executable instructions/control-flow outcomes, per-parameter Bool/enum values, and supported finite returns |

Tests run with fresh deterministic effect providers. Tests for a word measure that word's own body; a caller's tests cannot substitute for the reusable callee's attached tests. Stale test results are invalidated when definitions change or reload. A gate runs the current tests again before accepting the revision.

For a runnable I/O example, see [testing library functions without real I/O](LIBRARY-IO-TESTING.md).
It demonstrates passing promotion, a coverage rejection, and an effect-count
rejection using the isolated virtual filesystem.

Persistence gates run against the exact durable project projection. A scoped commit includes candidate dependencies used only by its selected tests or examples, including explicit nominal container types; newly included helper words need their own passing tests. Temporary metadata dependencies must be promoted or removed before commit. Selected tests and examples cannot be silently discarded, and unrelated staged metadata remains staged. Replacement gates also check affected transitive persistent callers.

The initial conditional coverage gate requires both `if` outcomes, including the empty outcome of a conditional without `else`. Coverage reports identify uncovered source locations so an agent can add a specific missing case. Typed container support extends the gate to both Option/Result cases, empty/nonempty list iteration, and retained/rejected elements for filtering. The container milestone report records which of these gates have been validated.

Finite qualification uses the exact function ID and revision in its own passing tests. Each direct Bool/enum parameter is independent; finite composite returns may require their internal value combinations. Open composites retain applicable finite fields and tags. Expected-error tests supply no return evidence, and expectation expressions or failed tests cannot qualify a function. Unreachable declared alternatives are not exempt: narrow the contract or retain project maturity. See [finite coverage](FINITE-COVERAGE.md) for the 4096-value bound and unsupported-domain rules.

Coverage establishes that a path was exercised; assertions establish what it did. Neither establishes correctness for every possible input. Reusable words should include representative valid values, boundaries, invalid constructions or structured errors, and applicable domain invariants. For example, a speed conversion needs zero and representative positive/negative values permitted by its type; an Email constructor needs values accepted and rejected by its declared validator; an eligibility predicate needs both outcomes and values on either side of its threshold. Downstream words can rely on tested library contracts and focus on their integration behavior.

Host tests exercise parser/compiler/runtime correctness and storage boundaries. Language tests exercise agent-created words. Independent benchmark oracles evaluate the task outcome and are kept separate from the tests an agent supplies. An agent can write a passing but weak test, so its tests alone must never determine benchmark success.

Reports distinguish these three layers and record failures as well as passes. New branching constructs must define their observable coverage categories and acceptance cases before library promotion is enabled for them.

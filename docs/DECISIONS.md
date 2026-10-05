# Initial design decisions

These decisions refine the supplied PRD into a first usable experiment. They describe intended contracts; README and acceptance results identify what the current build delivers.

| Question | Initial decision | Reason |
| --- | --- | --- |
| How much to implement before an agent trial? | One inspect/define/run/test/commit/reload slice | Tests the workflow before investing in a broad language |
| Does the language contain AI agents? | No; coding agents are external clients/builders | Keeps the runtime deterministic and model-independent; the evaluation harness is separate |
| Many projects or a compact library? | Separate F# modules in a library, CLI, acceptance runner | Keeps boundaries inspectable without assembly overhead |
| Syntax? | Line-oriented declarations, explicit signatures/effects, quoted strings | Simple parser, readable persisted files |
| Stack ordering? | Signatures list bottom-to-top; final top is last | Removes a common source of concatenative ambiguity |
| Larger bodies? | Typed locals and checked conditionals | Reduces stack manipulation without abandoning composition |
| Primitive polymorphism? | Resolve a small set of built-in operations statically | Supports dup/equality without adding user-defined generics |
| Record identity? | Nominal type and generated typed constructor/accessors | Prevents accidental structural interchange |
| Semantic scalar types? | Nominal wrappers, explicit conversion, optional pure construction validator | Email and numeric units carry checked meaning beyond their representation |
| Containers and callbacks? | Closed List/Option/Result types, exhaustive cases, statically named callbacks | Preserves exact types and inspectable effects without user-defined generics |
| Financial arithmetic? | Float only for the demo; require a Money design later | Avoids claiming billing accuracy from a toy numeric model |
| Candidate semantics? | Inspectable and executable in-session; test-gated persistence | Supports experimentation without persisting broken vocabulary |
| Dependency commits? | Reject persistence of unresolved temporary dependencies | Ensures a fresh session has the same executable graph |
| Definition replacement? | Revalidate callers or reject incompatible changes | Preserves compiled-call assumptions |
| Effects? | Transitive declarations plus separate host authorization | Declarations describe behavior; policy grants authority |
| Tests? | Fresh virtual providers per test | Prevents contamination and host side effects |
| Library rigor? | Passing attached tests plus full instruction and supported control-flow outcome coverage | Covers conditionals, case selection, empty/nonempty iteration, and filter decisions; downstream project words have a lighter gate |
| Rollback? | Dictionary transaction; external effects remain recorded | Makes the practical rollback boundary explicit |
| Storage? | Canonical plain source plus lightweight metadata/history | Simple recovery and review, no database dependency |
| Agent protocol? | One command dispatcher with JSON-lines transport | Small tool surface and consistent human/machine behavior |
| Metrics? | Runtime events plus model-harness usage | Prevents invented token or recovery measurements |
| Benchmark scope? | Small pilot before a 60-task suite | Finds usability problems quickly |
| Executable representation? | Authoritative typed semantic IR shared by backends | Prevents interpreter and future native semantics from drifting; current checked AST is transitional |
| Native backend timing? | Conditional post-experiment LLVM JIT for development and AOT for release | Preserves live development while permitting a minimal native release runtime; LLVM stays outside V1 |

The refined PRD resolves several tensions in the original: 30 versus 50–100 primitives is a staged rollout; built-in parameterized containers do not imply generic user words; test-gated word commits are distinct from benchmark success; default effect denial is distinct from mocks; deterministic semantic logs do not require identical timestamps; and task rollback does not promise reversal of external I/O.

Open designs for later milestones include language-level exact Money arithmetic, structured error expectations, accumulation callbacks, constrained real filesystem access, revision migration, named benchmark snapshots, and exact token-budget enforcement. The conventional business fixture already uses checked integer minor units; language parity remains pending. The optional external harness provides provider integration and request-byte limits, with live comparisons still unmeasured. In-memory task snapshots and abort restoration are already required in the first release.

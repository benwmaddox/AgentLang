# Initial design decisions

These decisions refine the supplied PRD into a first usable experiment. They describe intended contracts; README and acceptance results identify what the current build delivers.

| Question | Initial decision | Reason |
| --- | --- | --- |
| How much to implement before an agent trial? | One inspect/define/run/test/commit/reload slice | Tests the workflow before investing in a broad language |
| Many projects or a compact library? | Separate F# modules in a library, CLI, acceptance runner | Keeps boundaries inspectable without assembly overhead |
| Syntax? | Line-oriented declarations, explicit signatures/effects, quoted strings | Simple parser, readable persisted files |
| Stack ordering? | Signatures list bottom-to-top; final top is last | Removes a common source of concatenative ambiguity |
| Larger bodies? | Typed locals and checked conditionals | Reduces stack manipulation without abandoning composition |
| Primitive polymorphism? | Resolve a small set of built-in operations statically | Supports dup/equality without adding user-defined generics |
| Record identity? | Nominal type and generated typed constructor/accessors | Prevents accidental structural interchange |
| Semantic scalar types? | Nominal wrappers, explicit conversion, optional pure construction validator | Email and numeric units carry checked meaning beyond their representation |
| Financial arithmetic? | Float only for the demo; require a Money design later | Avoids claiming billing accuracy from a toy numeric model |
| Candidate semantics? | Inspectable and executable in-session; test-gated persistence | Supports experimentation without persisting broken vocabulary |
| Dependency commits? | Reject persistence of unresolved temporary dependencies | Ensures a fresh session has the same executable graph |
| Definition replacement? | Revalidate callers or reject incompatible changes | Preserves compiled-call assumptions |
| Effects? | Transitive declarations plus separate host authorization | Declarations describe behavior; policy grants authority |
| Tests? | Fresh virtual providers per test | Prevents contamination and host side effects |
| Library rigor? | Passing tests plus full instruction and conditional-branch coverage | Reusable vocabulary has a stricter gate than downstream project words |
| Rollback? | Dictionary transaction; external effects remain recorded | Makes the practical rollback boundary explicit |
| Storage? | Canonical plain source plus lightweight metadata/history | Simple recovery and review, no database dependency |
| Agent protocol? | One command dispatcher with JSON-lines transport | Small tool surface and consistent human/machine behavior |
| Metrics? | Runtime events plus model-harness usage | Prevents invented token or recovery measurements |
| Benchmark scope? | Small pilot before a 60-task suite | Finds usability problems quickly |

The refined PRD resolves several tensions in the original: 30 versus 50–100 primitives is a staged rollout; built-in parameterized containers do not imply generic user words; test-gated word commits are distinct from benchmark success; default effect denial is distinct from mocks; deterministic semantic logs do not require identical timestamps; and task rollback does not promise reversal of external I/O.

Open designs for later releases include Money arithmetic, result/error ergonomics, higher-order list words, constrained real filesystem access, revision migration, named benchmark snapshots, provider integration, and exact context-budget enforcement. These should be driven by pilot evidence. In-memory task snapshots and abort restoration are already required in the first release.

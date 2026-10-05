# Review: data-flow-oriented syntax without mandatory RPN

Date: 2026-10-05
Status: review and early migration replan; the target direction is selected, while the new frontend is not implemented and its concrete grammar/format contracts still require validation.

Update: after this initial review, the user requested switching very soon, preferred dot notation with pipeline behavior, and confirmed close-to-first-use as lint. [The early migration plan](../docs/FRONTEND-MIGRATION.md) supersedes deferring the entire switch until late research. Words and semantic IR remain; named inputs/locals and static dot calls become the target authoring model. The proposed no-mutable-language-globals boundary preserves explicit state flow independently of notation. The implementation is not yet migrated.

The replan covers parser/type/lowering, complete container/test semantics, versioned authored persistence, then CLI/protocol/default cutover and reset external-agent fixtures. Overlapping RPN authoring expansion was held while this contract was prepared; independent interpreter value-safety work continued. The structured expected-value checkpoint now passes its focused Source suite with 74 assertions and language acceptance with 33 groups / 568 assertions; report 013 records that checkpoint. The bounded interpreter regression suite passed 19 assertions after the coordinator corrected two test-only helper calls. These focused results precede the complete code-milestone gate and do not validate the proposed frontend. Historical RPN trial artifacts remain unchanged.

Independent read-only frontend review confirmed that current Expr/typed IR can represent the lowering, and highlighted exact call resolution, lexical immutability, written argument effect order, multi-output binding restrictions, synthetic coverage sites, and separate language/storage versioning. Those requirements are recorded in the migration plan. Namespace qualification is explicit `::`; a receiver dot expression never receives an automatic namespace fallback. [Report 018](018-related-language-comparison.md) records related-language precedents and reinforces keeping the conventional F# comparison fair.

## Input and recommendation

The user supplied a discussion proposing pipelines, nearby named bindings, immutable rebinding, multiple-result destructuring, function composition, named transformation signatures, and blocks with an implicit current value. Their preference is to strongly encourage data flow without enforcing Forth's anonymous operand-stack source model throughout the language.

The strongest candidate is a small expression-oriented frontend with explicit pipelines, named typed parameters, immutable local values, and ordinary calls/conditional expressions. Encourage nearby producer/consumer relationships through examples, formatting, and deterministic lint feedback. Do not prohibit branching, reuse of an earlier value, or ordinary calls simply because they do not fit a linear pipeline. A task such as calculating a balance and independently checking eligibility naturally has more than one data dependency.

This preserves the project's more important commitments: inspectable named words, strong nominal/refined types, explicit effects, tested library admission, definition-level replacement, and one authoritative verified semantic IR. It makes source data relationships explicit without requiring agents to track anonymous stack positions. This is an engineering hypothesis; the existing RPN pilot does not establish that a proposed frontend improves agent outcomes.

## Assessment of the discussion's options

| Proposal | Recommendation and qualification |
| --- | --- |
| Explicit pipelines | Preferred research candidate. Choose exactly one rule for the piped argument; do not infer placement from overloads or parameter types. Auxiliary arguments can be named. |
| Nearby short-lived bindings | Prefer immutable lexical bindings. Encourage locality, but do not impose a maximum binding lifetime or force recomputation just to avoid referring to an earlier value. |
| Rebinding | Optional later convenience. If offered as shadowing/new value versions, distinguish it clearly from mutation of referenced state. Initially distinct local names are simpler to inspect and debug. |
| Multiple results/destructuring | Useful when operations genuinely return multiple values. Define exact output order/types and destructuring behavior. A named record is often more informative than a wide anonymous tuple. |
| First-class composition operators | Defer initially. Named words already preserve reusable compositions. Arbitrary function values, closures, partial application, and their effect/type rules are additional scope, not necessary for pipeline notation. |
| Named transformation signatures | Strong recommendation: inputs, outputs, and effects remain prominent, inspectable contracts. Names supplement type checking; they do not weaken it. |
| Implicit current-value blocks | Lower priority. Implicit input/replacement rules, nested blocks, branching, and error propagation can recreate hidden state. Explicit `|>` gives a clearer starting experiment. |

Pipeline syntax already has a conventional precedent in [F#'s documented function pipelines](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/functions/). This is evidence that the notation can express function application, not evidence of an agent-performance gain or a reason to copy F#'s full function semantics.

## Candidate semantic rules to settle before implementing

1. A pipeline stage evaluates its incoming expression once. Specify when auxiliary argument expressions evaluate and preserve that order in the semantic IR and every backend.
2. A simple first-input convention is a reasonable candidate: `x |> transform(option: y)` means `transform(x, option: y)`. Named auxiliary arguments resolve against declared parameter names. The research prototype must select one convention and document it; this report does not change existing call semantics.
3. Pipelines may change the carried type. Every stage remains strongly checked; a Customer may become a Balance or a Result, rather than requiring every stage to return the original type.
4. A Result or Option does not unwrap automatically. Use explicit matching or a separately specified bind/map operation. An ordinary pipeline passes the complete value to the next operation.
5. Pipelines do not imply purity. Observable effects must remain declared, authorized, ordered, and visible in introspection. A stage named upload or save is not pure merely because it returns a transformed value.
6. Conditional expressions and ordinary calls remain available for branching and merging data flow. Define identical branch output types rather than privileging the linear case.
7. Nominal/refined constructors still validate. Named parameters and expression syntax must not introduce implicit base-value coercions or unchecked construction of Email or numeric units.
8. Preserve source locations and coverage obligations through lowering. Source inspection and debugging must show the frontend the author actually used, while IR inspection remains canonical and frontend-independent.

## IR and memory implications

The current typed semantic IR uses stack signatures, explicit locals, resolved calls, and structured control flow. An expression/pipeline frontend can lower into those operations; an immediate rewrite of semantic IR is not necessary simply to change source notation. Native lowering can subsequently convert the semantic operations into LLVM's representation. [LLVM documents its IR as SSA-based](https://llvm.org/docs/LangRef.html); the project semantic IR still needs to retain nominal validation, effects, structured errors, limits, and source obligations that a low-level backend alone does not supply.

Immutable source bindings do not automatically imply zero allocation or no aliasing of referenced data. Rebinding does not by itself establish in-place mutation or ownership. Similarly, pipeline source does not prove smaller memory usage. Allocation/lifetime policy and value layout remain separate later backend research, with equivalent-workload measurements. F# can remain the compiler/host implementation while LLVM supplies JIT/AOT execution backends.

## Suggested experiment boundary

After the initial vocabulary experiments, implement only the smallest candidate frontend needed to compare current RPN with named locals, ordinary calls, and explicit pipelines. Use the same semantic IR, domain words, effect providers, library gates, hidden acceptance tests, and model. Include linear transformations, eligibility branching, joining two independently derived values, multi-input words, and Result failures; a comparison containing only pipeline-friendly tasks would be biased.

Record correct task completion, stack/order/type mistakes, error recovery, retrieval/context cost, source size, generated tokens where observable, and tool interactions. Keep syntax experiments separate from vocabulary retention and native memory experiments. Prefer whichever syntax reduces the cost of correct changes while retaining inspectability; do not select the winner on terseness alone.

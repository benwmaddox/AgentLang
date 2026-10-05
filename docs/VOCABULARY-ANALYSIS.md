# Vocabulary Analysis Foundation

`VocabularyAnalysis` is a pure, immutable analysis layer over effective word, record, and scalar maps. It does not execute a word, invoke an effect provider, change a dictionary, or infer semantic equivalence.

## Stable inputs

`VocabularyAnalysis.build words records scalars wordIds` requires every authored word (`Builtin = None`) to have a unique, nonempty stable ID. The mapping must not name words absent from the input map. Calls to authored words use those IDs in fingerprints, so a rename that preserves the ID preserves call identity. Primitive targets use their `BuiltinOp` identity; generated targets use their record/scalar/field identity.

The builder validates map keys, generated operation references, nominal type references, scalar validator names, stable IDs, and all direct call/callback references. Compiler type and effect checking remains authoritative; this module does not repeat stack or effect validation. Missing references fail with the existing structured `LanguageException` diagnostics.

## Duplicate candidates

`fingerprint index wordName` returns lowercase SHA-256 for an authored word. `duplicateCandidates index` emits sorted pairs of authored words whose exact structural fingerprints match. Primitive and generated words are not candidate owners.

The framed fingerprint includes the ordered input/output types, declared effects, and source AST structure. It preserves literal values and types, branch/case structure, callback target identity, and local binding names. It ignores the word's own name, documentation, source text/spans, revision, and maturity. Local and match-payload names are intentionally kept exact; this is strict structural matching, not alpha-equivalence or a semantic proof. A candidate is evidence of an exact normalized structure under the current stable target identities, not an automatic refactoring recommendation.

## Static primitive distance

`staticCallEstimate index root` recursively expands the root's static word call sites without running them. Its `PrimitiveCallSites` counts `BuiltinOp` occurrences. `GeneratedCallSites` counts generated constructors and accessors. A scalar constructor's validator is classified by the validator target: a built-in validator contributes its primitive site, an authored validator contributes one authored invocation plus its expanded body, and a generated validator contributes its generated site. There is no extra generated dispatch count. `AuthoredInvocationSites` counts nested authored calls and named callback invocations, excluding the selected root's entry invocation.

Every syntactic call occurrence contributes separately. A memoized DAG node's totals are added again for every call path, so diamonds and repeated calls retain multiplicity without repeating analysis work. Both arms of `if`, both option cases, and both result cases contribute their potential static call sites; the sum is not an executed-path estimate. A statically named callback target is expanded once per syntactic callback site. `DynamicCallbackTargets` is the sorted list of distinct target names, while `DynamicCallbackOccurrences` stores each target's arbitrary-precision static occurrence count. This compressed representation keeps result size proportional to the number of distinct callback words, even when a DAG expands to an enormous count. `DynamicCallbackRepetitionsUnknown` is set because runtime collection size is unknown.

All counts use arbitrary-precision integers. Recursive expansion cycles and unknown roots/references produce structured diagnostics rather than partial counts. Counts describe potential static expansion through the current dictionary; they do not predict latency, work performed at runtime, or a task's actual primitive usage. Runtime's current `Used` set has no occurrence counts, so this foundation does not compute a reuse ratio.

Run focused checks with `dotnet run --project tests/AgentLang.Vocabulary.Tests -c Release` after the Core project and solution include the module and test project.

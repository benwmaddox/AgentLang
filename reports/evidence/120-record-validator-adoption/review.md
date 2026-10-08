# Completed adoption-trial review

Read-only review of the closed actor trace, the actor's committed `project/actor` source/manifest, and the current Discovery and VocabularyAnalysis implementation. The pending counterfactual project was not inspected.

## Caller introspection

The generated record-constructor edge is present in the graph; this is not a missing dependency edge. `Discovery.directDependencies` in `src/AgentLang.Core/Discovery.fs` maps `RecordConstructor` to the record's declared validator (lines 77–90), and the reverse caller map is built from all words (lines 189–198). The `search-dependency` and `transitive-callers` Runtime operations use this Discovery index. `VocabularyAnalysis.dependenciesFor` and its constructor expansion also follow the record validator (`src/AgentLang.Core/VocabularyAnalysis.fs`, lines 49–62 and 398–413), so closure and call analysis retain it.

The empty `describe range.is-valid` `callers` field is expected under that field's narrower implementation: `Runtime.availableDescription` searches only `userWords state` and authored `Compiler.dependencies` (Runtime.fs lines 2654–2658); the legacy `callers` operation uses the same filter (lines 4383–4386). Generated constructors are not user words. Use `search-dependency` or `transitive-callers` to see the implicit constructor edge. The Discovery documentation currently mentions generated scalar constructors but omits the new record-constructor edge; that is a documentation gap, not a missing graph edge.

## Trial outcome and maturity

The committed source has the requested two-field `BoundedRange`, a pure `range.is-valid` predicate enforcing `start <= finish`, and `range.width`. The coordinator's independent final score reports all 9 oracle rows and all 5 actor-owned tests passing. The persisted type metadata binds the validator to the exact `range.is-valid` `WordId`. Thus the behavior and stable identity objectives passed.

The maturity objective was not maximized. `range.width` is library maturity, while `range.is-valid` remains project maturity. Its two attached tests observe only `true`; the expected-error reversed-construction test is attached to `range.width`, so the call fails while evaluating the constructor argument before `range.width` is entered and does not supply a false return observation for `range.is-valid`. The actor did not attempt a library commit for the predicate, so there is no structured library-gate refusal to report.

This limitation is avoidable with current semantics: attach an expected-error test to `range.is-valid` whose body constructs a reversed `BoundedRange`. The generated constructor invokes that target, the predicate completes with `false`, and the constructor then raises `RECORD_VALIDATION_FAILED`; expected-error coverage preserves the completed target return. With the record's open-only `Int` fields and a `Bool` result, that should complete the predicate's finite return coverage. Help at trace exchange 11 supplied the relevant expected-error/return-observation guidance. The actor nevertheless chose not to add this case or attempt promotion, so classify the trial as behavioral success with partial maturity adoption, rather than full attainment of the “highest maturity” objective.

This classification concerns evidence and authoring choices in the actor trial; it is not a defect in record validation or in generated-constructor dependency tracking.

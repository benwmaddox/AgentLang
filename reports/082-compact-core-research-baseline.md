# Compact trusted core: source baseline

Status: read-only source review, 2026-10-07. No actor, CLI, build or memory
measurement was run for this review. The current interpreter is F#/.NET.

## Foundation and vocabulary

Compiler.primitives registers 58 trusted word names (src/AgentLang.Core/Compiler.fs:114).
The explicit interpreter allowlist has the same 58 names (IrInterpreter.fs:66).
The source categorization is 30 numeric/comparison/equality/Boolean, 15 string
and GUID/email/instant, five list operations, three minimal stack operations,
and five host-facing words: file.read, file.write, file.exists?, clock.now and
console.write. All except those five are pure.

Generated record constructors/accessors and nominal wrapper .new/.value words
are additional dictionary entries, not additional trusted primitives
(Runtime.fs:352). Authored project words add another layer. Higher-order map,
filter, each and fold are typed frontend/IR constructs rather than primitive
host words; compact inventory reports syntax separately (Runtime.fs:3650).
A total dictionary count must distinguish these categories.

## Trusted execution and effects

Source expressions and semantic IR use closed discriminated unions
(Core.fs:53; TypedIR.fs:69,136). Verified programs bind the trusted catalog and
backends must match it (TypedIR.fs:241). Source review found no language-level
reflection or arbitrary CLR invocation. Host F# implementation uses .NET.

Ten effect names exist in IR, but implemented guest effect words currently
use fs.read, fs.write, clock.read and console.write. IrEffectCommand is a closed
virtual file/fixed clock/virtual console command union (IrInterpreter.fs:12).
Runtime checks granted effects and supplies project-local in-memory providers
(Runtime.fs:1382). Guest file.* does not provide real host filesystem access.
Host project persistence uses filesystem storage through a separate control
plane. CLI effect grants are names, not path-pattern policies. Database, network,
process and random primitives/providers remain absent; real providers would
need scoped resource policies and independent validation.

## Extension pressure to measure

A trusted primitive addition touches signatures/effects/docs, the IR catalog,
interpreter allowlist/dispatch and conformance tests. A new semantic construct
also touches parsing/lowering, verification, coverage and persistence. This is
a visible trust-boundary cost. Domain behavior should normally be authored
vocabulary, with host expansion justified by demonstrated missing mechanisms.

For future trials record the frozen primitive count/catalog hash, primitive or
IR additions, missing-capability requests and whether each was resolved through
composition, a domain word, a core mechanism or better authoring/discovery.
Combine those observations with independent edit/regression acceptance and
later reuse. Do not change the already frozen retention-003 outcomes.

Task logs expose inspected/used/created words, tests, effects and errors
(Runtime.fs:73,2050). VocabularyAnalysis.staticCallEstimate describes structural
primitive/generated/authored call expansion (VocabularyAnalysis.fs:346); repeated
callback execution is unknown. No runtime command exposing this estimate was
found in the review. It is not an execution or memory cost measurement.

## Limits and next evidence

Fuel/value limits bound execution (IrInterpreter.fs:55), but do not prove low
resident memory. Runtime values are managed F# discriminated unions and lists
(IrInterpreter.fs:38). The source count of 58 establishes neither a tiny
implementation nor a lean release runtime. Measure binary footprint, allocation,
peak/reserved memory, startup and mailbox state/queue capacity separately on
equivalent workloads after the semantic/lifetime design exists.

This baseline supports the compact extensible-core hypothesis without asserting
it succeeds. The reliability follow-up remains an unfrozen draft in
docs/RELIABLE-EDIT-TRIAL-DRAFT.md. Fresh agent behavior and missing-capability
observations, rather than this source review, will test whether the core can
support useful growing vocabulary without repeated host expansion.

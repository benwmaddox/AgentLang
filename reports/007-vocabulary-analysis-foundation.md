# Milestone 007: vocabulary analysis foundation

Status: pure analysis layer validated; runtime integration and research metrics remain pending.

`VocabularyAnalysis` builds an immutable index over word/type maps and stable authored word identities. Exact structural SHA-256 fingerprints identify duplicate candidates while preserving literal/type/effect/branch/callback/local structure. Documentation, source spans and the owner's display name do not affect fingerprints; calls to authored words use stable IDs. This is structural matching, not semantic equivalence or an automatic refactoring decision.

Static call expansion counts primitive, generated and nested authored call sites, retaining multiplicity across diamonds and repeated calls. Both branch arms contribute potential sites; callbacks expand once per static occurrence and flag unknown runtime repetition. Arbitrary-precision counts and compressed callback-occurrence maps avoid overflow and exponential output size. Recursive or missing references fail with structured diagnostics.

Review corrected two problems before publication: scalar validators were initially charged an extra generated dispatch, and expanded callback target lists could consume exponential memory on compact DAGs. Validator targets are now classified once by their actual operation kind, and callback multiplicity is represented by a bounded map of arbitrary-precision counts.

The integrated Release gate passed 32 focused assertions covering identity-preserving renames, exact duplicates and negative cases, all branch forms, callback multiplicity, generated validators, missing references/cycles, and counts larger than Int64. Evidence is retained with [milestone 006 validation](evidence/006-working-tree-validation.json). [The analysis contract](../docs/VOCABULARY-ANALYSIS.md) explains count definitions and limitations.

This layer does not yet emit commit warnings, track actual invocation counts, compute Vocabulary Reuse Ratio, or prove conceptual compression. Runtime's current used-word set lacks occurrence counts. Those PRD requirements remain open. The evaluation plan now explicitly retains the original primitives-only Flat baseline; a domain-seeded retention control must be labeled separately rather than replacing it.

This foundation shipped with code and reports in `743e07d79ab1bb29ea859bc61ab23aec540b4092`, merged and pushed to private `main`. [The exact-revision CI run](https://github.com/benwmaddox/AgentLang/actions/runs/37261518116) passed; [clean committed validation evidence](evidence/006-committed-ci-validation.json) includes the 32 integrated vocabulary assertions.

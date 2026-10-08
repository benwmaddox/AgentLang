# Complete-record validator milestone plan

Base db481e26d1bf5016aba6ff708d099b52a6d3e6ae. Previous goal turn: progress, report118 committed and pushed.

Goal: optional pure whole-record predicates, enforced at verified language construction, persistent binding and interpreter/native execution. Records without predicates retain behavior. Follow scalar-validator syntax/diagnostics/binding rules; exact Record -> Bool, false RECORD_VALIDATION_FAILED. Raw C allocation remains trusted implementation, not a public predicate-enforcing API. No arbitrary host escape is added.

Core API: RecordDefinition.Validator option; IrRecordDefinitionData.ValidatorCall option; MakeRecord(call,key,validator). Verify exact agreement of operation/type-table binding, purity, cycles and fingerprints/schema. Predicate may read its own fields but cannot recursively construct its own type through any dependency path.

Ownership: construction_boundary_plan Core/frontend/IR/interpreter and most focused tests; populated_delivery_plan Runtime/Discovery/VocabularyAnalysis/FlowLowering, Flow.Runtime/Storage/Business.Transitions tests and separate business-validated-lookup.agent; finite_trial_prepare LLVM/native and LLVM tests. Root reviews, reports, aggregate validation and publication. Shared checkout; no overlapping edits, no worktrees. Preserve retention-004 drafts and frozen runtime artifacts.

Acceptance: empty/matching lookup valid, mismatched customer/target rejected; nonvalidated records unchanged; unknown/wrong-signature/impure/cyclic validators rejected; tampered IR cannot omit/change validator; dependency closure is frozen after persistence; exact source/target reload; supported native Core/O0/O2 parity and retained-root entry boundary. Negative own tests invoke constructor and expect rejection while measuring actual validator false return, so validator library qualification remains possible without unchecked values.

Validation: fresh Debug Core and affected focused tests, independent review, scripts/Validate.ps1 -Configuration Debug saved evidence plus separate LLVM runner. Never rebuild frozen Release/trial binaries. Save report119 with exact delivered/pending scope and no fresh-agent claim, commit and fast-forward main/prototype then push after checks. Follow with bounded fresh-agent adoption test rather than expanding implementation indefinitely.

## Review decisions during implementation

Expected-error coverage needs an explicit contract correction. Runtime currently erases all TargetReturns on passing expected-error tests, including completed calls before a later error. Preserve actual completed target return observations; a throwing target still produces no return observation, and failed assertions/tests remain excluded. Add controls and update FINITE-COVERAGE.md/report119. This enables a validator's own constructor-rejection test to record its actual false return, without exposing unchecked records.

Refined record finite domains: an arbitrary validator can exclude field combinations. Reject unproven finite projections as unsupported, like refined finite scalars; do not enumerate invalid combinations or claim full valid-domain coverage. Fully open shapes with no finite obligations may remain observable without enumeration. A validated lookup-returning function may remain project maturity; its predicate can qualify through actual Bool outcomes. No automatic enumeration/proof engine is in scope.

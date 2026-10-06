# Flow project authoring and default frontend plan

Status: read-only planning, 2026-10-06. No default cutover or type-source schema
change is implemented by this report. The full PRD remains the objective.

## Authoritative gaps

Flow currently parses one word, test, example or expression. Runtime's Flow
registration takes one word source with separate test/example arrays. Records,
nominal scalar types and multiword project documents are authored only through
the legacy parser. Runtime's default frontend is Stack; the human CLI buffers
`end` declarations and dispatches bare inputs without a frontend selector.
Protocol forwards requests rather than selecting a frontend itself.

Existing durable Flow word/case sources, stable call bindings, verified semantic
IR, rename/deprecate and lifecycle gates must be preserved. Flipping a default
before project authoring works would leave strong types dependent on old syntax.

## Next implementation sequence

1. Specify brace-based Flow record/refined-type and project-document syntax.
   Named fields retain authored constructor order. Validator references must use
   explicit root or namespace-qualified keys, without suffix lookup. Candidate
   forms are `record Customer { field email: Email; }` and
   `type Email : String { validate email::valid; }`; these are proposals, not
   accepted grammar. Check consistency with the existing migration contract
   before implementation. An unvalidated nominal type still has distinct identity.
2. Parse all document members with precise spans and retained authored bytes.
   Add an atomic staging path: build the complete proposed type/word dictionary,
   generated type vocabulary and attached cases, resolve validators, compile and
   validate complete bindings, then activate only if every member succeeds.
   Calling the two existing registration paths in sequence is not atomic.
3. Preserve typed declaration provenance through storage/reload/source/export.
   Current type metadata has no frontend discriminator and uses legacy rendering
   and parsing. Audit the representation before deciding whether a new manifest
   version is necessary; do not silently reinterpret v1/v2 objects. Any schema
   change requires frozen compatibility oracles and fresh projection validation.
4. After full typed document authoring works, make Flow the default in Runtime
   and CLI. Use explicit Stack selection for historical files; handle incomplete
   brace-based REPL input precisely. Migrate examples, primers, protocol fixtures,
   matched fixtures and scripts consistently rather than rely on parser fallback.
5. Run controlled external-subagent trials only after reproducible authoring and
   default tooling are ready. Implementation checks do not establish agent benefits.

Likely files: FlowSyntax, FlowParser, FlowSource, Runtime, Storage, CLI Program,
Flow/Runtime/Storage/Acceptance tests, migration docs and example/tool fixtures.
Keep parser/document changes, runtime staging and storage ownership separated
when delegating; source contract decisions precede implementation.

## Acceptance and validation

One Flow document must stage a record, a refined nominal type with pure validator,
and two dependent words with attached tests. Invalid later members leave no
partially staged words/types. Nominal mismatch fails compilation; validators are
pure scalar-to-Bool and remain frozen after commit/reload. Authored source and
stable bindings survive commit, rename, reload, snapshots and task abort.

Default `add(10, 20)` and Flow file definitions work in protocol and human CLI.
Explicit Stack runs historical compatibility fixtures. Root `::name`, qualified
`namespace::name` and static dot behavior remain distinct. Incomplete submissions
are distinguishable from invalid source. Library coverage gates remain enforced.

Root serializes fresh builds. Focused suites: Flow, Flow Runtime, Source, Storage,
and language Acceptance in Release. Final gate:
`pwsh -NoProfile -File scripts/Validate.ps1`; run `git diff --check` and any
versioned source/export/projection oracles required by the chosen schema. Exact
committed-source CI precedes main integration, with reports updated at publication.

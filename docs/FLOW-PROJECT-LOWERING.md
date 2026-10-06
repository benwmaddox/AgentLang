# Flow project lowering

Status: the compiler now has an opt-in source-backed batch path with transient authored-call bindings. This is a project-lowering prerequisite, not durable project editing or publication.

## Public entry points

`FlowLowering.compileBatchWords` remains the AST-based batch API. It handles explicit additions and replacements through a body-free signature catalog, lowers all proposed bodies, and verifies the complete real dictionary once.

`FlowLowering.compileWordWithCallBindings` is a transient AST helper. It captures structural call sites and reconciles them with the verified IR, but it does not prove that the AST came from authenticated source bytes.

`FlowLowering.compileBatchFlowSources` accepts a base `Context`, a `FlowSourceInventory`, and source-backed additions or replacements. The inventory names the host-declared current Flow owner IDs and carries each owner's exact source reference, text, diagnostic source label, stable ID, and revision. Changes carry the same exact source data plus an explicit add/replace revision intent. The result includes the verified program, lowered projections, final source origins, and `FlowCallBinding` rows.

`FlowLowering.compileBatchFlowProjectSources` extends that proposal with a separately host-declared inventory of Flow tests and examples. Attachment keys are the stable owner `WordId`, attachment kind, and case name; the inventory maps each key to its exact `SourceRef` and supplies the matching source document. Add, replace, and remove are explicit operations. Replacements and removals compare the expected prior source reference, additions require a missing key, and a key can have only one intent in a batch. The result contains the word-bound compilation, every remaining compiled test/example against that same final verified program, and `FlowAttachmentCallBinding` rows.

`FlowLowering.compileTestWithCallBindings` is a transient host-AST helper for detached actual/expected body reconciliation, including host-built AST fixtures that deliberately share source spans. It proves AST-to-verified-IR correspondence only; it does not authenticate source bytes or project membership.

## Source and dictionary checks

The batch path validates the entire base dictionary before building an overlay: `Words` and `WordIds` must have matching names, IDs must be nonempty and unique, revisions synchronized, parameter metadata valid, and source-origin keys and values exact. It validates every proposed change before resolution, including canonical word names, identity/revision intent, closed known types, parameter names and arity, and declared effect names. Builtins and generated words remain protected from replacement.

The signature catalog contains metadata rather than placeholder executable words. It records ordered input/output types, effects, builtin/generated/user kind, parameter names, stable target IDs, revisions, and spans. Primitive generic relationships remain available, while proposed Flow signatures stay closed and generated record names continue to derive from record fields. The final compiler remains authoritative for real body types, effects, scalar validators, cycles, IR verification, and origins.

For the source-backed API, each `SourceRef` must name a word-definition object and match the exact strict UTF-8 encoding of `Content`; invalid UTF-16 is rejected instead of replacement-encoded. Parsed owner name, stable ID, revision, body, signature, documentation, effects, and parameter metadata must agree with the base dictionary. The supplied diagnostic `SourceFile` label must equal the retained definition span's file label so source reparsing and final IR spans can be compared without normalization. This label is host-provided provenance, not proof of manifest membership.

Flow attachment documents similarly validate their test/example object kind, strict UTF-8 hash, parser version, owner name, case name, stable owner ID, owner revision, and supplied source-file label. Base cases are accepted only for owners in the host-declared Flow word-source inventory. Candidate cases must belong to those base Flow owners or a word explicitly added/replaced as Flow in the same batch. This preserves the owner frontend boundary: Stack-authored attachments remain the host's responsibility, including during a Stack-to-Flow transition; a new Flow case can be added with that transition. The exact expected-key/reference map is a host declaration: the compiler checks complete coverage and document agreement against that map and the supplied `Context`, but cannot establish that either is the current durable manifest. Base attachments compile against the immutable base program; retained and changed candidates compile against the single final word-batch program. Retained source is reparsed and re-resolved, with structural path sets and stable target IDs compared to the base compilation. A same-ID target revision advance is allowed and final IR must resolve to that exact new revision. Unchanged attachment metadata is returned with the candidate owner revision.

When source-backed attachment lowering or detached-IR reconciliation fails, the diagnostic retains its original error code, source span, expected values, and actual values. The owning word is placed in the structured `Word` field, and the message identifies the attachment owner ID, kind, and case. If the span maps to an authored call, the diagnostic also reports its actual or expected-expression body role and structural AST path. This adds attachment provenance without representing an attachment as a dictionary word or changing resolver semantics.

`ExpectedFlowOwnerIds` is an explicit host trust boundary. The compiler `Context` does not identify which entries were authored in Flow, so inventory completeness is checked against the host-declared owner set. It does not establish that this set matches a durable manifest. A user-authored Stack word may be replaced by a Flow revision under the same stable ID; the old Stack body is not misclassified as Flow. Existing Flow owners require exact source inventory proof before replacement.

## Authored-call capture and IR reconciliation

Binding sites use a structural `FlowAstPath` with statement/argument/branch indexes, not source-span uniqueness or regenerated IR ordinals. This permits separate host-built AST call nodes to share the same span while keeping distinct paths. Call-event fragments are composed in emitted expression order: receivers precede written arguments and enclosing calls; conditions and scrutinees precede branch/case calls. The capture includes direct, absolute-root, dot-stage and static callback calls, plus generated constructors/accessors and explicit scalar constructor calls. It excludes the implicit scalar validator inserted by compilation.

The final reconciliation walks verified call-like IR operations and checks event count and order, owner ID, span, source kind, selected dictionary name, operation kind, stable target identity, and exact target revision. Each public binding row carries the owner name/ID/revision, the authored `SourceRef`, and the call path, span, form, requested name, target identity, and target revision.

For unchanged Flow owners, the API reparses and re-lowers the exact retained source under the proposed signature catalog without incrementing owner revisions. It compares path sets and target stable identities before final assembly. If an ordinary short call or dot stage would select a different target ID or become ambiguous, the batch fails with caller, path, and span information. A revision advance of the same target ID is allowed; final IR reconciliation still requires the new exact target revision. Absolute-root calls continue to select the exact root key as suffix candidates are added.

The complete proposed program contains retained real Stack definitions, re-lowered retained Flow definitions, and new/replaced real Flow definitions. Replaced markers are pruned only after the incoming origin map has been validated. The assembled program is then compiled once; no changed-only placeholder program is produced.

## Boundaries still outside this API

These APIs produce compiler proposals only. They do not persist bindings, verify durable manifest ownership, allocate durable revisions, edit a project manifest, execute tests, apply library coverage/publication gates, or change the runtime/default frontend. Runtime and storage integration must convert transient compiler bindings to storage-neutral records, include definition and attachment source references, prove complete manifest membership/site coverage, execute the appropriate tests, and gate publication atomically. A successful source-batch compile is therefore not a durable commit or test-passing guarantee.

Storage and runtime planning is tracked separately in [FLOW-DURABLE-INTEGRATION.md](FLOW-DURABLE-INTEGRATION.md). The prototype still uses the existing Stack frontend by default.

## Validation evidence

The prior word-only source-binding milestone passed 564 focused assertions and the complete 25-check Release gate. The attachment source-binding slice is being validated separately; see [report 040](../reports/040-flow-attachment-bindings.md) for its focused evidence and remaining integration boundary. Word-only milestone evidence and committed CI/publication are recorded in report 038.

# Canonical source and semantic renames

`AgentLang.Source` renders parsed language objects as deterministic, LF-separated source. It operates on the typed AST and declaration records; it does not search or substitute in source-text substrings.

The public renderers are:

- `renderExpression` and `renderBody` for executable expressions.
- `renderWord includeHostMetadata`, `renderRecord`, and `renderScalar` for definitions.
- `renderTest` and `renderExample` for attached checks and examples.

Rendering uses four spaces for each nested block, preserves field and expression order, sorts declared effects, formats nested types recursively, and quotes documentation and string literals as JSON strings. Floating-point literals always retain a decimal or exponent marker so `1.0` is parsed as `Float`, not `Int`. Output has no trailing newline. Source spans and original whitespace are not part of the canonical form.

`renderWord false` emits editable source and omits host-managed maturity and revision fields. `renderWord true` includes both fields for host persistence and history. Parsing either form and rendering it again with the same mode produces the same canonical text.

Reference rewrites are semantic:

```fsharp
let body' = Source.renameReferences "billing.apply" "billing.apply-v2" definition.Body
```

This rewrites `Call`, `list.map`, `list.filter`, and `list.each` targets recursively through `if`, Option matches, and Result matches. It leaves constructor type arguments, local bindings and loads, match payload names, string and numeric literals, and documentation untouched.

Header and attachment changes have separate helpers. `renameWordHeader` changes only a matching definition name; `renameWordDefinition` changes that header and executable self-references in the body. `renameScalarValidator` updates an exact validator word reference. `renameTestOwner` and `renameExampleOwner` change the attached word and rewrite executable references in the attached body while retaining the case name and expectation. Updated objects receive canonical `SourceText`.

These helpers provide safe AST transformations for rename, history, and diff features. Callers remain responsible for finding all references to a renamed word and updating the relevant definitions and attachments.

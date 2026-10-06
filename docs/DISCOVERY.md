# Structural Discovery Foundation

`Discovery` builds an immutable, deterministic index from the runtime's effective word, record, and scalar maps. Runtime queries rebuild that index from the current dictionary for every request, so staged definitions and replacements are visible immediately without a stale cache.

## Word graph

`Discovery.build words records scalars` validates map keys, generated record/scalar references, nominal types in signatures and declarations, duplicate record fields, validator names, and every direct word dependency. It returns a private index with forward and reverse edges. Invalid snapshots raise the existing `LanguageException` diagnostics.

For user-defined words, direct edges come from `Compiler.dependencies`, including calls and statically named `list.map`, `list.filter`, and `list.each` callbacks. A generated scalar constructor has an edge to the scalar's validator. A primitive's host implementation is not expanded into invented word dependencies. `dependencies` and `searchDependency` expose direct edges; `transitiveDependencies` and `transitiveCallers` return stable, unique closures and omit the requested root even when the graph is cyclic.

`graphText index root maxDepth maxNodes` renders dependencies in stable order. It marks ancestor cycles as `[cycle]`, already expanded shared nodes as `[reused]`, and limits with explicit placeholders and an omitted unique-word count. A missing root or invalid limit raises a structured language diagnostic.

## Structural search

`searchType` searches recursively through a word's input and output type expressions. `searchOutput` searches only output expressions. Recursion follows `List<T>`, `Option<T>`, and both sides of `Result<T, E>`. Named types compare by exact name: `Email` does not match `String`, even when `Email` wraps a string. These queries inspect signatures rather than record field implementations.

`searchEffect` matches declared effect strings as written. It does not infer a smaller effect set from a body; declarations remain conservative. `searchDependency` returns words with the requested direct edge. Results use the stable ordering of the language's maps.

## Compact context

`context index root maxDepth maxWords maxUtf8Bytes` builds a breadth-first dependency context, with the root first. Word entries include their input/output types, declared effects, direct dependencies, a concise documentation summary, and maturity. Documentation is reduced deterministically to the first paragraph and capped at 256 Unicode scalar values; each word carries `documentationTruncated` when content was omitted. Type entries include complete record fields or scalar base type and validator. Reachable nominal field/base declarations are followed recursively, including cycles without repetition.

The result's `Content` is one complete JSON document. It includes the requested budgets, the included words and types, omitted word/type counts, truncation reasons, and `utf8Bytes`. The byte count is the exact UTF-8 size of the complete serialized JSON, including that count and the omission metadata; it is not a token estimate. Words and type declarations are included whole. The serializer never slices JSON or source text to meet a limit.

The root is always required. A byte limit that cannot fit the complete root entry, its reachable types, and the JSON metadata raises `DISCOVERY_CONTEXT_BUDGET_TOO_SMALL`. When a dependency entry does not fit, the context adds `maxUtf8Bytes` to `truncationReasons`; if this notice cannot fit, the request also fails with that diagnostic. `maxDepth`, `maxWords`, and byte limits are independently reported, and all omission counts are computed from the full reachable closure.

## Deliberate limits

This foundation provides exact structural queries, not semantic or embedding search. It does not infer application behavior, extract arbitrary host calls, include tests/examples/source bodies, or invoke any capability. Read-only queries can add inspection entries to an active task log, but they do not run words or mutate vocabulary or virtual providers.

## Runtime and CLI command protocol

`{"op":"words","compact":true}` returns a names-only inventory: sorted
ordinary dictionary names in `data.words`, sorted syntax names in
`data.constructs`, and `data.compact:true`. The default listing and explicit
`compact:false` retain the full metadata rows. The compact option must be a JSON
Boolean. The human equivalent is `:words --compact`; other arguments are errors.

Actual word descriptions include `flowReference`, an exact Flow target spelling
validated by the parser. Root `add` is `::add`; dictionary name
`customer.discounted-balance` is `customer::discounted-balance`. Use the
dictionary name for protocol queries and the reference for Flow source. A null
reference carries `flowReferenceUnavailableReason` when syntax cannot express
the ordinary target. Syntax descriptors do not gain ordinary word references.
The reference names a target; its signature still determines valid arguments,
output use and static callback eligibility.

The JSON-lines dispatcher and human REPL expose these commands:

| Operation | Required arguments | Result |
| --- | --- | --- |
| `type-of` | `word` | The same word or syntax descriptor metadata returned by `describe`. |
| `search-type` | `type` | `data.words` contains words whose input or output structurally contains the closed type. |
| `search-output` | `type` | `data.words` contains words whose output structurally contains the closed type. |
| `search-effect` | `effect` | `data.words` contains words declaring that effect. Unknown nonempty effects return an empty list. |
| `search-dependency` | `word` | `data.words` contains words with a direct edge to that known word. |
| `transitive-dependencies` | `word` | `data.dependencies` contains the word's dependency closure. |
| `transitive-callers` | `word` | `data.callers` contains the word's caller closure. |
| `graph` | `word`, optional `maxDepth`, `maxNodes` | Bounded text plus expansion/truncation metadata. |
| `context` | `word`, optional `maxDepth`, `maxWords`, `maxUtf8Bytes` | One complete compact context document in `data`. |

For example, JSON-lines requests may be `{"op":"search-type","type":"Option<List<Email>>"}`, `{"op":"graph","word":"customer.discounted-balance","maxDepth":5,"maxNodes":80}`, and `{"op":"context","word":"customer.discounted-balance","maxWords":12,"maxUtf8Bytes":10000}`. Human commands use the corresponding colon form, such as `:search-type Option<List<Email>>`, `:graph customer.discounted-balance --max-depth 5 --max-nodes 80`, and `:context customer.discounted-balance --max-words 12 --max-utf8-bytes 10000`.

Type search inputs use the language's closed type syntax. The parser rejects malformed and open type-variable queries; every named type must match a declared record or scalar name exactly. For example, `Email` never matches `String`, and `Option<List<Email>>` is distinct from `Option<List<String>>`. Unknown roots, unknown dependency words, and undeclared nominal types return structured diagnostics. All query results are deterministically ordered.

Graph defaults are `maxDepth=8` and `maxNodes=128`; hard limits are depth 32 and 512 expanded nodes. Context defaults are `maxDepth=6`, `maxWords=24`, and `maxUtf8Bytes=12000`; hard limits are depth 32, 512 words, and 262144 UTF-8 bytes. Negative depth, nonpositive node/word/byte limits, noninteger JSON values, and requests above a hard limit fail with structured diagnostics rather than silently falling back to defaults. Context always includes a complete root entry or returns `DISCOVERY_CONTEXT_BUDGET_TOO_SMALL`.

`context.data` is the complete compact JSON document emitted by Discovery, including its own `utf8Bytes` field. That value measures exactly the UTF-8 bytes of this compact `data` document, including its budget and omission metadata. It excludes the fixed JSON-lines response envelope (`ok`, `kind`, and `text`); it is a byte count, not a token estimate. Runtime adds no unbudgeted fields inside the context document.

Queries use the live effective maps and perform no word execution, capability checks, or provider effects. An active task log records the query and inspected roots/matches, without marking those words as used.

Run the focused checks with `dotnet run --project tests/AgentLang.Discovery.Tests` after the Core project and solution include the new module and test project.

# Structured Runtime Value Inspection

`AgentLang.Core.ValueInspection` converts public `Value` instances into a bounded, deterministic JSON tree. The caller supplies the exact compiler-authorized `VerifiedIrProgram` snapshot that defines nominal types:

```fsharp
let data = ValueInspection.toData verifiedProgram values
let json = ValueInspection.toJson verifiedProgram values
```

The top-level object uses `formatVersion: 1` and a `values` array. Values are matched against the closed types carried by containers and the record/scalar schema in the supplied verified snapshot. The formatter rejects unknown nominal names, wrong nominal kinds, mismatched payload types, missing or extra record fields, null strings, non-finite floats, and unresolved type variables. It returns no partial JSON when validation or a resource limit fails.

The JSON tags are explicit:

| Value | JSON shape |
| --- | --- |
| Integer | `{"kind":"int","value":"9223372036854775807"}` |
| Float | `{"kind":"float","value":"-0","negativeZero":true}` |
| Boolean, string, unit | `kind` plus a `value` for Boolean/string; unit has only `kind` |
| List | `kind`, typed `elementType`, and ordered `items` |
| Option | `kind`, typed `elementType`, and `case: some/none`; `some` carries `value` |
| Result | `kind`, typed `okType`/`errorType`, and `case: ok/error` with its active `value` |
| Record | snapshot `name` and `typeKey`, plus `fields` in declared order; each field includes its `name`, `type`, and `value` |
| Scalar | snapshot `name` and `typeKey`, its declared `baseType`, and the wrapped `value` |

Type descriptors use the same closed primitive/container tags. A nominal descriptor includes `name`, `nominalKind` (`record` or `scalar`), and `typeKey`. Type keys are scoped to the supplied immutable snapshot. This keeps two scalar types with the same base representation distinct.

`Int64` payloads use invariant decimal strings so JavaScript and other IEEE-754 JSON-number consumers cannot silently round large integers. Adapters should normalize these strings into a lossless integer type. Finite Float payloads use invariant round-trip binary64 text; `negativeZero` is explicit so adapters can preserve its sign even if their JSON number handling normalizes `-0`.

The inspector checks that scalar payloads match their declared base types. It does not run scalar refinement validators or certify that a public `Value` originated from the supplied snapshot. The engine integration must pair values with the exact active snapshot; same-name, same-shape values from an earlier snapshot cannot be distinguished after they have become public `Value` instances. No effects, reflection, conversion, or truncation occur here.

The initial deterministic limits are 12 semantic value/type levels, 10,000 value nodes, 100,000 JSON nodes, at most 100,000 nominal schema entries for lazy name indexing, 48 JSON nesting levels, and a conservative UTF-8 output budget of 1 MiB including a 256-byte reserve for an embedding envelope. Exceeding a limit returns a structured `VALUE_INSPECTION_*_LIMIT` diagnostic rather than truncating output or leaking a serializer exception. Container metadata is depth-checked before structural comparison, and invalid null host references produce structured diagnostics.

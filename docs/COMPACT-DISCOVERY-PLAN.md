# Compact discovery and exact Flow references

This follows the measured retrieval and namespace friction in report 057. Keep
the frozen pilot-001 runtime, primer and artifacts unchanged. This is a bounded
interface improvement before further external-agent evaluation.

## Contract

- `words` keeps its existing full response by default and with `compact:false`.
- `words` with the strict Boolean `compact:true` returns sorted ordinary word
  names and sorted syntax-construct names under the existing `words` and
  `constructs` keys, with `compact:true` marking the inventory view. Identity,
  signature, effects, maturity, deprecation and other metadata remain available
  through full listing and `describe`.
- Human `:words --compact` selects that view. Unknown, duplicate and additional
  arguments produce `CLI_INVALID_COMMAND`; no silent fallback.
- Actual word descriptions gain additive `flowReference` metadata. A root key
  has reference `::name`; dotted dictionary keys use `::` between all segments.
  Preserve the dictionary's case, including generated constructor/accessor names.
- Validate the candidate spelling with the actual Flow parser and require an
  exact ordinary call target matching the dictionary identity. Unrepresentable
  legacy identities return null with a deterministic explanation. Syntax
  descriptors remain syntax descriptions, without an ordinary word reference.
- A reference is a target spelling, not an invocation template or a promise that
  every signature is usable in a scalar Flow expression. Argument types, output
  arity and static-callback constraints still apply.

## Ownership and compatibility

Runtime and acceptance/Flow Runtime tests implement inventory and reference
metadata. CLI and CLI tests implement the human option. Protocol already merges
top-level and nested arguments, so it needs no new operation or decoder schema.
There is no grammar, typed semantic IR, storage, primitive, capability, or library
coverage change. Audit existing metadata consumers and deterministic fixtures
before claiming compatibility; default full listing must remain byte-equivalent.

## Acceptance

Prove equal full/compact inventories and deterministic ordering, including
primitive/generated/user/candidate/temporary/deprecated entries and constructs.
Reject non-Boolean compact values before successful inspection or execution.
Recording the rejected request as a task error is allowed; definitions, types,
policy, providers and evaluation stack must not change. Full descriptions retain
their type/effect/identity/dependency/lifecycle metadata after compact discovery.
Measure serialized UTF-8 bytes on the same fixture and require compact inventory
to be at most 30% of the full listing.

Fetch described references and use them in real Flow evaluation for root,
multi-segment namespace, primitive, nominal record/scalar and callback targets.
Check exact identity despite suffix/local-name collisions. Exercise parser
exceptions for protected namespace prefixes and intercepted constructor syntax;
explicit Stack compatibility remains usable where Flow cannot express a name.
Check JSONL/top-level/nested argument transport and human CLI recovery.

Run a fresh Release build and the applicable acceptance, Flow Runtime and CLI
suites through `pwsh -NoProfile -File scripts/Validate.ps1`. Inspect all required
checks, formatting/diffs and source/protocol/storage version effects before
publication. Save the results in report 058. Byte reduction is an interface
measurement, not evidence of model-token or agent-outcome improvement; another
matched external-agent comparison is still required.

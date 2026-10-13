# 260 — AOT list traversal and no instruction limit

Status: compiler/runtime milestone toward language-owned Campfire views
([plan](../docs/CAMPFIRE-VIEWS-PLAN.md)). No application or benchmark change.

## List operations in the owning AOT backend

The owning LLVM backend now compiles `ListSingleton`, `ListMap`, `ListFilter`,
`ListEach` and `ListFold`, written in Flow as `list.singleton<T>(x)`,
`items.map(f)`, `items.filter(f)`, `items.each(f)` and `items.fold(seed, f)`.
Callbacks must be user words; a primitive or generated word can be wrapped in a
function. Semantics match the interpreter: the fold callback receives the
accumulator then the item, and `each` returns `Unit`.

Following the user's decision, **list operations never relocate arena data**.
`compact { }` will be the only relocation mechanism, in a later milestone:

- **`map`** reserves a 16-byte descriptor slot per element plus the list header,
  then calls the callback per element.
  - If every result lands directly after the previous one, the results already
    form the list, so only the header is written and no bytes are copied. A
    `compact { }` callback body is expected to make this the normal case; that
    will be measured in the `compact` milestone.
  - Otherwise, each result is copied once into a fresh contiguous list.

  Choosing between the two is a runtime offset comparison, not a liveness
  decision.
- **`filter`** records an 8-byte keep flag per element, then copies each kept
  element once.
- **`fold`** returns the last accumulator as is, with no copying.
- **`each`** calls the callback and returns `Unit`.
- **`singleton`** writes the header and copies the item once.

Callback temporaries stay in the arena until an enclosing `compact { }` or the
end of the request. Elements are measured in place with the existing checked
`al_owning_measure_value`, and each callback is called through the existing
descriptor ABI. No runtime functions were added.

## No instruction limit

Following the user's direction, the 10,000-instruction limit is removed from
the owning AOT runtime, the scalar LLVM backend, the IR interpreter and the
older runtime interpreter. Verified programs have acyclic call graphs
(`IR_RECURSIVE_CALL_GRAPH`) and traverse only finite lists, so every execution
terminates. Call-depth, collection-length and value-size bounds are unchanged.

Instructions are still counted, saturating rather than limiting, wherever they
serve as a parity signal or lifecycle statistic:

- the interpreter's host `ChargeInstruction` hook;
- the scalar backend's step counter;
- the owning runtime in every profile. The owning mailbox gate requires step
  statistics from trusted-generated builds too.

Removing the per-instruction runtime call from release IR is a possible
follow-up optimization.

## Validation

All gates passed in one clean run on the final source
(`gates-20261013T130758Z`):

| Gate | Result |
| --- | --- |
| Full LLVM suite | 1,009 assertions, zero build warnings |
| IR interpreter | 161 assertions |
| New owning list-ops conformance | 142 assertions (O0/O2) |
| Owning lists (report 251) | 1,725 assertions |
| Campfire regression gate | pass, including HTTP integration and library reload |
| Native owning value-stack (C scanner, UBSan) | pass |
| Owning mailbox | pass, including trusted-generated step statistics |
| IR parity | pass |
| Native conformance | pass |
| Campfire native room-read, TrustedGenerated and Diagnostic | 238 assertions each |

The list-ops suite checks every operation against the interpreter and an
explicit byte-level wire oracle at O0 and O2, and requires `MoveBytes = 0` and
deep copies of at most one copy per output element.

- An int→string map copied **0 bytes**: each callback result landed directly
  after the previous one.
- An identity map copied each element exactly once (120 bytes for six strings).
- `filter` copied only kept elements, and folds and `each` copied nothing.

A first gate run failed the owning-mailbox gate because release builds had
stopped counting instructions. Counting was restored in every profile, still
without a limit. That run's logs are retained.

Tests changed by the semantics change:

- Tests that expected an execution to *stop* at 10,000 instructions now assert
  that it completes with matching results and instruction counts. That covers
  four interpreter tests and the owning refined-Int, `float.to-string`,
  scalar fuel/source and validator cases.
- The `validator-fuel-limit` error fixture is removed.
- A module-metadata test used the step-limit diagnostic to carry a non-ASCII
  source file through C metadata. It now gives `add` a non-ASCII primitive
  definition span and checks the overflow diagnostic instead.

## Sealed evidence

[Archive](evidence/260-aot-list-traversal/260-aot-list-traversal-evidence.zip)
and [manifest](evidence/260-aot-list-traversal/manifest.json): 32 entries,
392,637 bytes, SHA256
`46b33fe40570b6ecf271c78fb0400a5d5199d41ceadd7e3437c28ad8de438166`. It holds the
final gate logs and summary, list-ops receipts with per-case copy metrics, the
gate and sealing tools, and snapshots of every changed source, test, fixture and
document.

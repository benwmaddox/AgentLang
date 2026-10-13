# 262 — `string.join`, Rails-exact HTML escaping, and `Html` as a library refined type

Status: stdlib and compiler milestone toward language-owned Campfire views
([plan](../docs/CAMPFIRE-VIEWS-PLAN.md), step 2). No application or benchmark change.

## User decisions

- `Html` is **not built into the compiler**. It is an ordinary refined type
  (`type Html : String { validate … }`) defined in library code: "Html can just
  be the more strict type."
- Refined types are checked **at compile time for literals**, "so it makes sure
  it is valid as a literal", without making any particular type a compiler
  built-in.
- Strings stay immutable and bump-allocated on the request data stack. Join
  copies each part once into a result sized up front.

A built-in `THtml` prototype (about 49 compiler sites) was written first and then
reverted after the first decision. Nothing from it remains.

## Stdlib primitives

| Primitive | Signature | Interpreter | Owning AOT |
| --- | --- | --- | --- |
| `string.join` | `List<String> -> String` | new | new: plan + write, one copy per part |
| `string.html-escape` | `String -> String` | new | new: plan + write |
| `string.contains` | `String String -> Bool` | existing | new |
| `string.starts-with` | `String String -> Bool` | existing | new |
| `string.ends-with` | `String String -> Bool` | existing | new |

- **Escaping rule.** `string.html-escape` replaces exactly `& < > " '` with
  `&amp; &lt; &gt; &quot; &#39;`. Every other UTF-16 code unit, including
  unpaired surrogates and NUL, is copied unchanged.
  - This is Rails `ERB::Util.html_escape`. It is checked against 18 vectors
    generated inside the pinned Rails reference image (ActiveSupport
    8.2.0.alpha, `once-campfire-verification:reference-90b3300`), stored with
    their generator in `tests/AgentLang.OwningHtml.Tests/vectors/`.
  - Rails does not preserve existing entities (`&amp;` becomes `&amp;amp;`).
    Rails uses the same escaping for text and attribute contexts.
- **Native join and escape.** Each primitive measures its exact result
  (`*_plan`), reserves it once, then writes it (`*_write`).
  - Join walks the packed `List<String>` layout exactly and fails closed if the
    walk does not end at the list owner end.
  - Escape expands the five entities in place as it writes.
  - Neither primitive copies the input to an intermediate buffer.
- **Native search.** `al_owning_string_find` is an ordinal UTF-16 code-unit
  search with three modes: contains, starts-with and ends-with. An empty needle
  matches, as with .NET `StringComparison.Ordinal`.
- **Supporting changes.**
  - All five primitives are in the arena-lifetime primitive set, so they can
    appear inside proven `compact { }` blocks.
  - Help text was added for the two new primitives.

## Compile-time refined literals

After program verification, the compiler finds every literal pushed directly
into a refined-type constructor (`Constant` immediately followed by
`WrapScalar` with a validator), in every function body and nested block. It runs
the constructor on that literal through the interpreter, the same path used at
run time.

- **Invalid literal.** The build fails with `REFINEMENT_LITERAL_INVALID`, naming
  the constructor, the owning function and the literal's source span.
- **Valid literal.** The build proceeds, and the value is still constructed
  normally at run time.
- **Non-literal construction.** It is unchanged and checked at run time
  (`REFINEMENT_FAILED`).
- **Validator errors.** If the validator itself fails on the literal (for
  example `DIVIDE_BY_ZERO`), the build fails with `REFINEMENT_LITERAL_INVALID`,
  carrying the cause code. Construction would fail the same way at run time.
- **Validators are pure.** The language already rejects effectful validators
  (`TYPE_VALIDATOR_EFFECT`, tested), so the compile-time host's effect guard is
  defensive only.
- **Scope.** Only verified word bodies are scanned. Test, example and eval
  bodies keep run-time semantics, so authored `=> error REFINEMENT_FAILED`
  cases still work.

The check is generic: it knows nothing about `Html`.

## `Html` in library code

The test fixture (`tests/AgentLang.OwningHtml.Tests/owning-html.flow`) defines:

```flow
type Html : String {
    validate html.safe?
}

fn html.escape(text: String) -> Html {
    effects none
    Html.new(string.html-escape(text))
}

fn html.join(parts: List<Html>) -> Html {
    effects none
    Html.new(string.join(parts.map(html.text)))
}
```

- **Example policy.** `html.safe?` rejects `<script` and `javascript:`. It is a
  policy example, not a sanitizer.
- **Compile-time rejection.** `Html.new("<script>alert(1)</script>")` fails to
  build, both at the top level and inside an `if` branch.
- **Run-time rejection.** `Html.new(text)` on a runtime string is rejected with
  `REFINEMENT_FAILED` in both the interpreter and native AOT when the policy
  fails.
- **Open item.** In view modules, raw `Html.new` should accept only literals,
  with runtime text entering only through `html.escape`. That convention is not
  enforced yet; a lint is the planned way to enforce it.

## Results (`AgentLang.OwningHtml.Tests`, 351 assertions)

- **Escaping vs Rails.** All 18 Rails vectors match byte for byte, both in
  `TrustedValues.htmlEscape` and in native AOT at O0 and O2. Four extra inputs
  also match: lone high and low surrogates, NUL followed by `<`, and 300 × `<`.
- **Copy count.** Each escape writes its result exactly once: deep-copy bytes
  equal the result extent.
- **Join parity and single copy.** Five join cases (empty list, one empty part,
  two parts, mixed Unicode with lone surrogates, 400 parts) equal
  `String.Concat`. Deep-copy bytes equal the result extent, so every part is
  copied exactly once. For example, 400 parts give a 9,392-byte result and
  9,392 bytes copied.
- **Search parity.** Contains, starts-with and ends-with match .NET ordinal
  behaviour on 11 cases each, including empty needles and a lone low surrogate
  inside a surrogate pair. Results are identical at O0 and O2.
- **Library `Html` words.** Every `Html` word commits and passes its authored
  Flow tests. Native AOT output equals the interpreter for every case.

### 40 × 9.6 KB page prototype

`native.html.page` maps 40 names, each about 9.5 KB with escapable characters,
through a `compact { }` row template. Each row is a literal `Html.new` open tag,
plus `html.escape(name)`, plus a close tag. It then joins the rows and wraps them
in `<ul>`. The output is 382,520 UTF-8 bytes (765,048 bytes UTF-16), about the
size of the 416–464 KB reference pages in report 259.

| O2 metric | Value |
| --- | --- |
| deep-copy bytes | 4,596,600 (6.0× the page) |
| compact move bytes | 765,440 (1×, one relocation per row) |
| peak arena | 3,823,496 bytes (5.0× the page) |

The 6× is fully accounted for:

- escape: 1×
- the row's two `concat` calls: 2×
- the page `join`: 1×
- the `<ul>` and `</ul>` `concat` calls: 2×

Templates cannot yet build a part list natively, because `list.append` and
`list.concat` are not in the owning AOT subset. With native list building, each
level joins once: escape, row join, page join, about 3×. That is the next step
(views plan 2b).

**Timing is not a throughput result.** The test harness runs the checked
(Diagnostic) runtime profile, which has init and poison bitmaps and traces, and
it marshals all inputs and allocates a 16 MB stack on every call.

| O2, checked profile | µs per call |
| --- | --- |
| page | 74,221 |
| harness floor: copy-free `contains` over the page-sized input | 9,264 |

**Not delivered: µs per GET.** The views plan asked for µs per GET at O2.
The trusted-generated compile path is tied to a Campfire host adapter and ABI,
so the page word could not be measured there in this milestone.

- In the checked profile, the page spends about 65 ms above the harness floor.
  Byte-at-a-time copies of 4.6 MB plus diagnostic bitmap marking are plausible
  causes, but they are unmeasured.
- A trusted-profile number is needed to choose between fewer copies (native list
  building) and faster copy loops.
- The UTF-16 → UTF-8 output cost is also unmeasured.

## Gates

All 16 gates pass (run `gates-20261013T153622Z`; the 15 report-261 gates plus `owning-html`):

| Gate | Result | Seconds |
| --- | --- | --- |
| llvm-full-suite | pass | 297 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 6 |
| owning-html | pass | 56 |
| flow-tests | pass | 3 |
| flow-runtime-tests | pass | 22 |
| ir-interpreter | pass | 2 |
| campfire | pass | 295 |
| native-value-stack | pass | 286 |
| owning-mailbox | pass | 72 |
| ir-parity | pass | 13 |
| native-conformance | pass | 312 |
| owning-lists | pass | 8 |
| owning-list-ops | pass | 15 |
| room-read-trusted-off | pass | 157 |
| room-read-diagnostic-off | pass | 156 |

An earlier run failed `llvm-full-suite` and `native-conformance`. One LLVM test used `string.contains` as an example of a primitive outside the owning subset; it is now native, so the test uses `string.guid-canonical?` instead.

## Evidence

Sealed in [`evidence/262-string-join-html-escape`](evidence/262-string-join-html-escape/manifest.json):

- gate logs and summary;
- the last three test receipts, which carry source pins and metrics;
- the gate and seal tools;
- every changed source file.

The Rails vectors hold no secrets and are committed with the test.

## Next

1. A trusted-profile page measurement without the Campfire adapter: a no-host
   trusted compile entry, or the room-read host. Use it to choose between
   native `list.append` / `list.concat` (views plan 2b) and faster copy loops.
   Both are probably needed.
2. Before the 259 rerun, confirm the HTTP host's stack capacity. The prototype
   peaks at 3.8 MB of arena for a 382 KB page.
3. Proven structural access.
4. The message partial, layout, rich text and signed avatars.
5. Rerun the 259 protocol: no cache, AOT only, against Rails and Rust.
6. A lint that restricts `Html.new` to literals in view modules.

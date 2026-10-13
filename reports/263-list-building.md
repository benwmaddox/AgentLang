# 263 — List building, proven literals, and trusted-profile page measurements

Status: language and runtime milestone toward language-owned Campfire views
([plan](../docs/CAMPFIRE-VIEWS-PLAN.md), steps 2 and 2b). No application or
benchmark change.

## User decisions in this milestone

- **No list length limit.** The 10,000-element cap is removed from the
  interpreter, the native runtime and `docs/CONTAINERS.md`. Memory bounds lists:
  the native arena capacity, or the interpreter's value-size budget. Host
  root-value and record-field count bounds are separate and unchanged.
- **Proven literals skip the run-time check.** `Html.new` (any refined
  constructor) always validates, but a literal the pure validator accepts at
  compile time is not validated again at run time.
- **Raw versus escaped HTML: one type, explicit raw.** Run-time markup enters
  only through a distinctly named `html.raw(text)`. Enforcement comes with the
  templates in the next milestone: a refined constructor that is literal-only
  outside its own namespace.
- **UTF-8 internal strings** are planned:
  - `string.byte-length` and a character-length function;
  - unpaired surrogates are rejected at the boundary.
- **Templates** will come in both styles: `html"…{x}…"` and `html { … }`.
- **Shared immutable literals:** literals are stored once in read-only data and
  referenced by every worker (next milestone).
- **Kept for later review** ([PENDING-REVIEW.md](../docs/PENDING-REVIEW.md)):
  immutable strings, workflow ceremony and a shared response cache. The design
  for writing responses straight to the socket is in
  [RESPONSE-STREAMING.md](../docs/RESPONSE-STREAMING.md).

## List building

- **`[a, b, c]` list literals (Flow/2).**
  - Every element must have the first element's exact type
    (`FLOW_LIST_LITERAL_TYPE`), and a trailing comma is allowed.
  - `[]` is rejected with `FLOW_LIST_LITERAL_EMPTY`; use `list.empty<T>()`.
  - Literals lower to a new core constructor `ListOf n` and IR operation
    `ListOf(T, n)`. The interpreter, IR verifier, formatter, rename rewrite,
    lint, stored AST paths (`listLiteralItem`), vocabulary serialization and
    arena-lifetime analysis all handle it.
- **Native list literal.** One sized list. Each element is range-copied once,
  in order, behind the 8-byte header.
- **Native `list.append` / `list.concat`.**
  - The runtime writes the header (`al_owning_list_combine_header`), then does
    two range copies.
  - Element bytes are copied exactly once, measured: 20,000 strings plus one
    element copies 632,024 bytes, which is the element bytes plus the new
    element's bytes.
  - Both work inside proven `compact { }` blocks.
- **Fused join.** A String list literal passed straight to `string.join`
  becomes an n-way join over the parts. No list is built
  (`al_owning_string_join_parts_*`). Templates lower to exactly this shape.

## Proven refined literals

`RefinedLiterals.evaluate` runs a literal through its refined constructor against
the verified program. The compiler and native backend share it.

- **Compile time.** Runtime.fs raises `REFINEMENT_LITERAL_INVALID` as in report
  262.
- **Native backend.** It records the `WrapScalar` sites in function bodies whose
  literal the validator accepted, and emits a plain retag there.
- **Measured.** The literal version of a word returns exactly one fewer frame
  than the same word built from a run-time string. The run-time version is still
  rejected with `REFINEMENT_FAILED` for invalid input.
- **Why only function bodies.** Detached entry bodies number sites from zero,
  so only function bodies, whose site ids are owner-qualified and unique, are
  eligible.
- **Every backend skips the same constructions.** The interpreter, the owning
  AOT backend and the scalar backend (`LlvmAot`) all consult the same cached
  `RefinedLiterals.proven`. The first gate run showed that skipping in AOT alone
  broke parity in two ways:
  - **Call depth.** A literal constructed at call depth 64 still exceeded the
    interpreter's 64-frame limit through its validator call.
  - **Step counts.** The mailbox's measured IR steps no longer matched.
- **Tests that changed with the semantics.**
  - The LLVM depth tests now build their constructor input from an operation
    (`0 + 1`, `x and true`), so the validator still runs. Their frozen step
    counts rise by exactly the two added instructions.
  - New cases show that a proven literal at the same depth is a plain retag.
- **Mailbox step oracle.** In the frozen fixture
  (`tests/fixtures/native-conformance/owning-mailbox-refinements.json`), each
  handler that builds a `PositiveId` from a literal drops by exactly 12 steps,
  one `is-positive?` call:
  - initialize 44→32, begin 48→36, resumeEmpty 33→21, resumeSuccess 53→41;
  - resumeFail is unchanged;
  - the RETURN-versus-KEEP resume delta stays 51.

  This is recorded as an `oracleProvenance.semanticTransitions` entry chained
  from the previous fixture hash (`DD6D9120…AA47B` → `78FCCB87…14F9D`), and
  `Verify-OwningMailbox.ps1` pins both. Values, bytes, layouts, diagnostics and
  copy accounting are unchanged.

## Runtime copy speed

- The freestanding runtime (`-ffreestanding -fno-builtin`, no libc) copied one
  byte at a time. Bulk copies and overlapping moves now use 16-byte unaligned
  chunks, loaded before they are stored, which is safe for both directions.
- `string.html-escape` now copies runs of safe code units in bulk and classifies
  each unit with a single bitmask test.

## Measurement method (trusted profile)

Report 262's timings came from the checked profile: bitmaps, traces, and
marshaling on every call. This milestone adds:

- `OwningStackAot.compileWithRuntimeProfile`, a host-free trusted-generated
  build;
- `OwningStackCompiledProgram.LastNativeElapsedTicks`, the time spent inside the
  native entry only.

Samples on this machine are bimodal (the median is up to 2× the minimum), so
comparisons use the minimum of 30 interleaved samples.

40 × 9.6 KB page (382,520 UTF-8 bytes), O2, native time only:

| Build | Page | Copied bytes | Notes |
| --- | --- | --- | --- |
| Checked profile, report 262 | 75 ms | 4.60 MB (6.0×) | bitmaps and traces dominate |
| Trusted, byte copies | 2.48 ms | 4.60 MB | |
| Trusted, 16-byte copies | 1.72 ms | 4.59 MB | `concat` chains |
| Trusted, list literal + join | 1.39 ms | 4.59 MB | literal copied into a list |
| Trusted, fused join | **1.25 ms** | 3.06 MB (4.0×) | |
| Harness floor (copy-free `contains`, page-sized input) | 0.29 ms | 0 | input admission |

Remaining copies of the dynamic content:

1. the name is copied into the row call;
2. the escape;
3. the row join;
4. the page join.

String constants are also copied into the arena on every use. In the fused
join test, 64 of 120 copied bytes are the two literals. Shared static literals
remove those copies, and part-list responses (RESPONSE-STREAMING.md) remove
both joins.

## Readable canonical source

The Flow formatter used .NET's HTML-safe JSON encoder, so a canonical source
literal `"<li>"` became `"<li>"`. String literals now use relaxed
escaping:

- markup stays readable;
- quotes, backslashes and control characters are still escaped;
- the parser reads the output back unchanged.

## Tests

- `AgentLang.OwningListBuild.Tests` (100 assertions), at O0 and O2 with
  interpreter parity:
  - Int, String, record and nested list literals;
  - formatter round trip and reload;
  - static errors;
  - 20,001-element `append` and 20,002-element `concat`;
  - empty-list edge cases and record elements;
  - fused-join copy accounting;
  - proven-literal frame accounting and run-time rejection.
- `AgentLang.Acceptance`: the old "16,384-element list must fail" case now
  asserts that the list builds.

## Gates

All 18 gates pass (run `gates-20261013T172943Z`): the 16 report-262 gates plus `owning-listbuild` and `acceptance`.

| Gate | Result | Seconds |
| --- | --- | --- |
| llvm-full-suite | pass | 389 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 10 |
| owning-html | pass | 97 |
| owning-listbuild | pass | 15 |
| acceptance | pass | 7 |
| flow-tests | pass | 4 |
| flow-runtime-tests | pass | 26 |
| ir-interpreter | pass | 3 |
| campfire | pass | 250 |
| native-value-stack | pass | 296 |
| owning-mailbox | pass | 67 |
| ir-parity | pass | 13 |
| native-conformance | pass | 309 |
| owning-lists | pass | 10 |
| owning-list-ops | pass | 19 |
| room-read-trusted-off | pass | 151 |
| room-read-diagnostic-off | pass | 152 |

An earlier run failed `llvm-full-suite`, `native-conformance` and `owning-mailbox` because AOT alone skipped proven literals. The fixes are described under "Proven refined literals".

## Evidence

Sealed in [`evidence/263-list-building`](evidence/263-list-building/manifest.json):

- gate logs and summary;
- the last three list-building receipts;
- the tools;
- the changed sources.

## Next

1. Shared immutable literals: a static read-only region addressable by
   descriptors.
2. Templates: `html"…"` and `html { … }`, lowering to fused joins, with a
   literal-only `Html` constructor outside `html.*` and `html.raw`.
3. UTF-8 internal strings: `string.byte-length`, character length, and
   rejecting unpaired surrogates.
4. Proven structural access.
5. The message partial and layout, then the 259 rerun.

# 265 — UTF-8 internal strings

Status: compiler, runtime and Campfire host milestone (views plan step 3).
Campfire's page is unchanged byte for byte; the native host is faster.

## User decisions

- **Strings are UTF-8 internally.** The owning layout is a u32 UTF-8 byte
  count, 4 zero bytes, the UTF-8 bytes, and zero padding to 8. It was UTF-16.
- **`string.byte-length`** (UTF-8 bytes) and **`string.char-length`** (Unicode
  scalar values) are the two length functions.
- **`string.length` is a deprecated alias** of `string.byte-length`. It must be
  removed by **2026-11-01** (`docs/PENDING-REVIEW.md`).
- **Unpaired surrogates are rejected** at every boundary, because they have no
  UTF-8 form.

## What changed

- **Owning runtime** (`owning_stack_runtime.c`):
  - external Strings are validated as UTF-8 when scanned;
  - concat, join, join-parts, result plans and the ASCII writer all use
    `8 + bytes` payloads;
  - `string.html-escape` and `string.find` work over bytes;
  - new `al_owning_string_char_length`.
- **Owning AOT** (`OwningStackAot.fs`): literals are emitted as UTF-8, and
  host values are measured, encoded and decoded as UTF-8. A string with an
  unpaired surrogate is refused with `STRING_INVALID_UTF16`.
- **Interpreter and core:** both new primitives; `importHostValue` and the
  host-value budget reject ill-formed strings; the parser defensively
  rejects ill-formed literals (the lexer already does).
- **Mailbox runtime:** validated UTF-8 text input is copied as-is. Before, it
  was transcoded to UTF-16.
- **Campfire native host (application code):**
  - the SQLite adapter uses SQLite's UTF-8 APIs;
  - cookie and browser checks work over byte views;
  - the request encoder and HTML encoder take UTF-8;
  - alert and refresh-token spans are copied, not transcoded;
  - the build script's source pins are updated.

## Fixture transitions (recorded, hash-chained)

The mailbox fixtures were frozen with UTF-16 byte oracles. Each was updated
with a recorded transition and a new pin in `scripts/Verify-OwningMailbox.ps1`.
Except for one value, noted below, no native output was used: each value was
re-derived by script from the fixture's own unchanged logical texts.

- **`owning-mailbox.json`** (`35d42491…` → `1c7e4abb…`):
  - the String encoding block and the Unicode lifecycle hex are now UTF-8;
  - **large request:** UTF-8 halves the old 4,096-character request, so it
    no longer overflowed the bank. The request is now 8,192 ASCII bytes. That
    keeps the 8,200-byte String and the intended retained-capacity failure.
    Only `utf8InputBytes` changes (8,193 → 16,385).
- **`owning-mailbox-sums.json`** (`ddd86cbd…` → `2322f915…`):
  - payload sizes and hex are re-derived; every extent is unchanged, and so
    are staging, import, publication and deep-copy totals;
  - **one-byte-short capacity:** the resume text doubled from 7 to 14 ASCII
    bytes, so the State is still 32 bytes against a 31-byte bank. Only
    `utf8InputBytes` changes (27 → 34).
- **`owning-mailbox-refinements.json`** (`78fccb87…` → `433e59ce…`): a second
  `semanticTransitions` entry (`utf8-string-encoding`). All its Strings are
  ASCII, so only `payloadBytes` drop, by one per one-character String.
- **`native-value-stack.json`** (not hash-pinned; the recorded notes are
  `stringWorkload.report265Utf8Transition` and `report265Utf8SectionsTransition`):
  - **String workload.** Every byte count, extent, tag offset, serialized
    image and `string.length` output was re-derived. That includes the
    `codeUnits` field, which the flow fills from `string.length`.
    `long-bmp-astral` now reports 34 bytes (was 18 units) and `astral-scalar`
    reports 10 (was 6).
  - **Other sections:** sums, lists, stable arena, NonEmptyString, nominal
    String, nominal Int and PositiveId. Each UTF-16 String image was replaced
    by its UTF-8 image, and the dependent sizes were recomputed. For example,
    the owner-range literal copies go from 80 to 56 bytes, and the "error"
    literal from 24 to 16.
  - **Rejection checks.** The isolated-surrogate cases became new
    `unpaired-surrogate-rejected-at-boundary` checks: the native boundary must
    refuse them with `STRING_INVALID_UTF16`. This covers four String-workload
    inputs, one sum case and one nominal case.
  - **Capacity probe.** It joined two lone surrogate halves, which cannot
    exist in UTF-8. It now joins `é` and `ü`, which keeps every extent
    (16 + 16 → 16).
  - **The one value taken from native output** is `allCasesPeakLocalBytes`,
    42 → 41. The C runtime test reported it, and it is recorded as a
    post-execution correction.
  - **Partway through the port,** the experiment aborted at its first
    surrogate input, which hid more than 800 later checks. All 1,382 now run
    and pass.
- The `utf16StagingBytes` stat keeps its name for ABI stability. It now counts
  UTF-8 String staging.

## Tests

- **New:** `AgentLang.OwningUtf8.Tests`, 181 assertions:
  - byte and char length across 1- to 4-byte scalars;
  - escape and find over multi-byte text;
  - surrogate rejection;
  - parity between the interpreter and AOT.
- **UTF-8 oracles updated:**
  - OwningHtml (359);
  - OwningListBuild (146);
  - OwningListOps (142);
  - OwningLists (1,725);
  - the LLVM suite (1,021, with a new native-boundary surrogate rejection);
  - the C runtime test (58 cases, 872 checks);
  - the Campfire room-read adapter tests;
  - the mailbox verifier (2,257 checks);
  - the native value-stack experiment (1,382 checks).

## Performance (report-259 protocol, ABBA)

The Campfire native HTTP host was run with the 259 protocol plus memory capture
(`.agentlang/campfire-bench/Native-Bench.ps1`). The run order was old, new,
new, old: 2 s warmup at 4 clients, then 8 s at 16 clients, gzip off, on the
`snap-nc` snapshot. The baseline is the UTF-16 trusted O2 host built earlier the
same day (`build-trusted-generated-20261013T110751812Z`, before reports 262–264).

| Build | Req/s | Server CPU / req | p50 | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| UTF-16 baseline A | 4,985 | 0.79 ms | 3.16 ms | 4.20 ms | 12.6 MB | 48.6 MB |
| 265 UTF-8 B | 7,611 | 0.51 ms | 2.08 ms | 2.64 ms | 12.5 MB | 48.5 MB |
| 265 UTF-8 B | 7,603 | 0.51 ms | 2.08 ms | 2.70 ms | 12.5 MB | 48.4 MB |
| UTF-16 baseline A | 5,133 | 0.76 ms | 3.07 ms | 4.04 ms | 12.6 MB | 48.5 MB |

- **+51% throughput and −34% CPU per request** against that baseline. Memory is
  unchanged. The baseline also predates reports 262–264 (join, list building,
  step-counter removal), so the gain is the combined effect, not UTF-8 alone.
- **Output:** every run has 0 invalid responses and the same 10,073-byte page.
- **Likely main cause:** SQLite text and the HTTP response are both UTF-8, so the
  per-request UTF-16 transcoding in and out is gone. ASCII-heavy pages also
  halve their String bytes.

### Against Rails and Rust (history)

These are the report-259 no-cache runs. **The pages are not yet equivalent**,
so this is context, not an apples-to-apples result.

| Server | Page | Req/s |
| --- | ---: | ---: |
| Rails (259) | 464 KB, full Campfire page | 19 |
| Rust port (259) | 416 KB, full page | 1,322 |
| AgentLang native (265) | 10 KB, room skeleton | ~7,600 |

The native page is 130 HTML nodes; Rails renders 4,896 (from the structural
oracle in `.agentlang/views-reference/structdiff.py`). The Rust port differs
from Rails only by CSRF tokens, the copy-link host, asset digests and importmap
text. An equivalence claim must wait for the full-page work in plan steps 4–7.

## Gates

All 19 gates pass (run `gates-20261013T221309Z`, 26 min).

| Gate | Result | Seconds |
| --- | --- | --- |
| llvm-full-suite | pass | 277 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 7 |
| owning-html | pass | 36 |
| owning-listbuild | pass | 19 |
| owning-utf8 | pass | 6 |
| acceptance | pass | 7 |
| flow-tests | pass | 4 |
| flow-runtime-tests | pass | 24 |
| ir-interpreter | pass | 3 |
| campfire | pass | 246 |
| native-value-stack | pass | 285 |
| owning-mailbox | pass | 67 |
| ir-parity | pass | 13 |
| native-conformance | pass | 273 |
| owning-lists | pass | 8 |
| owning-list-ops | pass | 16 |
| room-read-trusted-off | pass | 153 |
| room-read-diagnostic-off | pass | 151 |

The earlier run `gates-20261013T211322Z` passed 18 of 19. `native-value-stack` failed on UTF-16 oracles in its fixture, which the transition above fixes.

## Next

1. Commit the structural oracle as a tool.
2. Templates: `html"…"` and multi-line `html"""…"""` with `{expr}`
   interpolation, compile-time skeleton validation, well-formed interpolated
   snippets, and `html.raw`.
3. General app-declared host capabilities, which remove Campfire from the
   compiler.
4. Full-page data, then the message partial, then layout, head and composer.
5. Rerun the 259 protocol against Rails and Rust on equivalent pages.

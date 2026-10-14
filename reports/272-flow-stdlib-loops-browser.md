# 272 — Flow stdlib slice 1, loops and operators, browser normalization in Flow

Report 272 starts the restructure the user chose after 271: Campfire is to be
an application written in Flow, with generic plumbing in the language's
standard library and only truly necessary code kept in C and F#.

## User direction (2026-10-14)

- Campfire must be pure Flow. "Restructure first": build a generic stdlib,
  port the app's C and F# logic to Flow, delete the .NET Campfire host, then
  resume rich text and boosts.
- Long-term rule: keep in C/F# only what is needed (parsing, lowering to LLVM,
  the minimal native runtime, the OS boundary). Everything else moves into
  Flow's stdlib.
- Syntax: enums without `case`, records without `field`, names without `(` or
  `)`, `expect` tests, C-like operators with mandatory spaces, loops with `var`
  locals and `break`/`continue` and a runtime step budget (no `while`).
- Browser normalization: keep it lean.
- An `inline` attribute on `fn` is approved as a call-overhead hint.
- AgentLang merges to main as soon as work is good.

## Language

| Feature | Summary |
| --- | --- |
| Enums, records | Bare cases and fields; `case`/`field` removed from the formatter. |
| `expect` tests | Step lists of `let` and `expect <Bool>`; `expect a == b` reports both sides; a final bare Bool counts as an expect; `expect effects { }`. `=> value` is legacy. |
| Operators | `* / % + - < <= > >= == != && \|\|`, C precedence, only with whitespace on both sides (names keep symbols like `-`). No implicit conversions; `&&`/`\|\|` short-circuit; `%` is the new `int.remainder` (C semantics). |
| Loops | `var` locals with reassignment; `for i in a..b`, `for x[, i] in list`; `break`/`continue`; statement `if` without `else`. Real native back edges; per-iteration arena reclaim (an Int-only loop stays at 72 B for N = 1,000 and 100,000). 10,000,000 iterations per invocation, then `RUNTIME_LOOP_BUDGET_EXCEEDED`, identical in the interpreter and native code. |
| `else if` | Flow/2 chains; the formatter prints them flat. |

The older arena LLVM backend rejects loops (`IR_LLVM_LOOP_UNSUPPORTED`).

## Standard library, slice 1

- **Bytes:** a new value type with the String layout under its own runtime
  kind (11), so bytes from a host skip the UTF-8 check. It cannot yet cross a
  host boundary.
- **Primitives (interpreter and native):** `bytes.*` (from/to UTF-8, length,
  concat, slice, xor, constant-time equality), strict `base64.*` and
  `base64.url-*`, `hex.encode`, `crypto.sha256`, `crypto.hmac-sha256`,
  `crypto.pbkdf2-sha256`, `crypto.random-bytes` (effect `random.read`),
  `string.split` / `index-of` / `slice` / `byte-at` / `byte-at-or` /
  `to-lower-ascii`, `int.parse`.
- **Strictness:** base64 has one text per byte string (exact alphabet, no
  whitespace, required padding for `base64.decode`, zero tail bits).
- **Vectors:** SHA-256 at the 55/56/64-byte and multi-block boundaries,
  RFC 4231 case 6 (131-byte key), PBKDF2 two-block output, and the Rails CSRF
  global-token vector.
- **Native:** portable C SHA-256/HMAC/PBKDF2 (no `memcpy`, freestanding), one
  plan/write dispatcher for all results. The OS random source is linked only
  into modules that call `crypto.random-bytes`; every other module keeps a
  kernel32-only import table.
- **Parity harness:** `tests/AgentLang.Stdlib.Tests` replays 100 literal
  cases through native O0, O2 and the trusted O2 build that benchmarks use,
  and requires native = interpreter.

## Campfire

- **Browser normalization is Flow.** Requests carry the raw `User-Agent`;
  `http.flow` calls `campfire.browser.from-user-agent`. Deleted:
  `browser_normalizer.c/.h`, `Browser/UserAgentNormalizer.fs`,
  `RubyVersion.fs`, `BrowserHost.fs`, and native host operation 6 (retired).
- **Lean port.** The first, fold-based port was replaced the same day by a
  loop-based one that keeps only the paths that can change
  `campfire.browser.supported?`: the product tokenizer, the gem's kind order,
  versions for Safari, Chrome, Firefox, Opera and Internet Explorer, and bot
  detection. 815 code lines instead of 1,337.
- **Corpus.** 462 User-Agents (385 from the Ruby fixture), 508 comparisons and
  248 thresholds, frozen from the retired F# port. Expectations are
  canonicalized to what the policy reads: a bot is (other, none, bot) and any
  other family is (other, none). All pass in the interpreter and natively at
  O0 and O2.
- **CSRF in Flow (written; wired in 273).** `csrf.flow` reproduces host
  operations 11-13 with the same names and signatures, verified against
  Rails-format tokens built by an independent Python oracle (21 tests). It is
  wired in, and operations 11-13 deleted, in the next step.

App code, 271 → 272: C 13,801 → 11,797 lines (−2,004); F# 3,665 → 2,885
(−780); non-test Flow 2,269 → 3,378.

## Performance

### Quick benchmark (room page, 4 lifetimes, true median)

| Server | Page bytes | Req/s | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 272** | 460,238 | **2,770** | **1.30 ms** | 6.2 ms | 23.6 MB | 47.1 MB |
| AgentLang 271 | 460,238 | 2,749 | 1.32 ms | 6.2 ms | 23.5 MB | 47.2 MB |
| Rust, identity (recorded, 259) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded, 259) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

+0.8% req/s and −1.7% CPU per request against 271: within run-to-run spread.
The page is unchanged (460,238 bytes) and all 320 boost-form CSRF tokens on it
still verify against the session secret.

**The benchmark sends no `User-Agent`.** Neither the native bench nor the
report-259 protocol passes `--user-agent`, so all three servers take their
"missing header" path. The comparison stays consistent, but it does not
exercise User-Agent parsing. With a real Chrome header our side would add the
cost below per request; Rails and Rust would pay their own parsers.

### Native micro-measurements (trusted O2, min of 2,000+ runs)

| Measurement | Time |
| --- | ---: |
| Lean User-Agent, typical Chrome header | 50 µs |
| Lean User-Agent, corpus median / p90 | 31 µs / 65 µs |
| Empty loop with an Int `var`, per iteration | ~25 ns |
| `string.byte-at` + `match`, per call | ~80 ns (now `byte-at-or`: inline) |
| Call to a small Flow word | ~27 ns |

The retired C normalizer took a few microseconds. The loop numbers are the real
finding, and they affect every Flow loop, rendering included:

- A whole-program LTO experiment (runtime C and generated IR linked as
  bitcode) changed nothing: 25 ns per iteration with or without it. The cost
  is the work, not the call: every iteration reserves, stores and accounts
  arena slots for the index and each scalar result, and relocates carried
  `var`s at the latch.
- The fix is structural: keep scalar `var`s and loop indexes in registers,
  skip per-iteration reclaim when a body allocates only scalars, and return
  small `Option`s in registers when they are matched immediately. `inline fn`
  (approved) removes call overhead for small words.

## Fairness

Unchanged from 271: the page is not yet equivalent (rich text, 2 boosts and a
few attributes missing), the Rails and Rust numbers are recorded from report
259 rather than rerun in the same session, and placement differs (HTTP.sys on
physical cores versus Docker on WSL2 vCPUs). New: the benchmark sends no
`User-Agent` (above). No speed ratio is claimed.

## Tests

- **Gates:** 24 gates (run `gates-20261014T202456Z`, about 52 min, frozen
  sources at bd274bc): 22 passed first time. Two failed and were fixed in
  d5392eb, then rerun and passed (`gates-rerun-*`):
  - `native-conformance` builds the LLVM suite in **Debug**, where the
    interpreter's depth test overflowed the 1 MB stack: the new stdlib
    primitives and loop bodies had been added inline in `executePrimitive`
    and `executeBlock`, and Debug gives every local a frame slot. Both now
    live in their own functions.
  - `native-value-stack` builds an experiment with warnings as errors (a
    missing `BytesValue` case) and pins the header line
    `AL_OWNING_TYPE_LIST = 10u`, which the Bytes kind had changed; `BYTES` is
    now declared before `LIST` (values are explicit).
  - After the fixes: Flow 1,480, Flow.Runtime 2,586, IR interpreter 161,
    stdlib 334, acceptance 640 assertions pass again.
- **Native room-read verifier:** 252 assertions in both profiles. HTTP suite 473,
  integration 583, Browser 3,842, CampfireViews 15.
- **Room page oracle:** meta and composer tokens valid, 320 of 320 boost
  tokens valid with distinct masks.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 324 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 64 |
| owning-html | pass | 58 |
| owning-listbuild | pass | 30 |
| owning-utf8 | pass | 12 |
| owning-templates | pass | 21 |
| campfire-views | pass | 18 |
| acceptance | pass | 13 |
| flow-tests | pass | 25 |
| flow-runtime-tests | pass | 42 |
| ir-interpreter | pass | 5 |
| campfire | pass | 881 |
| owning-mailbox | pass | 86 |
| ir-parity | pass | 16 |
| owning-lists | pass | 14 |
| owning-list-ops | pass | 46 |
| stdlib | pass | 122 |
| campfire-browser | pass | 127 |
| source | pass | 3 |
| room-read-trusted-off | pass | 340 |
| room-read-diagnostic-off | pass | 365 |
| native-value-stack | fail, fixed, rerun pass | 34 |
| native-conformance | fail, fixed, rerun pass | 344 |

## Next

1. **Native loop and call performance** (docs/PENDING-REVIEW.md): scalar
   `var`s and loop indexes in registers; no per-iteration reclaim for
   scalar-only bodies; trusted inline helpers; `inline fn`; small `Option`
   results in registers when matched immediately. Measure with the
   micro-benchmarks above and the quick bench.
2. **Stdlib step 2:** wire `csrf.flow`, delete host operations 11-13 and the
   C/F# CSRF code, measure the per-page cost (about 320 HMACs).
3. **Stdlib steps 3-5:** cookies and sessions, the db API, the generic HTTP
   host, then delete the .NET Campfire host.
4. **Then** rich text and boosts, and a same-session rerun of all three
   servers.

Research items added: unused test overrides as failures; O0 frame
bookkeeping (large words exceed the 64 KB per-frame screen at O0).

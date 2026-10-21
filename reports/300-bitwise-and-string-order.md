# 300 — Reopened: bitwise operators, shifts and String ordering

AgentLang was reopened on 21 October 2026 to apply lessons from crudspec, a
later experiment. This is the first of the agreed changes, in the user's order:

1. bitwise operators and String ordering (this report);
2. SQL checked against the schema at build time, with typed rows;
3. typed routes, so links and forms are checked;
4. template holes escaped by their context;
5. interpreter-versus-native builtin parity, as a test;
6. C library bindings with declared effects;
7. templates and strings written straight to the response;
8. paired layout templates.

Every item is checked for speed and for build time. The aim is a verified
build under one minute.

## What changed

**Operators.** `&`, `|`, `^`, `<<` and `>>` on Int, plus `int.bit-not(x)`.
Like the other operators, they need a space on each side.

| Level (high to low) | Operators |
| --- | --- |
| 1 | `*` `/` `%` |
| 2 | `+` `-` |
| 3 | `<<` `>>` |
| 4 | `&` |
| 5 | `^` |
| 6 | `\|` |
| 7 | `<` `<=` `>` `>=` |
| 8 | `==` `!=` |
| 9, 10 | `&&`, `\|\|` |

**Decisions, for review:**
- **Precedence.** Bitwise operators bind tighter than comparisons, as in Rust
  and Go, not as in C.
- **Parentheses required.** A bitwise operand of a comparison or equality must
  still be parenthesized. `x & 1 == 1` is `FLOW_BITWISE_PARENS`, so a reader
  used to C is never misled. The formatter keeps these parentheses and drops
  ones that precedence makes redundant.
- **Shifts.** A shift count must be 0 to 63; any other count fails with
  `RUNTIME_SHIFT_RANGE` on both backends. `>>` is arithmetic: it keeps the
  sign. `<<` drops the bits shifted out and has no overflow check, unlike
  `+ - *`.
- **String ordering.** `<` `<=` `>` `>=` now accept two Strings, and
  `string.compare(a, b)` returns -1, 0 or 1. The order is by UTF-8 bytes,
  which is Unicode code-point order and Ruby's `String#<=>`. It is not locale
  collation.
  - The interpreter compares scalar values, not UTF-16 units. Ordinal UTF-16
    order puts U+E000–U+FFFF after the emoji; byte order doesn't.
  - Natively, string ordering is five stdlib dispatcher operations (ids
    46–50), backed by a byte loop in C.

**Campfire.** `campfire.views.crc32` (avatar colours) now uses XOR and shifts.
It had been about 40 lines of division and remainder; it is now 12 lines.

## Checks

- **New suite:** `tests/AgentLang.OwningBitwise.Tests`, 9,625 assertions.
  - Every operator over edge values (0, ±1, `Int64.MinValue`/`MaxValue`,
    32-bit boundaries) and the shift counts -1, 64, 1000 and `MinValue`.
  - The interpreter must equal the reference, and native code must equal the
    interpreter, at O0, O2 and trusted O2.
  - String ordering is checked against byte order, over ASCII, combining marks,
    U+E000, emoji and an embedded NUL.
  - CRC-32 is checked against zlib's algorithm.
  - Parsing: `FLOW_BITWISE_PARENS` in four places; type errors for Strings,
    Floats and Bools; `List<List<Int>>` still parses; the formatter round-trips.
- **Updated:** `AgentLang.Flow.Tests` had asserted that `"a" < "b"` is a type
  error. It now checks String ordering and bitwise precedence (1,497
  assertions).
- **Suites run:** Flow, Flow.Runtime, IR, IR.Interpreter, IR.Formatting,
  Flow.Lint, Llvm, OwningUtf8, OwningTemplates, OwningHtml, Stdlib, FlowHttp,
  CampfireViews and OwningBitwise. All pass.
- **Byte parity.** Base (72af085) and this build were compared over 22
  requests on the same snapshot and port (`bytecmp.sh` in the run root): the
  room page, HEAD, missing room, anonymous, cross-site, POST, the sidebar,
  account, paging, search, 4 avatars, 4 user pages and 3 around-windows. All 22
  are the same.
  - Masked before comparing: the encrypted session cookie (random IV) and
    time-stamped tokens (session token, transfer links and their QR codes,
    cookie expiry).
  - The base build is byte-identical with Rails (report 285), so these pages
    still are.

## Speed

**CRC-32, native (trusted, O2).** 3,000 calls on short strings, the minimum of
15 interleaved runs:

| | Per call |
| --- | ---: |
| Old (arithmetic) | 6,804 ns |
| New (XOR and shifts) | 200 ns |

That is 34× faster, with the same totals. At 0.2 µs it needs no C library.

**Room page.** The room page doesn't run CRC-32, so this is a no-regression
check. The first single runs differed by 9% in throughput but only 1% in CPU
per request; the server got less CPU time in the second run. An ABBA run on the
uncached room page (sweep protocol 285, 4 vCPUs) settles it:

| Lifetime | req/s | CPU ms per request |
| --- | ---: | ---: |
| 1 A (base) | 1,144 | 3.47 |
| 2 B (this) | 1,130 | 3.52 |
| 3 B (this) | 1,163 | 3.42 |
| 4 A (base) | 1,170 | 3.40 |

The difference is about 1%, inside the run-to-run spread, so there is no
change.

**Build time** (Campfire Linux server):

| Build | Before | After |
| --- | ---: | ---: |
| Verified | 43.1 s | 41.2 s |
| Unverified | 20.0 s | not rerun |

The time splits into emitting with the tests run (28 s) and clang O2 on the
35.8 MB `module.ll` (10 s). Running `-TwoPart` in sequence takes about 63 s,
because it runs the unverified and verified builds one after the other.

## Not done

- The legacy arena backend (`LlvmAot.fs`) still rejects the new primitives, as
  it already rejects loops; Campfire doesn't use it.
- `emoji.flow`'s UTF-8 decoder still uses `%` and `*`. It is correct and not
  on a hot path, so it was left alone.
- The new primitive names had to be added to six hand-kept lists (compiler,
  interpreter, owning support set, owning signatures, register-aware set,
  lifetime analysis). Item 5 adds a test that keeps those lists in step.

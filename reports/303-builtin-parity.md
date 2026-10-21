# 303 — Item 4: the interpreter and the native backend agree on the builtins

A recurring detour in the Campfire port: code passed its attached tests in the
interpreter, then failed the native build. The cause was primitives that only
the interpreter implements (report 285: `list.tail`, `list.is-empty?`,
`string.trim`). There was no list of which primitives were native. Instead,
seven hand-kept lists across the compiler, interpreter and native backend
had to agree. Report 300 added its new operators to six of them by hand.

## What changed

- **One public native set.** `OwningStackAot.nativePrimitives` replaces the
  native backend's private list.
- **The exceptions, with reasons.** `Compiler.interpreterOnlyPrimitives` lists
  the primitives that stay interpreter-only, each with why. There are 18:

  | Primitives | Reason |
  | --- | --- |
  | `clock.now`, `console.write`, `file.read`, `file.write`, `file.exists?` | host effects; native hosts provide their own operations |
  | `instant.*` (4), `int.add-checked`, `int.multiply-checked`, `int.scale-ratio-toward-zero`, `string.guid-canonical?`, `string.guid-normalize`, `string.email-address-valid?` | the business-language demo; no native app uses them |
  | `string.to-lower`, `string.to-upper` | Unicode case mapping needs case tables; `string.to-lower-ascii` is native |
  | `list.tail` | a copy of all but the first element; loops with an index, or `list.get`, are native |

- **Newly native:**
  - `int.abs`, `int.min` and `int.max`, inline LLVM. `abs(Int64.MinValue)`
    raises `RUNTIME_OVERFLOW`, as the interpreter does.
  - `list.is-empty?`, read from the list header.
  - `string.trim`, stdlib dispatcher op 52, in C. It removes exactly .NET's
    `Char.IsWhiteSpace` set at both ends (the interpreter calls `String.Trim`):
    U+0009–000D, space, U+0085, U+00A0, U+1680, U+2000–200A, U+2028, U+2029,
    U+202F, U+205F and U+3000.
- **The native check runs before the tests.** A verified server build
  (`FlowApp.loadNativeVerified`, used by `emit`) now:
  1. defines the app;
  2. runs the native backend's validation on the entries, without emitting;
  3. only then commits, which runs every attached test.

  A primitive with no native lowering now fails the build with its name and
  the word that uses it, before the tests (20 s in this build) rather than
  after. The check took 0.4 s.

## The parity test

**New suite:** `tests/AgentLang.NativeParity.Tests`, 950 assertions.

- **The lists agree:**
  - the compiler catalog and the interpreter's table name the same primitives;
  - every native primitive runs in the interpreter;
  - every interpreter primitive is native or on the list with a reason, never
    both;
  - the lifetime analysis names only native primitives.

  Adding a primitive to the interpreter alone now fails this test until it is
  lowered or given a reason.
- **The new lowerings match the interpreter** at O0, O2 and trusted O2:
  - `int.abs`, `int.min` and `int.max` over edge values, `MinValue` included;
  - `list.is-empty?` on empty and non-empty lists;
  - `string.trim` on 16 strings, each against .NET's `String.Trim`. They
    include every white-space class, and characters that look like space but
    aren't (ZWSP U+200B, BOM U+FEFF and U+180E), next to multi-byte text and
    emoji.
- **The early check.** The FlowHttp test app is copied and given a
  `counter.count` that calls `list.tail`, which also makes its own attached
  test fail. The build must stop with "Native check, before the attached
  tests: IR_OWNING_STACK_PRIMITIVE_UNSUPPORTED …list.tail". If the tests ran
  first, the failing test would be the error instead.

## Results

- **Suites:** the 17 suites of report 302, plus NativeParity, all pass.
- **Build:** the verified Campfire build took 40.2 s. That is sql-check 0.2 s,
  define 7.5 s, native check 0.4 s, commit and tests 10.9 s, emit 3.5 s, and
  clang plus staging for the rest.
- **Output:** all 22 sampled responses match the base build. Campfire's code
  is unchanged in this item.
- **Speed:** no request path changed, so one quick bench run checks for
  regressions. Uncached: 1,202 req/s at 3.33 ms CPU per request; the item-3
  runs gave 1,183 and 1,182 req/s at 3.36–3.37 ms. Cached: 5,717 req/s at
  0.679 ms, against 0.684–0.685 ms. No regression.

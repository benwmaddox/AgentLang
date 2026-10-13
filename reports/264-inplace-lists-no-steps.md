# 264 — In-place list growth, free nominal list retag, no native step counter

Status: compiler and runtime milestone (views plan step 2c; response-streaming
order item 1). No application or benchmark change.

## User decisions

- **Remove the step counter** unless there is a really good reason to keep it.
- **In-place `list.append` / `list.concat`** when the list is the newest value
  and nothing else refers to it.
- **Free `List<Html>` → `List<String>` retag.**
- **No unneeded copies of values already on the stack.**
- **Shared literals** wait until after further testing.
- **Output design:** streaming into a response buffer that is flushed at the
  end (the default), with a typed append-only sink as the direction. The
  part-list `Body` type is rejected. Templates are only interpolated literals
  `html"…"` with multi-line strings and compile-time syntax validation. See
  `docs/RESPONSE-STREAMING.md` and `docs/CAMPFIRE-VIEWS-PLAN.md`.

## Native step counter removed

The owning backend called `al_owning_charge_step` before every IR instruction,
and registered a `RUNTIME_STEP_LIMIT` diagnostic for each one. The scalar
backend incremented a context field per instruction.

Since report 260 there is no instruction limit, so the count served only as a
test oracle comparing native and interpreter instruction counts. Output and
diagnostic parity already cover that, so it was not a good enough reason to
keep a call per instruction.

- **Removed** from both native emitters and from the runtime
  (`al_owning_charge_step`).
- **ABI unchanged.** The context field `steps_consumed` stays and is
  documented as reserved and always 0.
- **Tests.**
  - 44 native step comparisons in the LLVM suite now assert that native code
    reports 0 steps, which also proves the counter is gone.
  - The mailbox verifier and its C runner check for 0 native steps.
  - The fixture's step oracle remains as the verified-IR interpreter's
    instruction count.
- **The interpreter keeps its per-instruction hook.** It drives test coverage
  and trace steps, and the interpreter is not a benchmark target.
- **Measured effect** on the byte-dominated 40 × 9.6 KB page: within noise
  (1.25 → 1.26 ms minimum, trusted profile). It matters more for
  instruction-heavy code.

## Copy audit

Calls, `map` and other list callbacks, and local loads already pass descriptors
(descriptor transfer). They copy nothing. Report 263's breakdown was corrected:
the remaining copies all come from operations that build a new value (escape,
join, list building).

## In-place `list.append` / `list.concat`

- **Compile time.** A list is *unaliased* if it was built in the current block
  by a list-building operation and has not been stored to a local. Local loads
  share bytes, so a stored list is never mutated. A list from an outer block is
  never eligible, so a scope rewind can never cut an extended list.
- **Run time,** for an unaliased left list:
  1. **Adjacent.** The appended bytes start at the list's end and finish at the
     arena cursor: only the header count changes. For `concat`, the right
     elements slide down over the right list's 8-byte header (a move, not a
     copy).
  2. **At the top.** The list ends at the cursor: only the appended bytes are
     copied.
  3. **Otherwise** the report-263 path runs: one sized copy.
- **Measured** (`AgentLang.OwningListBuild.Tests`, O0 and O2):
  - **`list.append(list.append([a], b), c)`:** copies exactly a, b and c, with
    no list recopy.
  - **`list.append([a], string.html-escape(b))`:** the escaped value is not
    copied.
  - **`list.concat([a], [b, c])`:** copies a, b and c, and moves exactly b and
    c.
  - **`let first = [a]; [first, list.append(first, b)]`:** returns
    `[["a"], ["a", "b"]]`, so a list bound to a local is never mutated.

## Free nominal list retag

A `map` whose callback only unwraps a nominal scalar (body `UnwrapScalar`,
optionally with a parameter store and load, and no effects) retags the list. The
bytes are the same, because nominal scalars share their base layout.

- `IrInterpreter.retagOnlyCallback` is shared by the interpreter and the owning
  backend, so both skip the callback.
- **Measured:** `values.map(tag.text)` copies 0 bytes and runs no callback
  frames.

## Tests

- `AgentLang.OwningListBuild.Tests`: 146 assertions (was 100).
- LLVM suite: 1,019 assertions.
- The mailbox verifier passes with native-zero step checks.

## Gates

All 18 gates pass (run `gates-20261013T190954Z`).

| Gate | Result | Seconds |
| --- | --- | --- |
| llvm-full-suite | pass | 287 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 7 |
| owning-html | pass | 58 |
| owning-listbuild | pass | 19 |
| acceptance | pass | 7 |
| flow-tests | pass | 3 |
| flow-runtime-tests | pass | 33 |
| ir-interpreter | pass | 3 |
| campfire | pass | 230 |
| native-value-stack | pass | 280 |
| owning-mailbox | pass | 71 |
| ir-parity | pass | 13 |
| native-conformance | pass | 289 |
| owning-lists | pass | 9 |
| owning-list-ops | pass | 16 |
| room-read-trusted-off | pass | 159 |
| room-read-diagnostic-off | pass | 158 |

An earlier run (`gates-20261013T184059Z`) failed only `acceptance`. It was launched from inside `.agentlang`, and the acceptance suite resolves `examples/legacy` relative to its working directory. The gate script now runs from the repository root.

## Also in this commit

- **`agentlang test|check|fmt FILE.flow...`.** A basic CLI file workflow:
  - it uses a fresh in-memory project;
  - diagnostics map back to file lines;
  - the exit codes suit CI.
  
  It is built with the solution and verified by hand; usability follow-ups are
  parked in `docs/PENDING-REVIEW.md`.
- **Plan updates** in `docs/CAMPFIRE-VIEWS-PLAN.md` and `docs/PENDING-REVIEW.md`:
  - UTF-8 strings come next, and `string.length` must be removed by
    2026-11-01;
  - templates validate their syntax and interpolated `Html` must be
    well-formed snippets;
  - general app-declared host capabilities remove Campfire from the
    compiler;
  - a performance check follows every batch.

## Next

1. UTF-8 internal strings:
   - rename `string.length` to `string.byte-length`;
   - add a character-length function;
   - reject unpaired surrogates at the boundary.
2. Templates: the literal-only `Html` constructor, and `html"…"` with
   multi-line strings and compile-time syntax validation.
3. `out.write` streaming into the response buffer.
4. The apples-to-apples Campfire page.

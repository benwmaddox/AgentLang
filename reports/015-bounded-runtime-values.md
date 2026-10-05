# Bounded interpreter runtime values

Status: focused Release validation passed on the working-tree candidate.
Full Release validation and publication of this implementation remain pending.

## Limits and enforcement

The interpreter now bounds values at depth 256, 100,000 expanded value nodes,
and an estimated 8,000,000 UTF-8 output bytes. Expanded-node accounting counts
each repeated reference at every occurrence, so a shared DAG is charged for
the tree it would produce during public conversion and serialization. String
payloads are estimated at six output bytes per UTF-16 code unit to cover JSON
escaping, and record/type names plus per-node structural overhead are included.
The existing 10,000-item collection cap and 10,000-instruction fuel limit
remain unchanged.

The interpreter walks runtime graphs iteratively and memoizes per-node
footprints using weak keys, so analysis neither recurses through attacker-sized
graphs nor retains popped runtime strings for the duration of an execution.
It checks the live stack and locals after each instruction, incrementally
checks `list.map` output as callbacks return, checks string concatenation
before allocation, and rechecks final roots before converting to public
`Value` instances. Type-footprint measurement and type diagnostic formatting
are iterative as well. The existing recursive conversion is reached only
after the depth and expansion bounds have passed; there is no attempt to catch
`StackOverflowException`.

An over-limit value returns `RUNTIME_VALUE_LIMIT` with the source site when an
instruction created the violating value. The checked dimensions are depth,
expanded nodes, and estimated output size. These limits bound interpreter
results and conversion work; they do not promise a complete memory quota for
all intermediate host allocations.

## Regression coverage

The direct interpreter suite includes the supplied `Chain` record shape with
an `Option<Chain>` tail, its 1,200-level constructor sequence, a below-limit
chain whose public value is compared structurally, and a shared binary `Tree`
whose output would expand exponentially. The shared-DAG fixture places a
virtual write after construction and verifies the value-limit diagnostic
occurs before the host effect. A large string fixture exercises the output
byte bound.

The coordinator ran `dotnet run --project
tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj -c
Release`: all 19 assertions passed, including the deep recursive record,
shared-DAG, effect-order and output-size regressions. A subsequent fresh CLI
Release build passed with zero warnings and errors.

The selected cross-binary parity check also passed against the pinned
`host-019d754` reference. Its exact candidate artifact hashes and selected
contracts are recorded in `.agentlang/reports/015-ir-parity.json`. This is
behavioral parity for the verifier's fixtures, not proof of parity for all
programs or a performance comparison. The new over-limit behaviors intentionally
reject values that the previous implementation did not safely bound. Full
Release validation is still pending; subsequent frontend changes require their
own validation against freshly built artifacts.

## Source-parser safety extension

The legacy source parser also had unbounded recursive type and block parsing,
and constructed flat operation lists using non-tail recursion. The pending
extension limits generic type nesting to 256 and expression-block nesting to
64, returning source-located `PARSE_TYPE_NESTING_LIMIT` or
`PARSE_BLOCK_NESTING_LIMIT` diagnostics. Flat operation conversion is iterative;
the parser does not impose an instruction-count cap to hide that recursion.
These are source-parser bounds, separate from execution fuel, call depth, and
runtime value-size bounds.

Focused regressions exercise accepted boundaries, one level beyond each limit,
and a 40,000-operation flat body with preserved push/call order. Patch review
found and corrected F# indentation and an option-branch cursor binding; the
fresh Source run passed all 84 assertions. A fresh CLI Release build completed
with zero warnings/errors, and the coordinator's bounded process probe passed
five checks: clean host exit, three responses, structured rejection of 3,000
type levels and 1,000 block levels, and successful `10 20 add` execution in the
same process after both errors. Evidence is saved at
`.agentlang/reports/015-parser-limits-ecb180ebd02b42e88d87e27091d77fed.json`,
including runtime dependency hashes and host/probe hashes. The probe enforced
64 KiB request and 16 KiB response limits, a 15-second exchange deadline, and
three exchanges. These scoped results do not replace the full milestone gate.

The probe is now a portable permanent verifier,
`scripts/Verify-ParserLimits.ps1`, with explicit CLI/evidence paths and retry
filenames that preserve earlier evidence. Its focused run passed all five
checks against the same pinned CLI artifacts; the runtime and script hashes
are recorded in `.agentlang/reports/015-permanent-parser-probe.json`. The normal
validation script now invokes it after rebuilding the solution, and CI uploads
its evidence. That newly integrated full gate has not yet been run on the
combined frontend/coverage changes.

A second invocation using the same evidence path produced
`015-permanent-parser-probe.retry-01.json` and passed the five checks again.
The coordinator compared the original report's SHA-256 before and after and
confirmed it was unchanged, directly checking the evidence-preservation branch.

## Integrated validation

The subsequent fresh full Release gate passed all 23 required checks, and the
pinned historical CLI comparison passed 314 selected checks. See
[report 021](021-integrated-flow-foundation.md) and its saved evidence. These
results validate the delivered slice; the full Flow migration and controlled
agent evaluation remain incomplete.

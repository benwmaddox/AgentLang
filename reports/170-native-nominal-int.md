# Native nominal integers in the owning value stack

2026-10-10. Engineering checkpoint complete. The serial Release build and
521-assertion LLVM suite, native conformance, mailbox regression and all
37 full local Release checks pass. This adds no new
agent-efficacy observation and does not change the mixed findings in report 151.

## Change

The selected dynamic owning-descriptor backend accepts nominal scalar types
whose base is Int and which have no predicate validator. Meters and OrderId
retain distinct verified nominal keys and native TypeIds while each stores an
eight-byte signed integer. Host input requires the exact NamedValue identity;
a bare Int, another nominal wrapper or a same-name RecordValue is rejected.
Record codecs now require an actual record definition.

Generated wrap/unwrap targets retain their exact frozen identity, revision,
signature and pure effect contract. Execution retags the operand descriptor,
preserving offset, payload, extent and enclosing owner end. It introduces no
payload allocation, copy, move, compaction or new rewind. Local stack-shape
planning and host decoding preserve nominal identity. The historical fixed
emitter and conservative lifetime analysis are unchanged.

Layout ABI/schema 3, stack ABI 1 and module ABI 1 remain unchanged. Physical
Int layout kind and nominal TypeId already coexist in the descriptor contract.
Predicate-bearing Int scalars and String/Bool/Float wrappers remain explicit
unsupported errors, including when nested in an inactive sum alternative.
Nominal typing is not a predicate-validation rule; PositiveId and Email still
need their planned native validator implementation.

## Independent checks and validation

Before application and execution, the source baseline, implementation patch and
oracle were frozen in `.agentlang/nominal-int-170`. Literal signed little-endian
fixtures cover zero, -42 and both Int64 limits. A separate 24-byte OwnerEnvelope
contains an eight-byte Meters field followed by a String. Descriptor-transfer
trace fields must preserve owner end 24 when the extracted value becomes Int;
a one-field record would not distinguish the two extents and was replaced
before execution. The existing nested record/Option/Result fixture stays unchanged.

The first `dotnet run` build attempt exited 1 with only a generic build-failure
message. Its output is retained. An explicit serial Release build then passed
with zero warnings and errors; no cause is inferred from that difference.
The newly added nominal Int section completed at O0/O2 and the fail-fast
LLVM suite advanced to later sections. Those cases check exact host identity,
signed bytes, rejection atomicity, nested values, zero-copy retags and the
24-byte owner-range trace. The complete LLVM suite then passed all 521 assertions against freshly built
artifacts. The full local Release gate also passed. CI remains manual-only.

The independent native runner adds 47 pinned cases per optimization. Its first
fresh gate stopped during the runner build: an unannotated F# record inferred
the wrong context type, and already-unpacked integer type keys were destructured
again. The runner now has an explicit lowering-context return type and direct
integer bindings. That failed build is retained separately; it executed no
nominal native cases. The signed-byte, copy-count, capacity and rejection
oracles were unchanged for that build correction. The second gate executed all
new cases but failed the combined owner-range assertion at both O0 and O2.
Its output bytes, owner end and allocation ordering were correct. The assertion
incorrectly required zero whole-body deep-copy bytes despite creating String
literals `temporary-in-scope` and `after-scope`.

Source inspection of the String codec and `al_owning_copy_constant` establishes
their copy cost independently: 18 and 11 UTF-16 units, plus eight-byte headers,
rounded to eight-byte extents, produce 48 + 32 = 80 bytes. The corrected fixture
requires exactly those 80 bytes, zero moves, and the same owner-range checks;
the separate wrap/unwrap and host-identity cases still require zero copies.
The failed execution remains evidence. The third fresh native gate passed all
41 gate checks with 46 stable source inputs. Its experiment recorded zero
failures and all 94 required nominal cases passed (47 each at O0 and O2).
Both runs preserve owner end 24 and scalar extent eight. The gate also rebuilt
and passed the native storage checks, UBSan trap run and 44 lifetime assertions.

The fresh owning-mailbox regression gate passed all 1,587 checks with 25 stable
source inputs. This rechecks the existing String and sum lifecycle fixtures
against the changed selected emitter; it does not yet establish native nominal
payload support in mailbox state. All 37 full local Release checks passed with
zero failed commands. Its isolated business-policy preflight passed 98 checks
and 30 control runs, retaining the distinction between expected rejection of
wrong implementations and a failed validation command.

The [verified evidence archive](evidence/170-native-nominal-int/archive.json)
records hashes for source, independent fixtures, accepted runs and failed
attempts. It excludes binaries and dependency build trees. Exact validation
commands and outputs are retained with the evidence.

## Limits and next work

This is a native type-support slice, not a throughput comparison or evidence of
better agent edits. It does not implement JIT, general native release support,
new ownership syntax or a final mailbox memory policy. The next strong-type
slice must execute frozen predicate validators and enforce them at the required
construction, external-input and publication boundaries; it must not silently
execute refined values as their base type.

Read-only review found no correctness issue in the changed layout, host codec,
frozen-call or owner-provenance paths. It recorded one minor diagnostic issue:
the shared unsupported-type message still omits unvalidated nominal Int from
its supported-type list. That wording is queued with the next type-support
slice. A focused defensive boundary review also found no concrete regression;
its source-only limit is preserved. The new wrong-host-value tests cover
top-level inputs, while malformed nested host inputs are not directly exercised
by this slice. Recursive encoding uses the same guarded branches. This scoped
review is not a security audit or a production-readiness claim.

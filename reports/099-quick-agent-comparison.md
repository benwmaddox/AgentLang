# Quick agent comparison

Status: completed preliminary comparison. The user requested the bare minimum for a decent comparison
and authorized parallel subagents. Elaborate 004 harness preparation is deferred.

Run one fresh Luna/max external subagent per condition on S07: implement a pure
Customer-to-Money discount operation, with exact ordinal premium classification,
10% discount and truncation toward zero across signed Int64 balances.

Conditions use isolated copies of existing projects:

- Sparse language vocabulary: archived 003 flat project.
- Retained vocabulary: accepted R03 project with its prior premium classifier.
- Reset rich vocabulary: archived 003 rich project without the learned classifier.
- Conventional F#: existing business-policy template and pinned Business DLL.

The three language actors ran independently with the same primer,
contract, host operation set and 100-exchange limit. The conventional actor ran
after a fresh agent slot became available, using normal file edits/local
tests. All actors were prohibited from reading the independent corpus. The
starts, editable projects, host traces and results live separately under
`.agentlang/quick-comparison-001/`; `plan.json` records setup before outcomes.

Use existing independent 54-case behavior checks after actors finish. Keep
behavior, definition preservation, library qualification, observed reuse and
errors separate. Do not infer success from actor-authored tests alone. Preserve
failed output and original study records. No new frozen protocol is needed for
this exploratory comparison.

One task and one actor per condition can reveal usability and failure patterns;
it cannot establish a general retention benefit or statistical superiority.
Conventional F# has no matching instruction-coverage gate. Token usage is not
available, and this interpreter study says nothing about native arena/mailbox
performance.

## Results

All four fresh actors produced correct behavior on all 54 independent cases.
The coordinator also recomputed every language output with BigInteger arithmetic
and checked the unchanged F# typed probe against the same arithmetic contract.

| Condition | Independent cases | Own tests | Own instruction / branch coverage | Runtime exchanges |
| --- | --- | --- | --- | --- |
| Sparse vocabulary | 54/54 | 8/8 | 37/37; 2/2 | 33 |
| Retained vocabulary | 54/54 | 6/6 | 33/33; 2/2 | 58 |
| Reset rich vocabulary | 54/54 | 6/6 | 35/35; 2/2 | 43 |
| Conventional F# | 54/54 | 10/10, plus 7 seed checks and example | Not measured | Not comparable |

All three language outputs are persistent, pure library definitions with the
exact Customer-to-Money signature. Fresh-process test execution and complete
own coverage passed. Starting authored words and nominal types were preserved:
sparse 0 words/6 types, retained 54 words/31 types, reset 53 words/31 types.
The conventional actor changed only Operations.fs and SelfTests.fs; the other
two public operation stubs, Domain.fs, Program.fs, project file and Business DLL
were preserved.

The retained actor discovered `customer.premium?` through describe/source and
called it from the new definition. That is observed reuse of an earlier
agent-created abstraction. The other actors implemented the raw comparison
directly. All solutions used quotient/remainder arithmetic to avoid overflowing
when calculating 90% at signed Int64 endpoints.

## Errors and interpretation

Every language actor initially supplied a noncanonical Instant fixture, so its
first tests failed before reaching the discount operation. The retained actor
recovered with one corrected definition. Sparse and reset actors also corrected
incorrect endpoint expectations. The sparse actor encountered source-parenthesis
and JavaScript integer-precision problems while constructing requests; reset
made an incorrect constructor-name query and two malformed eval requests.
The conventional build caught a missing `open System` in its test file and passed
after that was corrected. These recoveries are part of the observed result.

The language sessions lasted approximately 817 seconds (sparse), 736 seconds
(retained) and 343 seconds (reset), including thinking, tool latency and shared
machine scheduling. They are descriptive session times, not isolated runtime
performance or a matched end-to-end latency comparison. Retained used more
runtime exchanges than either control in this run; there is no observed call
reduction. Protocol success also does not mean tests passed: a test command can
return successfully with failing test results.

The existing retention verifier reports metadataPassed=false for all three:
the quick run deliberately lacks formal frozen starting-state metadata, and the
actors omitted attached examples. Behavioral acceptance, persistent library
qualification, tests, coverage and preservation passed independently of those
missing metadata requirements. Do not describe these as full original-study
acceptance passes.

The conventional verifier stopped before its behavior probe because the actor
did not add per-function source documentation. Its failure record is preserved.
The coordinator then ran the unchanged Program.fs `--probe S07` directly over
all 54 oracle inputs, without editing the actor output, and independently
compared every typed Money result. That separate behavioral result passed.

This establishes that the current interpreter supports discovering, composing,
testing and committing a correct typed abstraction, including actual retained
helper reuse. F# was equally correct on this task. It does not show that retained
vocabulary improves correctness or efficiency; one small task has a ceiling
effect when every condition succeeds. The repeated fixture friction points to
better constructor examples/help as a useful improvement. The next development
step is the approved Flow/2 syntax, rather than more comparison infrastructure.

## Evidence and validation

Exact setup, host traces, verifier outputs, independent recomputations and source
snapshots are archived under `reports/evidence/099-quick-comparison/`, with a
SHA-256 inventory. Original 003 outcomes and unvalidated 004 drafts are unchanged.
All language host traces record host-close and host/runtime exit code 0.

Local validation used existing Verify-RetentionTrial.ps1 in non-frozen mode
with each isolated project and immutable starting copy; all report
behaviorPassed=true and 54 recorded/executed cases. Conventional validation used
`dotnet build BusinessPolicy.fsproj --configuration Release --nologo` and the
local DLL self-tests, followed by the unchanged typed probe. No shared runtime
build, CI run or additional experiment harness was introduced.

# 178 — Tested preview defect: control calibration

Status: calibrated locally, 2026-10-10. No experimental participants dispatched;
no production runtime source changed. This prepares the defect-repair stage
recommended by [checkpoint 177](177-reliability-and-vocabulary-checkpoint.md).

The controls demonstrate a useful distinction: library qualification rejects a
new untested branch, but accepts that same faulty branch when a test explicitly
asserts its incorrect behavior. Independent acceptance rejects it. This is a
coordinator-introduced fault and mistaken expectation, not an observed agent
mistake or a comparative reliability result.

## Fault and results

`subscription.preview-replacement` must validate cancelling the old subscription,
then validate replacement creation, forwarding the original errors and returning
the complete original Store on success. The introduced shortcut instead returns
success immediately when replacement expiry equals handoff time. It accepts a
zero-duration replacement and bypasses earlier cancellation/creation errors.

| Control | Independent cases executed | Passed | Behavioral mismatches |
| --- | --- | --- | --- |
| Correct reset-rich language | 37 | 37 | 0 |
| Correct retained language | 37 | 37 | 0 |
| Correct F# | 37 | 37 | 0 |
| Faulty reset-rich language | 37 | 29 | 8 |
| Faulty retained language | 37 | 29 | 8 |
| Faulty F# | 37 | 29 | 8 |

Every case executes without setup errors. All three faulty controls fail the
same eight cases: four existing zero-period cases and four added combinations
with missing, cancelled or not-yet-started old subscriptions, or duplicate ID
plus blank term. The correct controls preserve the complete input and return
original Store on success. Thirty fixtures have valid references; seven existing
orphan-reference robustness fixtures are classified separately. Seven failures
use valid references; one is an orphan-reference robustness case. The 33 historical
fixtures are reused, not a new unseen corpus.

Both language projects initially reload on the fresh CLI with all 141/145 saved
tests passing. With only inherited tests, both faulty replacements are rejected
by `LIBRARY_COVERAGE_INCOMPLETE`. The runtime preserves existing library status;
`library:false` cannot downgrade it to bypass the gate. After the deliberately
mistaken `zero-period-no-op` test is attached, complete suites pass 142/146 and
library replacements are accepted. Their original WordIds and inherited test
hashes remain intact; both accepted targets are revision 3. The independent
37-case checks then reject their behavior.

The conventional control adds a disclosed coordinator-authored preview wrapper
to report 166's accepted handoff project. Its faulty seed also has the wrong
zero-period expectation. Shared original tests and new non-equal preview tests
pass. A separate correct zero-period assertion rejects the swapped faulty code;
the restored correct code passes after a fresh non-incremental rebuild. Original
Operations and SelfTests prefixes remain byte-for-byte intact.

## Validation and review

The current CLI is rebuilt from source in an isolated artifacts directory:

```powershell
dotnet build src/AgentLang.Cli/AgentLang.Cli.fsproj -c Release --artifacts-path .agentlang/efficacy-maintenance-178/build -m:1 -p:NuGetAudit=false
```

Build succeeds with zero warnings/errors. The F# Business foundation is freshly
built from the seed's Reference source; correct/faulty projects receive the same
DLL hash. Each independent F# scorer also builds a fresh disposable project.
All six final scores pin the same final scorer source and frozen case file.
No broader runtime gate is rerun: product source is unchanged, and report 176
remains the latest complete local Release gate. Package audit is disabled in
these correctness builds; no dependency-security claim follows.

Independent review found that the first scorer checked historical expected
outputs without comparing their fixture inputs to the model. The final scorer
checks complete input rows, output IDs/order, case counts, setup failures and
source preservation. Preview success explicitly uses the original-Store
projection, rather than the underlying handoff's updated state. Correct and
faulty controls are rechecked with the final pinned scorer.

Earlier attempts remain evidence: the F# swap initially hit an incremental-build
timestamp skip, then a source-prefix check exposed newline normalization; fresh
rebuilds and byte-exact restoration resolve both. Language preparation initially
misread source newline hashes/revision advancement, and a post-write summary
printer exited with a KeyError after accepted controls completed. Correction
notes reconcile these reporting mistakes with raw protocol outcomes. Neither
setup failures nor printer failures count as behavioral rejections.

## Next work and limits

The [study preparation](../experiments/AgentLang.SubagentTrials/preview-repair-178/README.md)
defines retained/reset-rich whole-project packages and the conventional workflow
reference. Before dispatch, freeze the follow-up maintenance task, prompts,
preservation exceptions and broker smoke results. The repair task may correct
the one coordinator-injected wrong expectation; every unrelated test/example
must remain intact. Do not count calibration agents as experimental participants.

The oracle checks error codes and complete projected state/input; exact error
messages are not exhaustively checked. Canonical bounded time fixtures do not
establish general date/time correctness. Library qualification, preservation,
behavior and recovery remain separate outcomes. No token/context or comparative
agent efficacy result is established here.

Effects remain pure/virtual for these controls, with no email/network operations
or new host capabilities. The [archive index](evidence/178-preview-defect-control-calibration/archive.json)
pins source, requests/responses, positive and negative results, failed attempts
and correction notes. Historical studies and unrelated checkout files remain
unchanged.

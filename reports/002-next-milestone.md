# Milestone 002: typed containers and experiment foundations

Date: 2026-10-04
Status: validated milestone. Validation was performed on the working tree before commit.

## Scope

Typed List/Option/Result values and cases, static list callbacks, library coverage for supported control flow, an offline-testable model harness, and a conventional strongly typed business fixture. Validation automation will retain machine-readable build/test evidence. The full research comparison remains pending.

## Checkpoints and review findings

- Initial independent business checkpoint: `dotnet run --project tests/AgentLang.Business.Tests` failed compilation with FS0046 at Business.fs line 500 because `process` is reserved by F#. No tests ran at this checkpoint; correction was requested.
- The business library then built with zero warnings/errors. The next independent acceptance build caught FS0001 errors, including `Subscription.status` inferred as an Invoice accessor because of shared F# record labels. Explicit nominal parameter annotations for every public accessor and external accessor checks were requested. The library-only build had not proved the intended public signatures.
- Parser review found record field parsing did not verify the `field` prefix before slicing its text. A structured malformed-field error and negative regression checks were requested.
- Business-fixture review found missing raw tier/term/expiry data needed for the five-task pilot. These raw attributes were requested without adding the premium-discount or renewal rule implementations.
- Typed container syntax must be discoverable as syntax metadata, rather than counted as phantom executable dictionary words.
- Harness review identified two task-integrity requirements: tool operation whitelists must be enforced by the decoder as well as the provider schema, and the independent oracle must run before the host completes the task transaction. Failed oracle checks must not leave retained changes in Growing mode.
- Independent review found user definitions could shadow generated record constructors in the reverse declaration order. Both user/generated and generated/generated namespace collisions must be rejected before dictionary construction, including on reload.
- A fresh expanded language checkpoint passed 17 groups but failed the syntax-source metadata assertion (`source distinguishes syntax from executable dictionary words`). This was reported for correction; a later complete pass is required before the milestone is accepted.

## Validation evidence

Business checkpoint independently reproduced: `dotnet run --project tests/AgentLang.Business.Tests` passed **7 groups and 113 assertions**. Groups cover nominal IDs/Email policy, checked minor-unit arithmetic/price invariants, immutable store/lookup, subscription lifecycle, invoice totals/validation, payment provider/receipt boundaries, and email outbox/provider isolation. The test project externally compiles the nominal accessors.

Independent CLI checkpoint passed 13 response/status assertions plus exact type/error assertions: empty List<Int>, absent Option<Int>, and Result<Int,String> error values retained their full types; mixing speed units inside a list returned TYPE_STACK_MISMATCH; an empty iteration with an fs.write callback returned CAPABILITY_DENIED. An Option library commit with only its some test returned LIBRARY_COVERAGE_INCOMPLETE, and adding its none test allowed commit. Both Result cases allowed library commit. Fresh processes reloaded the committed Option and Result words and returned 0 and "42". A piped human REPL defined a temporary word containing a match block, described it as temporary, and evaluated both cases correctly. Raw outputs are saved locally in `.agentlang/reports/container-probes.jsonl` and `container-repl.txt`.

Temporary-word absence in a fresh process also passed. The first verification probe mistakenly parsed the one-shot command's human output as JSON; the corrected probe used JSON-lines mode and asserted NAME_UNKNOWN_WORD.

Ten further CLI response assertions passed. Nested record fields retained `List<Option<Result<Int, String>>>`; filter library promotion failed until an empty-list case was added, then reported 2/2 instructions and 4/4 outcomes covered. A repeated concatenation reaching 16,384 elements returned `RUNTIME_VALUE_LIMIT` against the 10,000-element cap. Per-list limits do not imply complete aggregate or string memory accounting.

The first independent harness checkpoint passed 71 assertions using scripted responses and canned HTTP. Additional accounting and initialization-failure regressions were still being added at this checkpoint; this was not a live model run.

An independent harness CLI smoke run completed with provider `scripted`, 3 requests/turns and 2 runtime calls. All three hidden checks passed: premium balance 90, regular balance 100, and the four seeded tests. The report recorded 16,575 sent/prepared request bytes, peak request 6,396 bytes, and unknown model usage as JSON null. These byte counts include the full wire requests; the separately labeled 4,144-token estimate is not actual tokenizer or provider usage. The smoke reuses seeded code and does not demonstrate AI task implementation. Raw artifacts are saved under `.agentlang/runs/customer-discount-smoke-20261005T001204332Z-3b53ff54/`.

Final independent validation command: `./scripts/Validate.ps1 -ReportPath .agentlang/reports/milestone-002-validation.json`. The fresh Release solution build passed with **0 warnings and 0 errors**. Language acceptance passed **18 groups / 256 assertions**, harness acceptance passed **82 assertions**, and business acceptance passed **7 groups / 113 assertions**. Working and staged whitespace checks passed. The saved JSON identifies the baseline HEAD `d29e085`, branch `prototype`, and `dirty: true`, correctly recording that the next milestone working tree was tested before commit. CI runs the same checks on the pushed revision.

The final language suite corrected the syntax expectation and added whole-expression preflight checks: a wrong constructor payload or denied callback effect cannot execute an earlier allowed file write. Namespace checks reject generated/generated, generated/user, primitive and syntax collisions, including project reload. The runtime contains **49 trusted dictionary primitives**; constructor/match/iteration syntax is separately described and counted.

Harness checks cover preserved reasoning/function history, strict tool decoding, the compact language primer, temporary word cleanup, library coverage rejection and retry, rollback after interim word commits, Flat/Growing retention, initialization failure reports, prepared-versus-sent bytes, unknown usage, incomplete responses, output caps, and credential omission from HTTP failure artifacts. No live API calls were made. Business fixture equivalence with the language domain and live agent comparisons remain unverified.

## Feedback and remaining limits

Typed empty containers and explicit case branches make missing states visible to both the compiler and the library coverage gate. Static callbacks also preserve the capability boundary on empty inputs. These are useful correctness properties; their effect on agent effort has not been measured.

The runtime contains no AI agents or model calls. The optional external harness uses the same public interface as human and scripted clients. Its hidden oracles are separate from agent-supplied tests, and failing tasks must restore retained vocabulary.

Structured error expectations in language tests, aggregate allocation budgets, durable historical source, stable IDs, snapshots, vocabulary maintenance, complete event/quality metrics, confined real effect providers, language/business parity, conventional repository tools, the 60-task suite, and controlled live comparisons remain pending. The runtime still interprets a checked tree rather than lowered bytecode. Library coverage does not prove every input or domain invariant. No research efficiency improvement is claimed.

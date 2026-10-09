# 154 — Testing mocked I/O state with the existing language

Status: fresh external-agent submission and independent behavior/mutation scoring passed.

One fresh agent used existing language features to repair an I/O function and
add tests that reject three frozen defects. This supports bounded usability of
known-path readback plus exact effect counts. It does not establish a reliability
advantage over F# or make library coverage a correctness proof.

Flow/2 already supports assertions about the final contents of known virtual
files. A test can call its target, read the relevant paths afterward, and compare
those observations with a literal or pure expected value. No new syntax, test
AST or runtime API is needed. The target's exact effect counts exclude setup and
post-call inspection. These capabilities are complementary: correct call counts
do not establish correct contents, and correct final contents do not establish
that no redundant write occurred.

## Preflight evidence

One tiny `configuration.publish(source: String, destination: String) -> Unit`
operation reads the source once and writes its exact contents to the destination
once. The authored control test seeds source, destination and sentinel paths,
calls the target, then reads all three paths and compares their contents. It
also requires exactly one target read and one target write.

| Implementation | Final-state assertion | Exact effect assertion | Replacement |
| --- | --- | --- | --- |
| Correct | Pass | Pass: 1 read, 1 write | Library commit accepted |
| Wrong destination | Fail: requested destination remains stale | Pass: 1 read, 1 write | Rejected |
| Wrong contents | Fail: destination has a corrupt suffix | Pass: 1 read, 1 write | Rejected |
| Redundant same-value write | Pass | Fail: 1 read, 2 writes | Rejected |

Each mutant used a separate project with a passing committed baseline. All
three replacements returned `COMMIT_TESTS_FAILED`, and their durable manifest
hashes remained unchanged. The wrong-state cases reported
`TEST_ASSERTION_FAILED`; the redundant write reported
`TEST_EFFECT_ASSERTION_FAILED`. Coordinator-authored controls demonstrate the
mechanism, not independent agent adoption.

The frozen control inputs, raw JSONL requests/responses, literal expectations,
hash manifest and result summary are under
`.agentlang/provider-state-154/workspaceTMP/preflight-20261009-001/`.
They use the copied Release CLI with SHA-256
`3ee742e576c2e6d499f7ebaa968878217f5627218c66bb0ad6b27ec0c589909c`.
No build or product source change was made for this preflight. Earlier attempts
and command failures remain separate from the final passing controls.

The participant received a byte-identical copy of the seven-file weak seed and
the verified 22-file runtime snapshot. Three additional independent cases cover
plain text, empty contents, and Unicode with a newline; the correct control passes
all three and each mutant fails all three for its intended reason. The weak
Unit-only seed test passes even for those mutants. JSON-level ASCII escaping
avoided a Windows stdin encoding issue during preparation; decoded results were
checked for U+96EA and LF, with no question-mark substitution.

One auxiliary frozen metadata field needs a qualification: the wrong-contents
mutant's listed Unicode destination uses literal PowerShell backtick-n, whereas
the main oracle case and its `expectedFinal` contain LF. The frozen file is
preserved. Scoring uses the main case expectations and actual decoded responses;
the auxiliary mutant-summary string is not an acceptance oracle.

The fresh `gpt-6-luna` / max subagent has no inherited conversation context and
uses the existing broker with version-2 help. Its exact prompt and input hashes
are recorded in `.agentlang/provider-state-154/participant-inputs.json` (prompt
SHA-256 `3d5f2d600605f8cebf36c64dd1b99e4e81d051973ef2b2eea8e67677c5b4cc04`).
The frozen preflight/input-package manifest matches the hash recorded in the
participant inputs:
`f3a8b035b6eaf7cca3635f08fa78f3b62a58881eaa0fcf400079ffef141c3da2`.
Its `agentDispatched: false` describes preparation. The subsequent participant
dispatch and completion are recorded separately in `participant-dispatch.json`
and the broker trace, with the initial dispatch record retained.

## Observed participant behavior

The participant inspected the existing function and version-2 help, repaired the
write, added function documentation, retained the existing Unit-result test,
and added two tests. Both new tests inspect destination, source and an unrelated
file after the call, and require one target read and one target write. Their
cases use empty contents and text with line breaks and a Unicode character.
The actor submitted one definition, passed all three attached tests and
published library revision 2, then committed its task.

The independent oracle copy passes all three hidden cases and retains all three
participant cases (6/6 together). Source queries before and after attaching the
oracle tests return the same function body; this check evaluates the submission,
not a coordinator repair. The function has six covered instructions, no branches,
and only the Unit return alternative. Full coverage is consequently a weak
behavioral guarantee here: the incorrect seed also qualified as a library word.

The broker records 24 exchanges: 23 forwarded requests and one invalid-UTF-8
request rejected by the host. Four exchanges requested help. Three exchanges
failed: `EFFECT_FILE_NOT_FOUND` during exploratory eval, `TRIAL_INVALID_UTF8`
at the transport boundary, and `FLOW_TRAILING_INPUT` when eval received more
than one expression. There were no failed definition or publication requests.
The agent's final self-report mentioned only two exploratory failures; these
counts come from the raw trace instead.

Dispatch to broker close took 387.62 seconds. Request payloads totaled 2,909
bytes and response payloads 36,837 bytes, including the rejected request and
host response. These are protocol payload bytes, not model tokens, model turns,
or the complete context supplied to the agent. Authoritative token usage is
unavailable. The terminal broker record confirms host and runtime exit code 0;
the participant itself could not recover the already-closed process handle.

The participant's assertions concatenate observations without separating their
boundaries. That distinguishes the frozen defects in the independent scoring
below, but can conflate different triples of file contents.
It must not be described as exact equality of structured provider state.

## Independent scoring

| Candidate on an isolated copy | Participant tests | Independent oracle | Library replacement |
| --- | --- | --- | --- |
| Submitted repair | 3/3 pass | 3/3 pass | Published revision 2 |
| Wrong destination | Original Unit test passes; both new value checks fail | Not attached to this copy | Rejected |
| Wrong contents | Original Unit test passes; both new value checks fail | Not attached to this copy | Rejected |
| Redundant same-value write | Original Unit test passes; both new count checks fail | Not attached to this copy | Rejected |

The wrong-state mutants retain matching read/write counts of 1/1 and fail
`TEST_ASSERTION_FAILED`. The redundant-write mutant retains matching final
values, but its 1/2 counts fail `TEST_EFFECT_ASSERTION_FAILED`. All three
replacement attempts return `COMMIT_TESTS_FAILED` and retain durable manifest
`3dc33e6e2deab31580fc7cb8fb84467b1fa94dcb3ade8d1c432b157aa031d50e`.
Each mutant preserves exactly the participant's three tests, with the same
test-source hash. No coordinator oracle test is credited as participant adoption.

Decoded participant values contain actual U+03A9, CR and LF; the input has
length 15, rather than literal backslash escape text. The independent Unicode
case contains actual U+00E9, U+96EA and LF. The submitted project is unchanged
by scoring, as checked against complete before/after file inventories.

Two early scoring launches reached EOF before receiving requests. Those empty
sessions and preparation/analysis errors remain in the evidence. The successful
scoring runs used redirected JSONL input, separate copies and the frozen CLI;
there was no participant restart or subsequent coaching.

## Scoring method and limits

One fresh external subagent received a weakly tested, incorrect publisher and its
public behavior contract through the existing version-2 broker interface.
Independent literal acceptance cases and mutation controls were hidden from the
participant and frozen before dispatch. Behavior, authored test adoption,
mutation rejection, persistence and collateral changes were checked separately.
Word-only mutants ran on disposable copies retaining the participant's own tests;
coordinator-added acceptance tests are not credited as agent adoption.

Known-path readback does not expose or assert equality of the whole provider
map. The expected expression must be pure; observations belong in the actual
test body. This single probe does not establish a comparative reliability
advantage over F#, a retention benefit, or production filesystem correctness.

## Product implication and validation

Keep this capability in the compact core through ordinary provider operations
and existing test assertions. The new documentation makes the readback pattern
explicit. Improve examples toward independently identifiable observations rather
than treating concatenated strings as a general state representation. Library
coverage remains useful, but effectful APIs also need assertions about their
observable contracts. Passing counts alone or a Unit return is insufficient.

No compiler/runtime source changed for this milestone. Local validation consists
of the frozen preflight, independent submission and mutation checks, decoded
Unicode checks, unchanged actor inventory, independent evidence review and
`git diff --check`. The full 37-check Release run from report 153 is the runtime
baseline; it was not repeated for these documentation and research artifacts.
Preparation failures and rejected mutations are retained rather than rewritten
as an uninterrupted successful run.

The [evidence archive](evidence/154-provider-state-assertion-probe/evidence.zip)
contains frozen inputs, the participant prompt and final response, raw protocol
trace, durable submission, isolated scoring copies, review, and preparation
attempts. [Archive metadata](evidence/154-provider-state-assertion-probe/archive.json)
records its SHA-256; each archived entry is independently hashed and verified.
Runtime binaries are excluded; their frozen source revision and file hashes
are retained.

# Current-syntax stateful reminder comparison

Both productive agents completed the stateful task and passed their independent
acceptance suites. This demonstrates a workable dictionary-based workflow with
current syntax; it does not establish a reliability advantage over F#.

## Results

| Observation | Flow/2 dictionary | Conventional F# |
| --- | --- | --- |
| Independent verifier | 126/126 checks | 34/34 checks |
| Actor tests | 4 own + 2 seed, passing | 4 own + 2 seed, passing |
| Runnable example | 1 passing | 1 passing |
| Own library coverage | 20/20 instructions, 4/4 branch outcomes | No corresponding gate |
| Broker exchanges, excluding close | 22 | 8 |
| Final host/runtime exit | 0/0, host.close | 0/0, host.close |

These are heterogeneous verifier checks, including metadata and protocol checks,
not 126 versus 34 distinct business scenarios or an equal denominator. Both
independent suites checked first write, repeat behavior, exact existing contents,
separate IDs, non-open statuses, capability denial, snapshot restoration, nominal
input rejection and preservation of the seed and actor inputs. Tests ran locally
on disposable copies. Both actors left the supplied path helper intact and used
it in the new operation.

The Flow actor created one persistent library function with four attached tests
and one example, using fn, properties and typed ==. It recovered from
DISCOVERY_INVALID_ARGUMENT (search-type omitted its required type field) and
FLOW_SYNTAX_VERSION (a definition initially used the seed's Flow/1 word keyword).
No attached test failed. The accepted [Flow/2 source](evidence/111-current-syntax-stateful-comparison/results/flow-submitted.agent)
includes the function, all four tests and its example. The F# actor changed only Operations.fs and SelfTests.fs,
preserving the seed-test region and entry point. One HASH_INVALID edit request
was rejected before mutation; the corrected edit and validation succeeded.

## Coverage finding and scripted control

The language actor's attached tests asserted returned strings. They covered all
branches, but did not assert provider counts or the resulting stored state. The
F# actor's four tests additionally asserted read/write counters, repeat behavior,
marker contents and preservation of an unrelated entry. Independent acceptance
verified state/effects for both final implementations; both passed.

After scoring, I created a separate copy of the Flow result and changed only the
existing-marker branch to read the contents, write the same contents back, and
return them. The actor's original project, tests and independent oracle were not
modified. This is a scripted negative control, not another agent outcome.

The wrong implementation still passed all four attached tests and the library
replacement gate: 25/25 own instructions and 4/4 branch outcomes, persistent
library revision 2. The unchanged independent verifier rejected it at the repeat
case: two reads and one write, where the contract requires two reads and zero
writes. Thus complete execution coverage does not enforce the observable IO
contract even when return values and final contents look correct.

This supports the approved state/effect-property work. The next testing
improvement should make such assertions straightforward in authored library
tests and include this mutation as a negative control. More coverage percentages
alone would not reject it. No claim is made that F# tests always have stronger
assertions; this is the observed output of these two actors.

## Question and design

Can a fresh external agent discover a supplied domain helper, add a tested
stateful operation, preserve existing behavior and avoid unwanted IO? Compare
Flow/2 dictionary development with conventional F# and an explicit virtual-file
provider. The task is invoice reminder queuing: exact raw status matching,
read existing contents without overwriting, write once when absent, and make no
provider calls for non-open invoices. Repeated calls and unrelated state matter.

Both conditions start with nominal invoice types and an implemented reminder-path
helper. Two fresh Luna/max actors receive the public contract and only their own
broker interface, with at most 100 exchanges. They cannot use repository files
outside the broker by instruction; this is not OS sandbox isolation. The Flow
condition uses fn, record properties and typed equality for new source, while
preserving the existing Flow/1 helper/types. Conventional edits are restricted to
Operations.fs and SelfTests.fs, with seeded tests and entry-point wiring preserved.

Starting projects and independent acceptance logic come from historical 071/072.
Local verifier adaptations change paths; the Flow verifier additionally checks
persisted Flow/2 source metadata. No behavior oracle was changed for this run.
Public instructions disclose effect counts, dependency requirements, documentation,
attached tests/example/library coverage, and conventional test-output markers.

The Flow runtime is pinned to clean revision
91fe813984c4fb4f5834bc4c61ba2bbb81ff8ec2. Dispatch took place after report 110, but
this isolated runtime predates its arithmetic addition. The experiment evaluates
existing stateful Flow/2 behavior, not new ratio functions, enums, dictionary
IO overrides or native memory/runtime behavior. The conventional broker and all
45 runtime files were verified against their saved pins before dispatch.

## Infrastructure correction

The initial Flow actor never reached the project. Its advertised help request
was denied by an incomplete allowlist; the next words request observed the child
exit. My launch configuration combined JSONL with interactive frontend/version
flags, which the CLI rejects. Trace records host exit 3 and runtime exit 2; the
actor observed outer process exit 1. This is a setup failure, not a language-task
failure. The starting project was verified byte-for-byte unchanged.

The corrected launch removes those flags (authoring requests already specify
frontend and syntaxVersion) and permits help as advertised. A disposable-copy
preflight served help and words, then closed with host/runtime exit 0. Its first
wrapper attempt also used nonexistent array-JSON parameters; that setup error is
retained. A new fresh Flow actor receives only the corrected prompt. No code or
task solution from the failed launch was reused.

## Scope of inference

The oracles overlap but differ in depth and representation. Conventional checks
include empty existing contents, unrelated state and separately denied read/write
permissions; the historical Flow oracle checks other nominal/runtime metadata
boundaries. Check totals are not a common success denominator. Both public
contracts require exact contents and capability preflight, but passing one oracle
does not establish every additional condition checked by the other.

Both prompts explicitly require the supplied helper. Reuse here is successful
required composition, not evidence that an agent independently chose an abstraction.

One task and one productive actor per condition cannot establish superiority,
vocabulary-retention benefit or large-project scaling. There is no controlled
LLM-token or latency comparison. Broker exchanges are observable interface actions,
not a complete count of reasoning steps or tools. Agent-written tests alone do
not determine success: final changes, preserved inputs, independent state/effect
checks and process lifecycle will be reported separately.

## Evidence and validation

The [indexed evidence](evidence/111-current-syntax-stateful-comparison/index.json)
contains both acceptance reports, trace summaries, public prompts, runtime and
verifier pins, the reviewed submitted Flow source, conventional source files,
independent review and the scripted control. A raw archive preserves starting
and final project files, full traces, verifier scripts and preparation records;
build outputs are excluded. SHA-256 inventories verify the archive entries and
the readable evidence. The failed initial launch and its correction are retained.

Local acceptance commands use the trial's pinned CLI and supplied starting
projects with 071-verify-stateful-trial.ps1 and
072-verify-conventional-stateful.ps1. Both exit 0 on the completed actor outputs.
The same Flow verifier exits 1 on the extra-write control for the expected effect
mismatch. No current enum implementation or current working-tree binary was used
for scoring. No automatic CI or model-token estimate is involved.
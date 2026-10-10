# 175 — FIFO email batching and preservation

Status: completed local agent comparison, 2026-10-10. Both implementations pass
the independent behavior contract. Exact collateral preservation does not pass
in the language arm. This pair supports feasibility and vocabulary reuse, with
no comparative reliability advantage established.

## Question and method

Can an external coding agent discover retained typed vocabulary and compose a
multi-step operation that preserves successful progress, stops on failure, and
retains prior behavior and evidence?

One fresh GPT-6 Luna/max participant per arm receives the same public task and
separate project copy, without helper names or hidden acceptance cases. The
language broker exposes dictionary inspection and definition operations; the
F# broker exposes file inspection, search, guarded patches and validation. Runs
are concurrent under shared load. Tool differences, response limits, unequal
library publication requirements and the small sample prevent causal claims
about reliability, context compression or latency. Agents remain external
developers; none is part of the language runtime.

The task consumes a FIFO queue and a supplied list of synthetic provider
outcomes. Successful items move to sent history. The first failure retains its
item and remaining queue while keeping the successful prefix. Queue or outcome
exhaustion succeeds without inventing a missing-message error. All seven Store
collections, prior sent messages and the input value must be preserved as
specified. No real email or network operation is used.

## Input lineage and oracle

The language seed has 62 authored functions, 37 types, 191 tests and 44 examples.
The retained project's old persistence identity fails current CLI loading;
preparation rehydrates it through the normal protocol rather than weakening
storage guards. All 334 authored body/test/example hashes match the original
seed, but all 62 IDs are fresh and revision/history metadata is reset. This is a
disclosed lineage difference. Most inherited source remains Flow/1; newly
submitted source is Flow/2, introducing conversion work absent from the F# arm.
The F# seed retains five exact source files and passes 18 groups/157 assertions.

Inputs, prompts, runtime and controls are frozen before dispatch. All 392 input
hash pins remain unchanged after scoring. Six cases cover complete success,
first failure, success then failure, outcome exhaustion, queue exhaustion and
empty input. Sentinel entities and two previously sent messages exercise full
state preservation. Correct controls pass; four executing mutants in each arm
are rejected: dropping the failed item, continuing after failure, rolling back
the successful prefix and reversing sent order.

The language's input check combines retained-input equality with the expected
delivery count; a separate check also covers the count. A false composite alone
would not prove input mutation. Control result views with normalized labels are
explicit derivatives of retained raw results with identical request expressions.
Earlier preparation and calibration failures remain in the evidence archive.

## Results

| Observation | AgentLang | Conventional F# |
| --- | --- | --- |
| Independent cases | 6/6 passed | 6/6 passed |
| Independent checks | 60/60 | 78/78 |
| Final saved self-tests | 200 tests | 18 groups/182 assertions |
| Broker exchanges | 75 | 86 |
| Rejected responses | 15 | 11 |
| Request payload bytes, UTF-8 | 91,464 | 54,129 |
| Response text bytes, UTF-8 | 162,705 | 845,976 |
| Concurrent broker session seconds | 1,631.0 | 1,633.8 |
| Broker/runtime exit | 0/0 | 0/0 |

Check and self-test counts use different units and are not comparative quality
scores. Protocol bytes are not LLM tokens or context consumption. LLM token and
agent-turn counts are unavailable. The language uses 12.8% fewer exchanges here,
but more request bytes and more rejected operations. One concurrent pair cannot
establish cheaper development, faster completion or higher correctness.

The language agent uses 24 describe requests and eight source requests. Its
batch and step functions qualify as library functions, with five and four new
tests respectively. Their entire user-function dependency closure is library
qualified. The batch reaches the existing single-message operation through a
step helper, preserving that operation's stable ID within the trial dictionary.
The F# implementation directly calls the existing deliverNext operation.

The language encounters eight root-call argument errors, two constructor
errors, two attachment metadata errors, one expression error, one invalid
commit state and one broker-denied example operation. F# encounters two invalid
patch hashes, eight stale-content responses and one failed validation before
its final passing build. These describe actual interface and syntax friction;
they do not demonstrate a weakness in F#'s type system.

## Preservation is a separate result

The strict structural audit remains false. It is not changed to accept the
participants' submissions after seeing the result.

The language agent replaces email.apply-delivery-result's fold-based queue
split with list.get/list.tail, converts its three tests and one example, and
qualifies it as library. This is a production implementation rewrite, beyond
formatting. Branch review finds equivalent empty/nonempty queue behavior and
error precedence; the prior tests retain their scenario literals and checks.
The old blobs, original types and stable identities remain available, and no
unrelated original production function changes. Nevertheless, the inherited
example changes its observable result from the string NO_PENDING_EMAIL to the
boolean true while checking that error internally. Its original presentation
contract is not preserved. Historical storage is not a substitute for retaining
the current example.

F# adds a result type and batch function without changing original production
functions. It prepends new checks within the existing email test function.
Consequently, the audit's exact test-block comparison is false. Independent
source review confirms the complete original body and assertions remain after
the additions, with line endings normalized; this is not a byte-identity claim.

The independent preservation review and raw audit are both archived. Behavioral
acceptance, library qualification, semantic review and exact preservation remain
separate outcomes. Passing coverage did not guarantee preservation of example
output. No coordinator repair is applied to either scored candidate.

## Code produced

The language participant's actual public function is:

```text
fn email.deliver-batch(store: Store, outcomes: List<Result<Unit, String>>) -> EmailBatchResult {
    doc "Deliver queued emails in FIFO order for each supplied provider outcome."

    let initial = emailBatchResult.new(store = store, delivered = 0, failure = option.none<BusinessError>())
    outcomes.fold(initial, email.deliver-batch-step)
}
```

Its helper returns the accumulator unchanged after failure or queue exhaustion.
The fold still visits later supplied outcomes; those visits perform no delivery
attempt. The F# implementation uses a recursive loop that returns immediately
on exhaustion or error. This is a semantic behavior comparison, not a throughput
benchmark or claim of equal internal work.

## Implication and next work

Both agents voluntarily discover and reuse existing single-message vocabulary.
The language's publication and coverage gates work for this composition, but
do not establish superior edit reliability. The example change demonstrates
why preservation must be checked independently of coverage and test totals.

Close this bounded trial. Prioritize the observed qualified-call and attachment
conversion friction before another similar study. Make inherited example output
an explicit preservation contract in future trials. Avoid another easy batch
variant or additional harness construction without a distinct research question.
Keep LLVM/arena conformance as the secondary development track.

## Validation and evidence

Both independent scorers run locally against final, closed participant projects.
The F# scorer builds its candidate fresh. Final participant test-all/validation
responses and task publication are retained in terminal broker traces. All frozen
inputs are rechecked after scoring. This is a research/report milestone with no
product-source change; the preceding native milestone's complete local gate is
recorded in [report 174](174-native-refined-string.md). No CI run is requested.

The first preservation audit fails before reading candidate sources because its
loader expects actors instead of actorInputs in the frozen manifest. Its empty
output and a coordinator reconstruction of the error are retained, distinguished
from a raw stderr transcript. The corrected second audit's genuine structural
failures are retained unchanged.

The [archive index](evidence/175-email-fifo-batch/archive.json) records hashes for
the [evidence archive](evidence/175-email-fifo-batch/evidence.zip): frozen inputs,
accepted seed lineage, calibration controls, failed attempts, candidate sources,
terminal traces, scoring, metrics and preservation review. Build trees, binaries,
temporary environments and credentials are excluded. Broker restrictions are
workflow constraints, not an OS security sandbox; correctness results here are
not a cybersecurity audit.

Archive correction: the initial filter treated CURRENT as a plain manifest hash,
but it is a JSON pointer. It therefore omitted unpinned final manifests. The
verified [supplement index](evidence/175-email-fifo-batch/supplement.json) and
[supplement archive](evidence/175-email-fifo-batch/supplement.zip) retain every
CURRENT-referenced final manifest and pointer, including the scored candidate.
The original archive, its hash, trial sources and results remain unchanged.

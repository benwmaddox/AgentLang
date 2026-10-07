# Explicit trial transport termination

Status: focused validation passes; the clean-source full release gate is pending. This milestone adds
reliable terminal evidence for future trials; it does not reclassify report 079's
failed frozen trace audits or establish a new agent outcome.

Both 079 actors sent the prescribed Ctrl+C tool input, but the v1 traces ended
with clean host/runtime exits and no cancellation event. The frozen auditor
correctly rejected those unrecorded ends. Future trials need an explicit,
portable transport control rather than an inference from an EOF or clean exit.

The new versioned host reserves the exact frame `{"op":"host.close"}`. It is
not a language word, never reaches the runtime and does not consume the runtime
exchange budget. An accepted control retains exact request bytes and the current
exchange count, then closes the runtime's input and waits within the configured
shutdown deadline. There is no stdout acknowledgement; the terminal process
result and trace establish what happened. Ordinary EOF remains distinct.

The v1 host, authoring-help-002 inputs, pinned source snapshots and completed
results remain unchanged. A versioned generic termination audit and transport
controls must pass before any study adopts v2. The standard validation gate
adds a focused v2 verifier alongside the existing v1 checks.

Required evidence: real CLI durable commit/reload, no extra runtime operation,
close at the exchange bound, malformed/duplicate/extra-field/unterminated close
rejection, ordinary EOF rejection, bounded unsuccessful shutdown and tampered
terminal evidence rejection. Tests use fresh isolated transport fixtures and
retained unique evidence. Implementation checks are not an agent experiment.

Publication follow-up: exact 078 main CI run 37571612474 failed only the existing
v1 inspection-counter reset fixture. Its healthy fake-child exchange reached
TRIAL_EXCHANGE_TIMEOUT under the inherited two-second deadline. The exact CI
command passed locally with all 33 checks using unchanged v1 verifier bytes; both
remote failure and local reproduction evidence are retained. Ordinary fixture
deadlines are being made tolerant of runner scheduling, while deliberately short
timeout cases, runtime defaults and uncertain-outcome assertions remain intact.

## Focused result and evidence

The final focused attempt passes **29/29 checks**. It executes real CLI library
definition, tests, durable commit and fresh-process reload; close at the runtime
exchange bound; whitespace/CRLF framing; close under a one-byte response cap with
no acknowledgement; malformed, duplicate and partial close frames; ordinary EOF;
and bounded failed shutdown. A valid six-event baseline passes the auditor before
mutation. Negative controls require their intended rejection diagnostic for hash,
event count, missing/duplicate terminal events, limits, uncertain execution,
incomplete responses and cancellation history.

[Final focused evidence](evidence/080-host-v2-focused-retry-08.json) records the
dirty implementation checkout and exact script/runtime hashes. It is not a
clean-source full-gate result. [The attempt index](evidence/080-control-attempt-index.json)
retains all eight failed coordinator attempts. Those failures exposed PowerShell
array, strict property-access and mutation-fixture bugs. Retry 07's nested event
array could cause unrelated rejection; its partial negative checks are not proof
that the intended controls worked. The final retry explicitly validates baseline
shape and acceptance before asserting specific rejection diagnostics.

[The raw trace archive](evidence/080-transport-trace-archive.json) maps 78 retained
traces to portable checked-in copies with byte lengths and SHA-256 hashes. Copies
were checked against their source bytes, including failed attempts and mutation
fixtures. These are implementation controls, not fresh model experiments.

Exact milestone 079 main CI passed all **36 checks** on clean source `93b9e19`
(run 37573276020). The failed 078 main CI, successful local exact-command
reproduction (33 v1 checks), and verifier-only deadline change remain separately
recorded. The new complete gate has 37 checks and must pass before publication.

## Evaluation implication

This removes an observability defect from future trials. It does not repair the
frozen 079 actor traces, establish token savings or prove a vocabulary benefit.
The shortest remaining research path is rotated matched repetitions and a
reset-rich control, with actual usage/context measurements where available.
Native compilation and memory-policy research remain later work.

An independent static review confirmed flat mutation fixtures and diagnostic-specific
negative audits. It also identified two broad host-failure assertions. The final
verifier now checks the reserved-allowlist startup message and exact unterminated
frame diagnostic, retaining those messages in check details. This final strengthening
postdates retry 08; the clean-source full gate must execute the updated assertions.

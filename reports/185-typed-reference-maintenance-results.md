# 185 — Typed reference maintenance: first-participant outcome

Status: the first retained participant was scored and the six-person cohort was
closed before any other dispatch. This is an environment capability-gap finding,
not a balanced efficacy comparison. No reliability or vocabulary-advantage rank
is claimed.

[Report 184](184-typed-reference-maintenance.md) records the frozen task,
independent oracle and calibration. Study inputs were frozen at `687753f` and
the accepted runtime at `49a7f30`. Only retained-1 ran: a fresh external
GPT-6-Luna/max participant with no inherited conversation or oracle feedback.
The other five participants were not dispatched.

## Outcome

The participant added and durably committed a new `TrackingReference` scalar,
but did not migrate `Scan.reference`, `ScanInput.reference`, or the existing
helper parameters from `String`. Those required fields and helper signatures
remain `String`. The accepted frozen scorer executed all 18 independent cases:
13 passed and five replay-idempotence cases failed. Its saved result uses scorer
SHA-256 `4d674aefeb9c79b992bd372835d26e4d289f570c8aded1f90990777dcf1d8659`;
the saved score was not rerun for this report.

The participant's `task.commit` succeeded, but the terminal task log recorded an
empty goal and zero tests run. No new test sources were added. The candidate
therefore preserves and commits a partial project change without delivering the
requested maintenance behavior.

The baseline has 21 inherited tests across ten retained functions. The
preservation receipt confirms all 21 inherited test source rows are exact.
The ten existing function identities, signatures and
maturity metadata are unchanged. The durable-preservation audit independently
matched all ten full persisted revision rows—including definitions, tests,
examples and call bindings—matched all ten inherited type rows, and verified the
candidate word-object hashes against the baseline. This establishes byte
preservation of inherited source bodies; it does not show that those old tests
cover the newly requested replay behavior.

The broker ended normally after 53 exchanges, with host and runtime exit codes
0/0. The trace records 2,734 request-payload bytes, 72,965 response-payload
bytes, and 271.102 seconds of broker duration. These are protocol measurements,
not LLM token usage, context size, total agent time, or service performance.

## Capability gap and preparation finding

The trace records rejected record-replacement attempts at exchanges 49 and 50.
The frozen Flow `define` add-only route rejects an already registered type; the
word-replacement route requires multiple existing word declarations and does
not accept type declarations. The read-only source review in the
[evidence bundle](evidence/185-typed-reference-maintenance/bundle.zip) traces the
same restriction through the default Stack parser and registration path. It
confirms that none of the participant's allowed routes can replace an authored
type under its existing name. The review did not inspect the captured candidate,
so these implementation findings are separate from participant behavior
evidence.

Calibration built fresh migrated controls but did not test migration of types
that were already persisted in the participant seed. That is a preparation gap
and explains why this participant-facing task was not yet implementable through
the frozen interface. The `task.abort` operation was absent from the frozen
allowlist; the participant did not exercise rollback, and this report makes no
rollback claim.

The earlier source-display observation is corrected: semicolons found in the
inspected test text were already present in the authored seed. The source-printer
review confirms inspection returns stored source bytes; no printer defect was
shown.

## Disposition

Stop the current cohort. Do not treat the five undispatched participants as
missing measurements, balance conditions from this run, or infer a reliability
rank, vocabulary advantage, or general efficacy conclusion from one retained participant.

Atomic type evolution is the immediate implementation prerequisite; see the
[implementation plan](../docs/TYPE-EVOLUTION.md). After that change is implemented
and validated, freeze a new cohort against the new runtime and inputs. Do not
pool results from this runtime with results from the changed runtime.

The evidence bundle and per-file manifest are in
[evidence/185-typed-reference-maintenance](evidence/185-typed-reference-maintenance/index.json).
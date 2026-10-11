# 230 — Grouped renewal timing replacement preflight

Status: input preparation complete; calibration pending; no participants dispatched.

The next comparison tests replacement of an existing function contract and
preservation of its callers. Both fresh agents will group monthly renewal's
two timestamp arguments into a nominal `RenewalTiming` record, migrate the
actual callers, retain existing behavior and tests, and add a direct regression.
The seeds are the accepted report-228 endpoints. Runtime pins will use accepted
report 229; historical inputs and evidence remain unchanged.

The matched acceptance set keeps the seven scheduled-renewal cases and adds
two direct monthly cases: distinct start/expiry timestamps and expiry equal to
start. It compares complete Store values and input preservation. AgentLang's
66 inherited observations and library qualification remain separate from the
nine common cases; no equivalent 66-case F# claim is planned.

Preservation will be checked independently from behavior. The AgentLang delta
is restricted to the new type, the monthly function and its two actual caller
bodies, necessary direct test-call updates, and the new targeted test. Existing
identities, unrelated definitions and expectations must survive. F# changes
only `Operations.fs` and `SelfTests.fs`. Its two batch paths share one caller;
AgentLang's seed has separate callers. This inherited architecture difference
will be disclosed rather than attributed to the language.

New generated constructor/accessor entries are restricted to the new type.
The three replaced functions may advance revisions; all old revision rows and
source objects must remain intact. The aggregate project export may change.
The equal-timestamp case uses a distinct new ID to avoid masking the expiry
error with duplicate-ID precedence. Independent scope review identified these
details and the additional scheduled-step test call before preparation was frozen.

Before dispatch, a correct control must pass and controls with swapped timing,
an unmigrated caller, or a weakened inherited assertion must fail the applicable
behavior, publication or preservation check. Fresh actor copies, input hashes,
interactive broker startup and closure will be verified. Preparation reuses
existing fixed-purpose brokers and scorers; it does not add a general harness.

The coordinator's actual interactive startup checks passed for both arms:
F# answered `inspect`, AgentLang answered `help`, and each live session closed
through `host.close` with both host and runtime exit code zero. These checks
used disposable preflight copies, not participant projects. Calibration and
final independent review are still pending; dispatch remains disabled.

Independent preparation review found no blocker in the public task, launchers,
path handling or pinned inputs. It verified that both actors still match their
baselines and that all 22 files in each private runtime match the recorded
hashes. The review covers preparation only; oracle and control review remain
separate gates.

The first proposed task added a third argument to a list callback. Inspection
rejected it because current callbacks cannot capture caller locals, and the
two seeds did not share the assumed caller graph. The grouped-time contract is
representable without changing the language. See the [plan](../experiments/AgentLang.SubagentTrials/replacement-trial-230/PLAN.md).

No efficacy result follows from preparation. No packages, external capabilities,
network listeners or host escape are introduced. Broker-only/no-web rules are
instruction and application boundaries, not OS confinement. Participant code
will remain within local sandbox execution; host retries may build trusted
coordinator dependencies but must not execute participant code.

The [preparation archive](evidence/230-grouped-renewal-timing-preparation/preparation.zip)
and [member index](evidence/230-grouped-renewal-timing-preparation/index.json)
retain the pinned baselines, private runtime files, task packets, launchers,
setup, preparation receipt, review and actual smoke traces. Archive SHA-256:
`6ed9a50842f34a0af4a477f635459a5fa7e43401efb0578f16c7aced1c42b1db`.
The archived report predates this footer and status update. This snapshot does
not freeze the oracle or authorize dispatch; those gates remain pending.

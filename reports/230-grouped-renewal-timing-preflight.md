# 230 — Grouped renewal timing replacement preflight

Status: calibrated and frozen; F# trial completed and AgentLang trial underway.

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

The F# behavioral controls now produced nine observations each. The correct
control matched all nine expected outcomes; the swapped-timestamp control
diverged from correct behavior in six cases, exactly as the model predicted.
These are coordinator calibration results, not agent results. The first
control attempt failed because its directory depth changed a relative project
reference; its raw failure logs were retained before retrying at the original
depth with the project file unchanged.

Provisional oracle review found preservation gaps: whitespace normalization
could alter quoted expectations, some valid local record constructions were
rejected, generated dictionary entries were allowed by name alone, and F#
needed a separate audit of its six source files and inherited assertions.
The AgentLang gaps were fixed and validated. The F# audit now accepts local
bindings, either record-field order, inline result assertions, match-based
assertions and regression placement after the annual scenario. Its 12 pure
source controls passed. Production behavior and source scope are checked
separately: swapped timestamps pass the permitted edit scope and fail six
behavior cases.

The F# audit remains lexical. It preserves inherited tokens and quoted literals
but does not prove equivalent assertion execution when indentation or inserted
control flow changes. Before dispatch, acceptance therefore explicitly requires
independent review of final sources for inherited assertion execution/scope and
the complete non-generated file delta. The same independent final review also
checks the AgentLang submission. Static failures remain visible and must be
resolved against the original requirements; no preservation requirement is
relaxed. This is a material automation limit, not an agent result.

The correct AgentLang control published the three revised functions with their
existing identities after all 160 attached tests passed (159 inherited and one
new regression). Its six direct monthly test calls were migrated. The new
record's generated constructor/accessors are runtime entries, with no added
persisted function identities. Separate independent scoring now matched all
nine common cases and all 66 inherited creator cases. Preservation verified
284 inherited source/test/example objects, prior revisions and unrelated
definitions; all three replaced functions remained library-qualified. The
swapped candidate produced exactly six predicted behavior mismatches. The
corrected old-caller control returned `FLOW_ARGUMENT_ARITY` (expected four,
received five) without advancing the monthly revision or changing its
manifest. Weakened-assertion and semantic type controls also passed calibration.

The disposable F# control builds enabled NuGet audit but could not fetch
advisory data in the sandbox (`NU1900`). Their receipts explicitly report
unavailable audit data; a successful build does not establish an audit pass.
No new package was introduced and no participant code was executed on the host.

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
not freeze the oracle or authorize dispatch. Those later gates are recorded in the preflight archive below.


The [sealed preflight archive](evidence/230-grouped-renewal-timing-preflight/preflight-230-20261011T191419.684150Z.zip) and [member index](evidence/230-grouped-renewal-timing-preflight/preflight-230-20261011T191419.684150Z.members.json) preserve the 48 frozen inputs and 10,079 verified evidence/source members. Archive SHA-256: `1728f033de7052955b4450d25c95105df33cb858403193b39786f77e016fe34c`. This preflight-only archive includes zero participants; it was sealed after dispatch from frozen pre-dispatch inputs. The archived report predates this footer. Both trials use fresh Luna/max agents, F# first, under sandbox-only execution. Participant results will be reported separately.

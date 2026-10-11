# 216 — Typed batch failure localization maintenance

Status: implementation/preflight, not frozen or dispatched. No participant
outcome. Freeze the packet, adapter, controls and permitted changes before launch.

## Question

Can a fresh developer change a persisted batch API to a structured failure,
update its production fold state/helpers and preserve prior behavior and test
scenarios? This follows 213, whose nine-case task had both participants correct.
Use both accepted terminal 213 projects, independently copied and fingerprinted.
This is a paired continuation reliability probe. The projects have different
batch bodies, helper/state types and tests. It is not a clean causal retention
contrast, and it has no conventional arm or population reliability estimate.

## Public task

Keep subscription.renew-batch's existing inputs. Change its result to
Result<Store, SubscriptionRenewalFailure>, with an exact nominal record:
index: Int, old-id: SubscriptionId, cause: BusinessError. On failure report the
zero-based INPUT position and old ID of the FIRST failing item, together with
its unchanged existing cause. Stop at that error. Preserve sequential validation,
all successful Store fields/list order, unrelated entities and the caller's input.
Empty lists still succeed unchanged. No partial Store is a successful result.

Preserve the existing root and step function IDs and library maturity. Root,
step and their internal batch state may change. Reset uses
SubscriptionRenewalState; retained uses SubscriptionRenewalBatchState. New
related helper functions/types are permitted, with normal library qualification.
All common-seed objects and retained monthly/annual renewal APIs, tests and
examples remain unchanged. Keep SubscriptionPlan and SubscriptionRenewal intact.

Retain all existing batch/helper test identities and scenarios. Necessary result
annotations, structured failure expectations, counter fields and helper state
construction may change; preserve input fixtures, expected success state and
expected underlying causes. Do not replace scenarios or weaken assertions.
Add tests for a later-item failure and first-error precedence/localization.
Passing coverage is necessary but does not establish semantic preservation.
An independent source review decides this narrowly allowed adaptation separately.

## Inputs and launch

Reset terminal fingerprint: b939b981fb1ba47a3f004d2bd76c7851adbe8f744f72d1cf62ea100873b1361e.
Retained terminal fingerprint: b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611.
Use the accepted 213 CLI only after all 37 source and five runtime pins match.
One fresh Luna/max participant per project, randomized recorded order,
sequential runs, same broker operations/timeouts, 100-exchange cap, virtual I/O,
empty effect grants. Withhold controls, outcome feedback and other participants.
Read only assigned packet outside the broker and launch the assigned command;
afterward discovery, edits and tests use that broker. Disclose other tool use.
Never restart merely after observation timeout; poll the same live handle.

## Acceptance and controls

Reuse the nine frozen 210/213 case inputs and independent occupied-cell transition
model. Derive localization by enumerating original inputs against evolving model
state until its first error. Score complete success Store, input preservation,
exact cause, index and nominal ID. Retained additionally executes all 66 original
creator cases unchanged. Reset need not expose either retained renewal function.

Require complete responses/zero CLI exit, no setup errors, all tests/examples
passing, nominal record fields and exact root signature, root/step identity and
library qualification, unrelated objects unchanged, inherited test identities
retained, normal broker closure, and unchanged actor fingerprint after scoring.
Manual preservation review is an explicit final acceptance gate; the automated
adapter must not label unreviewed assertion changes as accepted edits.

Before dispatch, execute correct and deliberately wrong controls on copies.
Model predictions are correct 9/9, always-zero index 6/9, last-error wins 8/9;
verify these predictions rather than relying on them. Calibrate malformed fields,
wrong nominal types, missing responses, baseline mutation and lost test identity
as separate adapter failures. Demonstrate a public-interface migration on the
persisted baseline, including passing adapted suites; behavior controls alone
do not prove authoring readiness. Preserve failed preparation receipts.

No new harness framework, effect capability, public listener or .NET escape.
Broker restrictions are prompt-governed, not an OS sandbox. Protocol bytes and
broker duration are not model tokens, context limits or native service speed.
Store preflight and participant evidence separately; sealed historical results
remain unchanged. Do not dispatch before readiness is verified and frozen.

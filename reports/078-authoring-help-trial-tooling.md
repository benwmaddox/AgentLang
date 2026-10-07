# Versioned authoring-help evaluation tooling

Status: focused controls and repaired clean-source freeze checks passed. No new external-agent
outcome or efficiency result is claimed.

Report 077 implemented inspectable authoring help after the two Flat metadata
failures in report 076. This milestone prepares fresh independent Flat S06 and
S07 trials under `business-policy-help-002`. The original 001 prompts, scripts,
pins, traces, oracle and results remain unchanged.

The new oracle retains the original 10 S06 and 54 S07 behavior cases. Preparation
starts from the same six-type schema-only seed. The public rules still require
raw ordinal `premium`, exact signed Int64 Money, and truncation toward zero.
No prior task solution or coordinator acceptance is supplied to the actor.

The versioned verifier records metadata and independent behavior separately.
Metadata failures remain acceptance failures while executable safe targets still
run the complete behavior corpus. Missing or unusable targets stop execution.
The new version also reads protocol diagnostics from `error.message` or `text`;
it does not retrospectively rewrite the old overflow-control diagnostic.

Preparation, freeze and trace audit preserve source/runtime inventories, public
prompt bytes, schema-only project identity and the supplied host allowlist. The
unchanged v1 host explicitly permits help and has no cumulative inspection-byte
budget. Protocol-only editing is an instruction boundary, not an OS sandbox.
Actual model token usage and effective context limits remain unmeasured.

Public guidance and runtime help availability both change. This follow-up can
show whether fresh agents meet the contract, but cannot isolate the causal
contribution of help alone. The expanded predecessor primer's discovery,
call-reference and coverage guidance is retained alongside the new authoring
instructions.

Review caught timestamp-string conversion and ambiguous candidate replacement
guidance during preparation. Both were corrected before freeze; defaults
must remain identical JSON strings and candidate replacements use normal commit.

Actors launch only after the tooling's positive and negative controls pass and
its source is committed cleanly. Each fresh Luna/max actor owns its live host
handle; independent verification uses a disposable copy before the actor closes
its own session. Completed outcomes and archives belong to a later report.

Focused validation passed 59 checks across 14 verifier outcomes: correct S06/S07,
missing documentation, no-op implementations, wrong boundaries, missing targets,
missing required pins and evidence collisions. Safe targets ran all 10 or 54 cases.
Invalid required pins and existing evidence outputs started zero JSONL sessions,
zero processes and zero oracle cases. Earlier coordinator fixture failures remain
archived in [the attempt index](evidence/078-control-attempt-index.json); they are
not external-agent failures. The final matrix is saved in
[the focused report](../experiments/AgentLang.SubagentTrials/business-policy-help-002/evidence/078-verifier-focused-run-20261007T040056224Z-39cbf996ca2a466f993a592a76eedafa.json).

The solution also built fresh in Release with zero warnings/errors. Runtime source
is unchanged from milestone 077, whose exact main CI run passed all 36 checks.
A valid frozen-pin check and one-field tamper control remain pending until this
tooling is committed; agents have not launched.

The first postcommit preparation exposed a wrapper defect: strict PowerShell
property assignment could not add the new preparation field to the original
helper's starting-state object. It failed before prompt/pin creation or actor
launch. The partial preparation is retained under the ignored failure archive;
the preparer and its corresponding source snapshot are corrected together before
a new clean-source commit and retry.

Both preparations and freezes succeeded at clean repair revision f559852. The
positive required-pin control then exposed a verifier initialization-order defect
and a host-settings assertion mismatch. It stopped with zero CLI sessions,
processes and oracle cases. No actor launched. This failed freeze/control state
is preserved; both trials must be frozen again after the verifier/source-snapshot
repair. The earlier matrix did not exercise a valid pin, so it was insufficient
to establish launch readiness.

The pin-check repair reads the clock from its raw JSON string token, avoiding
PowerShell ISO timestamp coercion, and checks the launch command after resolving
the CLI runtime path. A no-runtime diagnostic confirmed both predicates against
the preserved pin. AST/LF checks and exact verifier/source-snapshot hashing pass;
the full positive pin test still requires the repaired clean-source freeze.

A second positive-control attempt at 1cce62c exposed an inventory comparison
defect: dictionary rows were sorted as objects without named properties. The
verifier must materialize named runtime/path/bytes/hash properties before sorting,
as the freezer does. Both controls stopped before execution, their evidence is
preserved, and no actor launched. The complete valid-pin path remains a gate.

Final launch gates passed from clean source c380703: both S06/S07 required pins
validated, then safely stopped at the expected missing target with zero behavior
cases. A one-field model-pin tamper failed exactly the model declaration check,
with zero CLI sessions, processes and behavior cases. Because validation resolves
the canonical pin path, this coordinator-only control temporarily replaced that
file and restored its original bytes in a finally block; SHA-256 is identical
before and after. No actor was running. The control is saved in
[the tamper summary](evidence/078-tampered-pin-control.json), with full verification
and both valid-pin prelaunch reports beside it. The earlier source commit and
failed coordinator checks remain retained; they are not agent outcomes.

The finalized primer grew from 3,091 to 3,931 UTF-8 bytes and from 418 to 528
whitespace words. Those measurements are not LLM tokens or effective context.
Only the two new Flat tasks are being rerun; this is neither a new conventional
comparison nor a controlled causal test of help alone.

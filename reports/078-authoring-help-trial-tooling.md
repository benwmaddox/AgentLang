# Versioned authoring-help evaluation tooling

Status: focused controls passed; clean-source freeze checks pending. No new external-agent
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

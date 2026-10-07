# Inspectable authoring help

Status: implemented and locally validated. No new agent outcome
or efficiency improvement is claimed.

Report 076 found two Flat agents could author, test and library-commit policy
words while leaving documentation metadata empty. Agents also confused source
staging with `replace-word` persistence. The next shortest improvement is to
make the language's authoring contracts inspectable and reject unsupported Flow
definition fields rather than accepting ignored metadata attempts.

The change provides bounded deterministic `help` topics through JSONL,
the human REPL, and the existing model inspect tool. The tool count remains six.
Flow help includes inline documentation, attached tests/examples, supported
request fields, literal expectations, revision checks and replacement finalization.
The compact primer gives a discovery pointer; the [authoring guide](../docs/AUTHORING.md)
contains the full sequence. JSONL protocol capabilities and the smaller live
model-tool adapter remain explicitly distinct.

Unknown top-level Flow define fields and unknown attachment-removal fields
fail before staging. Explicit Stack requests retain their existing behavior.
Library persistence still requires own tests and complete own supported coverage;
documentation and examples remain separately inspectable metadata rather than
new runtime commit requirements. Completed study prompts, pins, scripts and
trial evidence remain frozen.

The frozen version-1 subagent host does not classify `help` as inspection for
its optional cumulative byte cap. Follow-up help trials must be unbudgeted until
a separately versioned host covers that operation. Complete model-harness
request-byte accounting is separate. No historical host source is rewritten by
this milestone.

Acceptance executes help examples, persists/reloads metadata, exercises candidate
and committed replacement paths, and verifies failed requests leave definitions,
durable authority and a virtual-filesystem sentinel unchanged. Core build passed
without warnings or errors; Flow Runtime passed 24 groups and 774 assertions.
The [focused evidence](evidence/077-core-flow-focused.json) retains intermediate
failures. Review corrected undefined expectation examples and clarified that
external attachment arrays are for single-word requests; multi-declaration and
attachment-only documents require inline cases. See the
[review record](evidence/077-independent-review.md).

The complete Flow system primer grew from 922 to 1,606 UTF-8 bytes (140 to 233
whitespace-delimited words), including unchanged shared rules. These are
[text measurements](evidence/077-primer-size.json), not model token counts.
Help trades some initial context for discoverable authoring contracts; its net
cost or benefit remains unmeasured. Harness focused checks passed 249 assertions;
CLI passed 9 groups and 129 assertions. The [focused transcript](evidence/077-harness-cli-focused.json)
preserves initial compile/assertion failures and the final passing runs.

The fresh full Release gate passed all 36 checks, including 98 checks across
30 isolated business-policy control outcomes, with zero build warnings/errors.
See [saved local validation](evidence/077-local-validation.json). This validates
the working changes over source baseline `866e00b`; clean committed-source
GitHub CI is observed separately after publication.
Fresh external-agent follow-up is required to assess whether these interface
changes resolve the observed failure; executable examples alone do not establish
agent usefulness. The full PRD and broader controlled evaluation remain active.

Publication follow-up: [main CI run 37565992570](https://github.com/benwmaddox/AgentLang/actions/runs/37565992570)
passed all 36 checks on clean revision `7f02a82135de68364df58517ef5eb439df2eb9ab`.
See the [downloaded validation report](evidence/077-main-validation.json) and
[run status](evidence/077-main-ci.json).

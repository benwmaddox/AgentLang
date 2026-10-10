# 177 — Reliability and accumulated vocabulary checkpoint

Status: evidence review through report 176, 2026-10-10. No new participant trial
or runtime change. External AI coding agents build and use the language; AI is
not part of its execution semantics.

The prototype supports inspectable, strongly typed vocabulary that agents can
discover, compose, test and publish. The research has **not demonstrated a
comparative reliability advantage over F#**. Reuse is promising, but passing
behavioral acceptance and library qualification have sometimes coexisted with
lost assertions or changed examples. These are separate outcomes.

## Audited observations

Two independent read-only audits checked archived acceptance results, traces,
preservation reviews and archive hashes. Earlier synthesis remains in
[report 151](151-efficacy-checkpoint.md). Counts below have different units and
must not be pooled into a success percentage.

| Study | Observed behavior and reuse | Preservation and interpretation |
| --- | --- | --- |
| [149](149-validated-window-reuse.md), validated billing input | One agent per environment; both pass 18 hidden cases and reuse the API. | The coordinator designed the validated-input API. This establishes usability, not autonomous abstraction design or a language advantage. |
| [150](150-shared-rule-maintenance.md), billing maintenance | One agent per environment; both pass 48 cases across singleton, helper and batch behavior. | Shared calculation/delegation remains. AgentLang has ten transient assertion failures while revising expectations; final results pass. F# also reuses the abstraction. |
| [163](163-atomic-subscription-handoff.md), atomic transition | One agent per environment; both pass 33 cases: 26 common valid-reference cases and seven orphan-reference checks. | Both preserve existing implementations and reuse transitions. One pair cannot rank reliability. |
| [166](166-handoff-signature-maintenance-agent-pair.md), signature and callers | One agent per environment; both pass 132 independent observations. AgentLang passes 149 saved tests and qualifies all three functions. | AgentLang changes a conflicting-input test scenario and drops two success-test pre-state assertions. F# retains prior assertions. The library gate does not detect evidence loss. |
| [168](168-test-source-inspection-agent-probe.md), inspection follow-up | One fresh language agent discovers test-source inspection, passes 132 observations and 150 saved tests. | It retains the conflicting-input scenario but again drops the two pre-state assertions. Inspection availability does not guarantee preservation. This is not a fresh matched F# comparison. |
| [171](171-preview-vocabulary-retention.md), retained versus reset-rich | One language agent per condition; both pass the same 33 cases. Retained discovers handoff and writes a wrapper with 12 own instructions versus 30 for reset. | Both preserve prior objects and qualify their new function. A smaller wrapper implementation surface is observed, not less context, transitive work or superior correctness. Treatment includes retained tests/metadata/history; fixtures are reused. |
| [173](173-handoff-window-maintenance.md), shared rule and callers | Two agents per environment, sequential ABBA; all pass 144 observations each and reuse the shared policy. | Language preserves inherited attachments byte-for-byte. F# preserves assertions with one disclosed fixture-time adjustment. Unequal seeds and patch guards limit attribution. |
| [175](175-email-fifo-batch.md), effectful batch | One agent per environment; both pass six independent cases and reuse single-message vocabulary. | Language changes an inherited example output from `"NO_PENDING_EMAIL"` to `true` and rewrites a helper. F# source review retains original assertions despite a strict block-audit flag. Behavioral acceptance does not establish collateral preservation. |
| [176](176-retained-attachment-source-versions.md), engineering repair | No new agent trial. Replay preserves three tests, one example, identity and bindings; 191 saved tests pass before and after reload. All 37 local Release checks pass. | Independent attachment source formats repair a demonstrated editing obstacle. This does not retroactively repair report 175 or show improved future agent behavior. |

## What this tells us

Discoverability and reuse were observed on these bounded tasks. Agents sometimes voluntarily reuse
domain functions, and report 171 shows how retained vocabulary can reduce the
logic a later agent authors. Conventional F# agents also reuse APIs. The samples
do not establish that accumulated vocabulary causes more reliable edits.

Strong types, exhaustive matching, effect checks and library gates are useful
enforced contracts. Coverage shows observed execution; it does not establish
the intended behavior or preservation of earlier evidence. Independent acceptance
and collateral checks remain necessary. Reports 166, 168 and 175 provide concrete
counterexamples to treating a passing gate as sufficient.

The findings are mixed rather than a rejection of the project. Successful
maintenance with preservation in report 173 and repeated discovery/reuse justify
another bounded study. Recurring evidence loss requires making preservation an
explicit endpoint, rather than counting successful execution alone.

## Unanswered questions

- Whether agents independently create abstractions that remain useful across a
  longer task sequence, and whether vocabulary pollution grows with them.
- Whether retention improves behavioral correctness, regression preservation or
  defect recovery compared with reset-rich vocabulary and conventional F#.
- Whether the compact trusted core stays adequate as tasks diversify: capability
  requests, primitive growth and escape-hatch pressure have not been measured here.
- Whether smaller controlled context budgets retain success. No such comparison
  is established, and authoritative provider input/output token counts remain
  unavailable. Broker exchanges, bytes and elapsed time are not substitutes.
- Whether the eventual native mailbox/arena runtime meets service performance
  goals. [Report 140](140-matched-mailbox-load.md) is a bounded load study with
  failed as well as passing sustained-load confirmations; it does not select a
  universal arena policy or demonstrate general server superiority.

## Next decision

Close the easy composition microtrials. The next efficacy work should be a small,
predeclared repeated **defect-repair and shared-rule maintenance sequence** using
the existing brokers. Compare retained vocabulary, reset-rich vocabulary and
conventional F#; keep domain fixtures equivalent and distinguish retention from
the overall language/tooling treatment. Define the primary retention treatment
before dispatch: either match auxiliary tests, metadata and history where possible,
or explicitly evaluate the whole retained project-state package. F# is a separate
workflow benchmark; its test results are not language-library qualification.
Do not build new harness infrastructure
or wait for a complete native service runtime first.

Before dispatch, freeze intentional defects, independent behavioral acceptance,
collateral objects/assertions/examples, and permitted fixture changes. Record
behavior, preservation, library qualification and recovery separately. Match
source access and editing guards as closely as practical, disclose remaining
differences, repeat participants and rotate order. A small study still cannot
provide a general reliability ranking; its purpose is to expose mechanisms and
failure modes beyond the present easy-task ceiling.

Use deterministic in-memory effects for these agent trials. This checkpoint adds
no network, process or arbitrary host-library capability. It is an efficacy review,
not a cybersecurity audit. LLVM and arena conformance remain a separate secondary
track, with the typed semantic IR authoritative across execution backends.

## Evidence and validation

[Input pins and audit record](evidence/177-reliability-and-vocabulary-checkpoint/review.json)
identify the source revision, reviewed inputs, audit scope and documentation
validation. Underlying trial archives remain with their individual reports;
historical submissions and failed outcomes are unchanged. Validation for this
documentation-only checkpoint checks local links, pinned inputs and Git diff
formatting, followed by independent review. The runtime's last full validation
is report 176; it was not rerun or counted as a new result here.

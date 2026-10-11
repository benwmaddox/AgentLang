# Independent preflight reviews

Reviewed 2026-10-11 by a separate GPT-6.1-sol/medium subagent.
These reviews concern preparation controls, not participant outcomes.

## Adapter review

The final adapter review accepted all eight fixture groups with no outstanding
material scorer issue. Review found and required corrections for inherited-test
multiplicity, added example owners, and additions to root/helper examples. The
final checks allow additions while rejecting removal, rewriting or reduced
multiplicity of inherited evidence. Runtime calibration and source preservation
remain separate requirements. The adapter deliberately reports
`behavior-passed-review-required`, with accepted-edit and overall pass false,
until independent source review is supplied.

## Published control preservation

Independent source-preservation review passes for successful control run
`20261011T111403757539Z-b833459a`.

All 11 reset and six retained inherited batch/helper tests retain their identities,
fixtures, scenarios, successful-state expectations and underlying errors.
Adaptations comprise typed failure wrappers/projections, counter/state
construction and equivalent canonical formatting. No assertion weakening or
scenario replacement was found. Each arm adds two tests for later-item
localization and first-error precedence.

Root/step IDs and library maturity remain intact. Changes stay within root,
step and internal state, plus the new failure type. Other baseline objects and
inherited examples remain unchanged. Both immutable baseline fingerprints match
the accepted 213 values.

The supplied scorer receipts record 9/9 behavior for both, retained 66/66 creator
regressions, exact 50/116 responses and unchanged scoring inputs. Published suites
report 150/150 reset and 153/153 retained, with all 39 example owners passing.

Scope: published source, every inherited test diff, preparation
transformations/templates, manifests and supplied receipts. The reviewer edited
no files and invoked no runtime. This establishes preservation for these
preparation controls; it is not an inference from coverage alone.

## Fault calibration review

Final calibration logic passes independent review, including nine focused pure
gate checks. Calibration requires all nine observations to match the declared
fault prediction, with exactly six or eight matching correct behavior, plus
successful definition, zero exit, exact responses, no timeout/parse/setup errors
and unchanged projects. Failed preparation cannot receive calibrated status.

Both mutations match the independent model. Including the existing failure-type
source hash resolves the record replacement precondition. No outstanding material
issue was found. Deliberately failing local assertions remain diagnostic evidence,
separate from behavior calibration and library qualification. The reviewer
invoked no runtime; execution receipts must still establish observed counts.

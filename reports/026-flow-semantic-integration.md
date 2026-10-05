# Flow semantic integration

Status: this implementation milestone passed the complete fresh 24-check Release gate. Flow remains opt-in; the complete frontend migration and PRD remain unfinished.

This stage extends the opt-in expression/dot frontend through the existing verified semantic IR. The current implementation work covers six explicitly typed container constructors, exhaustive Option/Result cases, source-origin-aware attached test/example compilation, and advisory local-binding lint. Runtime/default authoring and durable Flow storage are separate remaining requirements.

The foundation and its reports were merged and pushed to main at `87f8fadccf9d65ebf3adcabe35ef3102f055f863`. Hosted CI run [37344386756](https://github.com/benwmaddox/AgentLang/actions/runs/37344386756) completed successfully. Its identity/result is retained in [parent CI evidence](evidence/026-parent-ci-run.json). This proves the committed parent gate; it does not validate the new working-tree implementation.

## Acceptance scope

- Fresh constructor/case tests must cover all six typed constructors, nested nominal/refined payloads, both Option and Result outcomes, branch-local isolation, wrong payload/scrutinee types, malformed cases, conservative effects, authored spans, and exact raw branch obligations.
- Attached compilation must validate the complete source-origin keyset, disjoint private markers and exact compiler snapshot; actual/expected/example bodies must retain authored diagnostic spans. Expected expressions remain independently evaluated and cannot supply tested-word coverage.
- Binding lint must count lexical statements rather than lines, preserve nested scope, warn without editing or rejecting programs, and remain deterministic across formatting changes.
- Focused Flow, IR and lint tests and the complete fresh Release gate must pass before publication; the observed results are recorded below.

## Remaining frontend scope

Static list callbacks, output vectors/destructuring, Flow test/example syntax and whole-project lowering still belong to stage 2. Complete the [durable source integration plan](../docs/FLOW-DURABLE-INTEGRATION.md) after that surface passes. Default CLI/protocol cutover, executable examples, equivalent frozen snapshots and controlled external-subagent trials follow durable conformance. A constructor/case milestone does not complete the frontend migration or the PRD.

## First integration build

The initial fresh `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release` failed with five F# inference errors in the new constructor type-argument helper (FlowLowering lines 163, 164 and 298). An unannotated list was inferred as `SourceSpan` because both expose `Length`; the mapped `Type` field was also ambiguous. The Flow owner is adding explicit `FlowTypeArgument list` annotations before the build is repeated. No semantic acceptance success is claimed by this build attempt.

## Partial validation after the helper fix

The repeated Core Release build passed with zero warnings and errors; exact output is saved in [Core build evidence](evidence/026-core-build.json). The subsequent fresh `dotnet run --project tests/AgentLang.IR.Tests -c Release` passed all 102 assertions, including the new attached-source-origin group; see [focused IR evidence](evidence/026-ir-focused.json). Both runs used the dirty prototype branch based on the published parent above. The later lint registration is not covered by those runs. Focused Flow/lint and integrated validation remain required before publication.

## Independent source review

A read-only review found two existing foundation edges exposed during this extension: postfix dot chains were assembled by a loop without counting their resulting AST depth, allowing accepted source to reach deeply recursive rendering/lowering; and private marker allocation seeded from origin-map count could reuse an index in a sparse map after source-owner removal. The Flow owner is adding actual combined AST-depth checks and collision-free marker allocation with focused regressions. These fixes require a new build and acceptance runs; the earlier partial checks do not cover them.

## Final local integration result

`pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/026-flow-semantic-validation.json` completed with exit code 0 and all 24 required checks passing. The solution build reported zero warnings/errors; Flow passed 122 assertions, IR 102, and lint 42. Existing language, harness, business, source/storage, conventional, discovery, formatter/interpreter and vocabulary suites passed, as did fresh-process persistence, matched fixtures (70), bounded trial host (17), parser process limits (5), and working/staged whitespace checks. The 2,114 task-bank assertions check artifact shape for 60 tasks and 180 proposed vectors; they do not execute those benchmark tasks.

Exact gate output is retained in [integrated validation evidence](evidence/026-flow-semantic-validation.json), with adjacent projection, matched-fixture, host and parser reports. This is a dirty working-tree validation based on the published parent, not a committed CI result. Failed focused attempts are also retained: the first exposed test-compilation errors, the second a shifted fixture index, and the third an invalid one-line effects-metadata fixture suggested during review. Those fixtures were corrected before the final passing Flow run.

The completed-AST parser depth check now rejects overdeep postfix/nested input before recursive consumers. An accepted 128-depth `.abs()` chain renders, reparses, lowers and executes in the focused suite. The sparse-marker regression uses a valid rebased identity definition whose parameter marker actually collides with the former count-based allocator; new markers advance beyond the retained maximum. Source-marker exhaustion returns a structured diagnostic. Library-quality raw Option/Result coverage labels and strongly validated Email payload behavior are verified at compiler/interpreter level; durable Flow library commits remain pending.

Repository visibility was freshly confirmed private before publication. The native/arena/mailbox designs remain research. No new controlled agent trial or productivity improvement is claimed.

## Committed CI and publication

Implementation commit `2a01370e23b1b34ba468232a038ea0d758a442c6` passed hosted [CI run 37352818234](https://github.com/benwmaddox/AgentLang/actions/runs/37352818234). The downloaded validation artifact identifies that exact revision, a clean checkout, Release configuration, and all 24 required checks passing. The run identity is retained in [CI run evidence](evidence/026-committed-ci-run.json); the complete gate output is retained in [committed validation evidence](evidence/026-committed-ci-validation.json).

This report/evidence update accompanies publication of the milestone to main. It changes documentation only; the validated implementation remains exactly the implementation commit above. Default Runtime authoring is still RPN, and no controlled productivity or native-memory result is claimed.

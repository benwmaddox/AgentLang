# Flow renewal experiment preparation

Status: focused checks and complete local Release validation passed. This is fixture preparation,
not a new agent trial or evidence of an efficiency improvement.

The default Flow authoring milestone is now independently verified on published
`main`: CI run [37443572286](https://github.com/benwmaddox/AgentLang/actions/runs/37443572286)
completed successfully on `f3955d5ed98d2f8786ebeb5a7762f9ec745e646e`.
The downloaded report identifies branch `main`, a clean checkout, and all 27
checks passing. Saved [run identity](evidence/053-parent-main-ci.json) and
[validation](evidence/053-parent-main-validation.json) distinguish this parent
evidence from the changes being prepared here.

## Scope and comparison contract

Prepare separate Flow seeds and an executable verifier for
`experiments/AgentLang.SubagentTrials/matched-renewal-001`. Preserve the archived
Stack/RPN seeds and verifier. Flat starts with Customer and Subscription schemas
and generated operations; Growing additionally retains the two customer library
words and seven tests from the earlier pilot. Conventional starts with the same
baseline helpers as Growing. Thus Flat versus Growing investigates retained
vocabulary, while Growing versus Conventional compares representations with
equivalent starting helpers. A single task cannot establish cumulative savings.

The shared task applies the existing baseline customer discount, then a further
5% when the subscription is exactly annual and renewable. That further discount
applies to both premium and nonpremium customers. The renewal solution must be
absent from both language seeds; the conventional placeholder remains visibly
unsolved. Verify the twelve selected finite baseline inputs, seven retained
tests, seed inventories, persistence and reload identity, and the conventional
eight-combination acceptance runner.

Review found an error in the archived conventional oracle: the shared task
requires the extra discount for every annual renewable subscription, but the
standard/annual/renewable case expected 100 rather than 95. The new Flow fixture
uses a separate conventional project with that expected value corrected; the
archived project remains unchanged. Its unsolved baseline-only implementation
must now fail both eligible cases and pass the other six. Treating the old green
fixture verifier as proof of task-contract equivalence would have been wrong.

Translation of earlier agent-created words is host preparation, not an observed
new agent abstraction. This toy fixture uses String/Bool/Float and does not
satisfy the strict Money/Email/ID/time business-domain requirement. A source
fixture alone is not a pinned runtime snapshot. Actual external-subagent trials
still require frozen identities, snapshot/content hashes, independently checked
acceptance results and honest accounting of unavailable model usage.

## Validation and publication

The translated Growing source passed all seven tests and both words committed
with `library:true`; [raw runtime responses](evidence/053-growing-source-review.jsonl)
record that independent check. The separate corrected conventional project
built Release with zero warnings/errors. Its placeholder failed the premium and
standard annual renewable cases and passed the remaining six, as expected.

Reviewed public snapshot bundles now pin Flat manifest
`272b44c7e2a752f75f1554ed00c94c53d7632e7ecfedd82b9af6ade26c268a3e`
and Growing manifest
`480feeaec8a8ad5d860349890bbbe151b2345f7c746f1400e926da68eb181521`.
They contain storage sources/manifests and provider snapshot metadata, with no
binaries, capabilities, credentials or task history. The restore helper checks
the complete file hash/size inventory before writing into a new project.
`Verify-MatchedRenewalSnapshots.ps1` passed **26 checks** for source hashes,
fresh-process manifest and word identity, library maturity, inventories, passing
tests, refusal to overwrite an existing project, and rejection of a modified
bundle before any destination is created. The original 24-check result is
retained separately. See
[final snapshot checks](evidence/053-snapshot-checks-final.json) and
[initial reload responses](evidence/053-frozen-snapshot-reload.json).

The Flow fixture verifier passed **129 checks**, including committed seed
inventories, precise test names, library signatures/documentation/revision and
actual current-process coverage, fresh-process snapshot reload, twelve baseline
results matching the conventional adapter, and the corrected two-failure/six-pass
unfinished renewal fixture. See [focused evidence](evidence/053-flow-fixtures-focused-03.json).
The first two executable attempts failed due to an empty PowerShell array
collapsing to null and a missing metadata-check variable. Both were corrected;
[first failure](evidence/053-flow-fixtures-focused.json) and
[second failure](evidence/053-flow-fixtures-focused-02.json) remain available.
Review also corrected syntax continuation and assumptions about protocol fields
and ensured coverage queries follow tests in the same process.

The full Release gate now includes both historical and Flow fixture verifiers
plus frozen snapshot validation, for **29 required checks**. All passed with
zero build warnings/errors. [The complete local report](evidence/053-local-validation.json)
identifies parent revision `f3955d5` and a dirty working tree; it is not clean
committed-source CI evidence. Sidecars retain the projection, both fixture
verifiers, frozen snapshot verifier and host/parser checks. The task-bank check
still validates 60 proposed task documents and 180 proposed vectors, not 60
executable agent acceptance trials.
Staging revealed that the repository's automatic LF conversion would change
the byte-pinned seed files. Narrow Git attributes preserve bytes for those two
seeds and the snapshot bundles, while retaining whitespace checks with CRLF
recognized as a line ending. All **34 byte-addressed files** have identical raw
and staged Git blob hashes; [the byte audit](evidence/053-staged-byte-audit.json)
records the comparison. Committed-source CI will also exercise a fresh checkout.
Source CI and publication will be recorded after those checks succeed. No native
allocator, mailbox execution or LLVM backend is delivered by this step. The
optional bounded persistent-state plus processing-arena candidate remains in the
PRD and memory research documents; it is not current runtime behavior.

# Retained source review clarification

Terminal report review identified overly broad wording in the root-authored
retained-review.json receipt inside the sealed result archive. Its added-test
finding says "exact cause"; that local test asserts the cause code, not the full
message. The correct finding is: the added later-item test checks index, nominal
missing old ID, cause code and input preservation.

The separate independent nine-case scorer checks the exact BusinessError code
and message. Source preservation acceptance remains valid: no inherited scenario
or assertion was weakened, and original underlying-error checks were preserved.
This clarification changes neither actor source nor the acceptance decision.

The original sealed archive/index remain unchanged (archive SHA256
9ad097b37fcec5b64f6365ae7beafefbc3e15ada30779b7d3889028a6b626d3b).
The discrepancy and correction are retained explicitly rather than silently
rewriting sealed evidence. Report 217 uses the corrected local-test wording.

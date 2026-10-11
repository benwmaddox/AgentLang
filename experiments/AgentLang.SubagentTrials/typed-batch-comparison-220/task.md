Maintain the existing subscription batch API in your assigned typed project.
Inspect current functions and tests before editing, and reuse existing behavior.

Keep the batch's Store, renewal-list and Instant inputs. Change its result error
to a nominal SubscriptionRenewalFailure record with an integer input index,
nominal SubscriptionId old ID and the existing business error as cause. Use the
project's existing naming conventions (index/old-id/cause in AgentLang;
Index/OldId/Cause in F#).

Return the FIRST failing input's zero-based position, old subscription ID and
unchanged exact underlying error code and message. Preserve validation/processing
order. Stop on that error. Successful complete Store results, list order,
unrelated entities and the caller's input must remain intact. Empty input succeeds
unchanged; an intermediate Store is not success.

Update the batch, existing step helper and related internal state as needed.
Preserve their identities/names. Related new helpers/types are permitted. Keep
other domain/renewal APIs, plan enum, input record, tests and examples unchanged.
Keep inherited batch/helper test names, fixtures and scenarios. Necessary result
annotations, typed failure expectations and counter/state construction may adapt;
preserve successful state expectations and underlying causes. Do not remove cases,
replace scenarios or weaken assertions. Add regression coverage for later-item
failure and first-error precedence/localization.

Run relevant tests and the full suite. AgentLang definitions must retain library
qualification; F# must pass the existing fixed local validation project. Finish
with a concise result and close your broker using the provided transport control.

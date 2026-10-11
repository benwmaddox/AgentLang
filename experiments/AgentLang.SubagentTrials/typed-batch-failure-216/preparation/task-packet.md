You are maintaining an unfamiliar typed programming project through its broker.
Inspect its existing functions and tests before editing. Prefer existing behavior.

Change subscription.renew-batch to return Result<Store, SubscriptionRenewalFailure>.
Keep its existing Store, List<SubscriptionRenewal>, Instant inputs. Define:

record SubscriptionRenewalFailure {
    field index: Int
    field old-id: SubscriptionId
    field cause: BusinessError
}

The failure identifies the FIRST failing input item: zero-based input position,
that item's old subscription ID, and the existing error. Keep validation and
processing order, successful complete Store results, unrelated data and input
preservation. Stop at the first failure; an intermediate Store is not success.
An empty list succeeds with the unchanged input.

Update batch, its step helper and internal batch state as necessary. Preserve
existing function identities and publish them as library functions. Related new
helpers/types are allowed. Keep all other existing domain APIs and their tests,
examples and types unchanged, including SubscriptionPlan and SubscriptionRenewal.

Keep all inherited batch/helper test names and scenarios. You may mechanically
adapt result annotations, failure expectations and helper state/counter construction
to the new API. Preserve input fixtures, expected successful state and underlying
error causes. Do not remove cases, replace scenarios or weaken assertions. Add
meaningful regression coverage for later-item failure and first-error precedence.
Run relevant tests and the full suite; complete the task and close the broker.

Use help to discover syntax/commands. Do not inspect siblings, reports, hidden
oracles or other agents. No external effects are granted. All discovery, edits,
tests and commits use your assigned broker after the authorized packet read and
launcher. Disclose any other tool use in your final response.

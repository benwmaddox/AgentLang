# Successor task packet

You have a fresh context and the accepted creator project. Add and publish one pure library operation with this interface:

```text
enum SubscriptionPlan {
    case monthly
    case annual
}

record SubscriptionRenewal {
    field old-id: SubscriptionId
    field new-id: SubscriptionId
    field plan: SubscriptionPlan
    field expires-at: Instant
}

subscription.renew-batch(store: Store, changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store, BusinessError>
```

Use `SubscriptionId`, `SubscriptionPlan`, and `Instant` for those fields; do not replace them with strings or integers. For each item, end the old active subscription at the common `at`, then append its replacement for the same customer and product starting at `at`, with the item's expiry and the plan's lowercase value (`monthly` or `annual`). Process items in list order against the Store produced by earlier successful items. An empty list returns the input Store unchanged. On success, preserve subscription order and all unrelated Store data. On failure, return the first error encountered in list order. The result type carries a Store only on success, and the caller's input remains unchanged on all paths.

Add meaningful tests for empty, single-item, multi-item, and failure behavior. Publish the operation as library vocabulary. Preserve every definition, type, attached test and example already in the project, including the creator's work. Do not rename, replace, or edit inherited source or attachments. Keep this task within the assigned project and broker; do not contact, inspect, or delegate to another agent. At completion, report what you changed, which tests and commits you ran, and any limitation.

Use the runtime's current Flow/2 guidance before authoring. Request `help` for the `define` and `examples` topics with `syntaxVersion: 2`. New Flow source uses dotted calls and newline separators; keep names and types in this packet exact. The seed's existing subscription operations and examples are useful references.

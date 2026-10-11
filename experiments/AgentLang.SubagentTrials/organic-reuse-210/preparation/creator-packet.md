# Creator task packet

You have a fresh AgentLang project and one assigned broker. Add and publish these two pure library functions:

```text
subscription.renew-monthly(store: Store, oldId: SubscriptionId, newId: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError>
subscription.renew-annual(store: Store, oldId: SubscriptionId, newId: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError>
```

Each function ends the active subscription identified by `oldId` at `at`, then starts a replacement with `newId` for the same customer and product, beginning at `at` and expiring at `expiry`. The monthly function stores the fixed term `monthly`; the annual function stores the fixed term `annual`. Preserve the existing cancellation and subscription-start validation order and error codes. If starting the replacement fails, return that error; do not return a successful Store containing only the cancellation. The supplied Store remains unchanged on every path.

Document both functions, add meaningful tests for the new public behavior, and publish both functions as library vocabulary. Preserve every definition, type, attached test and example already in the project. Do not rename, replace, or edit inherited source or attachments. Keep this task within the assigned project and broker; do not contact, inspect, or delegate to another agent. At completion, report what you changed, which tests and commits you ran, and any limitation.

Use the runtime's current Flow/2 guidance before authoring. Request `help` for the `define` and `examples` topics with `syntaxVersion: 2`. New Flow source uses dotted calls and newline separators; keep names and types in the signatures above exact. The seed's existing subscription operations and examples are useful references.

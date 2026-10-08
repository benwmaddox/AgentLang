# Coordinator review before dispatch

The coordinator reviewed seed, prompt, broker, runtime pin and verifier before any actor was launched. The task does not reveal the missing false output, unreachable None output, or mutation bodies.

Required pre-dispatch corrections:
- Compare attachment snapshots captured before and after mutation, rather than comparing the post-mutation snapshot to itself.
- Filter durable attachments to active revisions.
- Exercise library qualification in the one-off-diagonal control; project replacement alone is not evidence that a mutant passes the library gate.
- Use Flow/2 plain `.first` properties in independent Selection evaluations.
- Smoke-test scoring against the known complete control before freezing it.
- Keep literal test-source matching descriptive only. It does not establish execution coverage and is not task acceptance.
- Do not require the actor to add specific boundary tests or all four literal Boolean calls for basic task success. Independent behavior, published maturity, preserved contracts, passing own tests and an honest explanation define acceptance. Mutation resistance is a separate measurement.
- Preserve blank lines between function metadata and code in new fixtures.

These are research-harness corrections, not actor errors or product implementation changes. Final preflight evidence must demonstrate the corrected checks before dispatch.

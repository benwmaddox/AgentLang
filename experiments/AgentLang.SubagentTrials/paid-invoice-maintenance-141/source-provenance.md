# Source provenance

The accepted seeds are report 135 replica-two actor outputs, not the earlier
R09 starts and not the reset-language output. Original participant and accepted
source artifacts remain untouched.

| Arm | Accepted source root | Important files | SHA-256 |
| --- | --- | --- | --- |
| Retained Flow/2 | `.agentlang/r09-discovery-001/comparison/participant-work/replica2/flow-retained/project` | `dictionary.agent` | `ef29e62d0a8d0ad3973e0f39089ad3e0daf179a1aaed57bf236d86a09fd3cba9` |
| Conventional F# | `.agentlang/r09-discovery-001/comparison/participant-work/replica2/fsharp/project` | `business/Business.fs` | `abe1ca41c0e20b6fa3d89f871295a72352446bdd22d4fbff1ba60e95dbad03f6` |
| Conventional F# | same | `tests/Program.fs` | `d428cccceaffab367c9f8a297c8087d87ae04adb13c48e946e9fe282205c64a7` |

The accepted retained helper is `customer.payment-total`, Flow word ID
`word_65cacd4cdfc3480c9ada5756f71aba09`, revision 2, and both summaries call
it. It delegates to the earlier agent-created `customer.paid-total`, word ID
`word_9aeab7858ed34909813e745aff22cfe8`, revision 1, whose fold uses
`customer.paid-total-step`, word ID
`word_b58bd8df52c8451c848879e1899ec73f`, revision 2. The source snapshot's
Flow current manifest is generation 16, hash
`a151b99c0c245d5ef95a93db7bab7106b77cfb3b9c220f4e4f3e7b961e964fc1`.

In F#, `Store.customerPaidTotal` is the coordinator-supplied conventional
equivalent in report 135. `Store.customerPaidTotalStep` performs the payment
fold; `Customer.accountSummary` and `Store.customerMetrics` both call
`Store.customerPaidTotal`. `Store`, `Invoice` and `Payment` have private record
representations. Their normal public operations cannot create a payment linked
to an Open invoice or a missing invoice, so a narrowly scoped fixture builder
must be present in each frozen study copy for equivalent defensive tests.

Report 135 source freezes and actor evidence are recorded in
`.agentlang/r09-discovery-001/comparison/replica2-input-freeze.json` and
`.agentlang/r09-discovery-001/comparison/participant-evidence/replica2/`. The
relevant source owners/final claims are retained at
`participant-evidence/replica2/flow-retained/actor-final.md` and
`participant-evidence/replica2/fsharp/actor-final.md`.

The frozen study copies are recorded in `start-inventory.json`. Flow's final
`dictionary.agent` SHA-256 is
`f7a8f0344d557be34e7b2f30468ac99d7023a51ef9739374db423ae11237f052`; it
contains the coordinator fixture installed with the retained CLI define/commit
protocol (`library=false`). A fresh CLI load finds the fixture and passes
191/191 attached tests. The F# `Business.fs` SHA-256 is
`fef9e58c08adcb533fb164b9299703687ed866e1a32543a1cd27b328366f8a69`; its
only study scaffold change is `Store.Maintenance141Fixture.importPayment`. The
inherited F# tests remain unchanged at
`d428cccceaffab367c9f8a297c8087d87ae04adb13c48e946e9fe282205c64a7`. Both
helpers are frozen coordinator test scaffolding and must not be used from
production definitions.
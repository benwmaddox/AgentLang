# Campfire implementation

The active deliverable is a complete Campfire migration implemented in the language
using LLVM AOT, with measured endpoint performance compared to reported Campfire workloads.
Language/runtime additions are driven by the next native application requirement.
This supersedes the earlier scheduling of Campfire as a final research exercise.
It does not establish application readiness. Native release execution, arena allocation
and mailbox behavior must be verified on the application. The interpreter remains
a reference. Follow the [AOT delivery plan](CAMPFIRE-AOT-PLAN.md) before expanding
interpreter-only routes.

The owning AOT compiler now has an explicit profile for the existing typed room
lookup, with per-execution native provider state and direct arena-owned output.
See [report 248](../reports/248-typed-native-room-host.md). The subsequent
[native SQLite adapter](../reports/249-native-sqlite-room-adapter.md) performs the
real membership-filtered room lookup through the AOT entry without .NET at runtime.
Other native SQL contracts and real HTTP still need implementation and parity checks.

The HTTP slice (room listing, message creation, login page/submission and logout)
is a delivery milestone, not the final
scope. Completion still requires the full application's observable behavior,
existing-data migration, compatible cookies/assets/live protocols, deployment
and native runtime work described below. Where a requirement appears infeasible,
record the specific evidence and migration consequence; do not substitute a
smaller chat demonstration for the requested application.

[The route inventory](campfire-route-inventory.json) retains all 177 rows from
the pinned Rails route vectors, including aliases and possibly absent reference
actions. It is a compatibility checklist, not 177 implemented features or a
percentage-complete measure. Rows require executable evidence before being
marked validated; live protocols, assets, jobs, stored files and deployment also
need separate acceptance evidence. The current five base route rows are in progress
pending broader compatibility; GET/HEAD login and POST login now pass a focused
302-assertion local HTTP suite. See [report 244](../reports/244-campfire-public-login-http.md).

## Reference and compatibility

Initial Rust reference: `https://github.com/basecamp/once-campfire-rust`, revision
`6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`, downloaded into the ignored
`.agentlang/campfire-reference` directory. Preserve that pin when evaluating a
behavior; upstream changes require a deliberate reference update. The reference
repository's Rails submodule and documented deliberate divergences determine the
remaining compatibility investigation. Do not claim Rails parity from Rust source
inspection alone.

The Rails reference is checked out at
`90b330024dec3e757c79b6a7e6568f93da8e3148` under that reference directory.

Maintain existing SQLite data, storage layout, signed/encrypted cookie formats,
browser assets and Action Cable protocol behavior. Record any deliberate departure
with tests and a migration consequence. Use established cryptographic and transport
libraries through narrow trusted adapters; application authorization decisions
remain authored and inspectable language code.

## Delivery sequence

1. Port message administration and room-access decisions with nominal IDs, closed
   roles, exhaustive matches and library coverage. Test denied cases explicitly.
2. Connect authenticated room message listing/creation to actual SQLite transactions
   and request handling. Keep session/CSRF verification before state changes.
3. Render compatible responses and deliver live updates over Action Cable-compatible
   WebSockets. Preserve message ordering and commit-before-broadcast behavior.
4. Expand room/user/session management, search, attachments, bots, notifications,
   jobs, assets and deployment behavior against the pinned application's inventory.
5. Complete native application lowering and arena/mailbox integration, then measure
   real sustained workloads, including pending I/O, cancellation and slow clients.
6. Run full application parity and migration checks, plus later-agent maintenance
   tasks. Compare correctness, throughput, tail latency and whole-process memory;
   report failures and compatibility limits alongside successes.

Each component should have runnable local validation and a saved report. Push and
merge completed milestones together with their reports. Pure policy tests, simulated
storage, individual native kernels and loopback load tests are component evidence;
none alone proves a complete Campfire implementation. Remaining missing capabilities
stay visible rather than being replaced with opaque host-side application logic.

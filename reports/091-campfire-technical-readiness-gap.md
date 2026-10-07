# Technical prerequisites for a Campfire migration

Assessment, 2026-10-07. Proposed late-stage roadmap, not new V1 scope or a claim
that the interpreter can currently host this application.

## Current foundation and verified gaps

AgentLang has strong typed values, records, containers, a verified semantic IR,
an interpreter, dictionary inspection, persistence, tests and initial library
coverage gates. The richer frontend and library requirements in report 085 are
still planned.

The implemented guest effects are virtual text-file operations, a fixed clock
and captured console output. Database/network/process effect names exist, but
real guest adapters do not. Engine has no provider-injection constructor inputs.
Disk dictionary storage is host control-plane persistence, not guest filesystem
access. Public values and verified IR types lack bytes/buffers and opaque resource
handles. Native compilation, arenas, lifetime checking and mailbox execution
remain unimplemented. Sources: `Core.fs:24`, `TypedIR.fs:68`,
`IrInterpreter.fs:9`, `Runtime.fs:226`, `docs/MEMORY-REGIONS.md`, `docs/BACKENDS.md`.

## Application reference

The [Rust repository](https://github.com/basecamp/once-campfire-rust) documents
SQLite compatibility, WebSockets, security/session handling, uploads, background
work and external media dependencies. Its
[conversion plan](https://github.com/basecamp/once-campfire-rust/blob/main/plans/rust-conversion.md)
proposes retaining browser assets and checking behavior against Rails. Current
README differences mean the proposal is not itself the current behavior contract.
Pin both references and choose which behavior the candidate must match.

The [dependency manifest](https://github.com/basecamp/once-campfire-rust/blob/main/Cargo.toml)
identifies networking, SQLite, templates, cryptography and media integration
as substantial support layers. The [database implementation](https://github.com/basecamp/once-campfire-rust/blob/main/crates/db/src/database.rs)
has a bounded writer queue and separate readers, making it a useful concrete
mailbox workload. These source observations are not reproduced benchmarks.

## Proposed capability boundary

| Layer | Required work |
| --- | --- |
| Language and IR | Complete approved authoring/library rules; add typed binary data and scoped resource handles; specify layout, errors and lifetimes shared by all backends. |
| Native execution | Lower verified IR to native code, define a stable trusted adapter ABI, and validate interpreter/native conformance. Native AOT is necessary for the proposed release-runtime comparison; JIT need not gate the first native application slice. |
| Memory | Implement the selected arena/data-stack model; distinguish scratch, pending I/O, connection state, queues, retained application state and outgoing buffers. Reject unsafe escapes and enforce capacity/cleanup contracts. |
| Scheduling | Typed mailbox messages, single-thread handler rules, nonblocking I/O completion, bounded queues, cancellation, deadlines and shutdown. Decide whole-process versus per-mailbox single-threading explicitly. |
| Trusted adapters | HTTP and WebSocket transport, SQLite transactions and typed parameters/results, files/uploads, clock/random, established crypto/TLS libraries, and constrained media processing. Each has declared effects, capabilities and deterministic test providers. |
| Authored vocabulary | Routing, authorization, session policy, room/message behavior, rendering, search and job policy implemented as inspectable tested functions above the adapters. |
| Compatibility | Independent protocol/data/browser parity, security assertions, regression/fault testing, rollback compatibility where required and reproducible operational workloads. |

Keep trusted primitives narrow. Wrapping the whole application behind one opaque
host operation would obscure whether authored vocabulary works. Conversely,
rewriting cryptographic algorithms, SQLite, TLS or codecs in the new language
would expand the experiment far beyond its purpose. Curated host libraries
remain compatible with a compact language core; their memory and CPU costs must
be included in the complete-system comparison.

An arena for each request can survive I/O waits until safe completion, or a
mailbox can release scratch after each processing turn and explicitly retain
only continuation state. Neither permits resetting memory while an I/O provider
still references it. See [async memory comparison](092-async-memory-runtime-comparison.md).

## Shortest useful migration sequence

First migrate one small server slice while retaining existing browser assets
and transport infrastructure: authenticate, list room messages, post a message.
Then add native mailbox execution and allocation measurements, live broadcast,
and prolonged operation; expand toward uploads, jobs and the full compatibility
surface after each gate passes. A hosted/interpreted slice can test language
usability but cannot establish native footprint or whole-application performance.

Use fresh external agents for subsequent fixes and features. Freeze acceptance,
workloads and optimization allowances, preserve failures and track host-core
growth. The meaningful result includes maintenance reliability and sustained
operation, not merely a successful initial translation.

No implementation, migration or performance test was executed for this report.

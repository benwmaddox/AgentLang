# Async memory and arena research

Discussion assessment, 2026-10-07. Design recommendations, not implemented
AgentLang semantics or a performance result.

## Established runtime approaches

Rust async functions produce state-machine futures. Borrowed inputs must remain
valid for the future's lifetime; moving inputs into an async block transfers
ownership. Tokio's ordinary spawned tasks require `Send + 'static`. Rust async
does not by itself imply either heap allocation or heap-free execution.
Sources: [Rust async primer](https://rust-lang.github.io/async-book/01_getting_started/04_async_await_primer.html),
[async lifetimes](https://rust-lang.github.io/async-book/03_async_await/01_chapter.html),
[Tokio spawn](https://docs.rs/tokio/latest/tokio/task/fn.spawn.html).

C# also lowers async code to a state machine. Ordinary task-based execution
that suspends preserves that state on the managed heap, including necessary
locals. Synchronous completion and specialized/pooling mechanisms affect
allocation; not every await implies a new allocation. Reachable managed objects
remain live until no longer rooted; reclamation is GC-managed, not arena reset.
Sources: [Microsoft async internals](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/),
[GC fundamentals](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals).

Elixir uses BEAM processes. Erlang's runtime documentation describes process
heap/stack storage and garbage collection, and ordinary cross-process message
copying with exceptions for reference-counted binaries and literals on the same
node. A long-lived process does not reclaim all memory after each message.
Elixir Tasks run work in separate processes and communicate results.
Sources: [Erlang process efficiency guide](https://www.erlang.org/doc/system/eff_guide_processes.html),
[Elixir Task](https://elixir.hexdocs.pm/Task.html).

## Implications for AgentLang

Data needed after I/O completion needs storage that remains valid until then.
Moving the program-data stack into an arena does not remove this requirement.
Single-thread execution eliminates simultaneous handler execution, but still
allows several suspended operations to retain memory.

Two candidates should be measured:

1. A request owns its arena across suspension until completion. This makes
   sequential async code convenient, but waiting retains arena allocations.
   Completion includes safe handling of outstanding operations and exported
   results; cancellation alone is not proof an I/O provider released a buffer.
2. Each mailbox processing turn owns a scratch arena and runs to completion.
   Before waiting, it copies required continuation data into separately bounded
   pending-state/message storage. A later completion message enters a fresh
   arena. This permits bulk cleanup per turn, but requires explicit state
   transitions and may incur copying and additional messages.

The second is a promising fit for a compact mailbox runtime, not an adopted
requirement. The first may be simpler or faster for some workloads. Compare
memory, copies, latency, cancellation behavior, queue pressure and agent
comprehension. Do not assume either is automatically lower-memory.

Distinguish process/connection state, queued messages, pending operations,
processing scratch and outgoing buffers. Checked transfers prevent surviving
references into reclaimed scratch. Socket/file/transaction cleanup needs its
own contract; bulk memory reclamation does not close those resources or undo
external effects. Third-party host allocations also belong in system accounting.

Possible practical direction: deterministic single-thread handlers, nonblocking
host I/O, typed completion messages, explicit retained state and bounded queues.
Whole-process single-threading versus single-thread execution per mailbox is a
separate choice, with capacity and throughput implications to test.

See [memory region contract](../docs/MEMORY-REGIONS.md),
[stack-only research](../docs/STACK-ONLY-RESEARCH.md) and
[late Campfire study](088-late-application-migration-research.md).

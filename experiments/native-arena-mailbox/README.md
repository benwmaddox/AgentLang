# Native arena and mailbox probe

This standalone C11 experiment compares per-turn scratch release (`turn`) with
keeping scratch for the whole suspended request (`request`). It uses Win32
`VirtualAlloc`/`VirtualFree`, checked generation handles, typed graph promotion,
and a single-threaded deterministic mailbox scheduler. C is the probe's
implementation choice; this does not choose a production AgentLang runtime or
change the F# frontend, interpreter, verified IR, or LLVM backend.

Build from the repository root with the installed Windows Clang and SDK:

```powershell
$clang = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
& $clang -std=c11 -Wall -Wextra -Werror -O0 experiments/native-arena-mailbox/probe.c -o probe-O0.exe -lpsapi
& $clang -std=c11 -Wall -Wextra -Werror -O2 experiments/native-arena-mailbox/probe.c -o probe-O2.exe -lpsapi
```

Run the native safety checks with `probe-O0.exe --self-test`. A workload run
requires all of these options:

```powershell
.\probe-O0.exe --mode turn --scenario delayed-small --seed 17 --requests 32 --budget-bytes 8388608 --process-limit-bytes 67108864
```

`--mode` is `turn` or `request`; `--scenario` is `cpu`, `delayed-small`,
`delayed-large`, `slow-output`, `cancellation`, or `retained-only`. Workload
commands print one JSON object to stdout. The safety command accepts an optional
`--process-limit-bytes` value and defaults to 64 MiB.

The scheduler offers one request per logical tick and maps request `id` to
mailbox `id % 8`. Each mailbox has an eight-message FIFO queue, a 4096-byte
queue limit, and at most one pending provider operation. A blocked mailbox
queues later messages until the provider acknowledgment resumes its pending
request; the scheduler advances to the next due logical tick. These ticks model
deterministic waiting only. The probe has no network or real asynchronous I/O.

| Scenario | Logical delay | Retained graph body | Provider buffer | Output buffer | Disposable scratch |
| --- | ---: | ---: | ---: | ---: | ---: |
| `cpu` | none | 1 KiB | none | 64 B | 512 KiB |
| `delayed-small` | 24 ticks | 128 B | 1 KiB | 64 B | 512 KiB |
| `delayed-large` | 40 ticks | 8 KiB | 4 KiB | 128 B | 512 KiB |
| `slow-output` | 24 ticks | 128 B | 512 B | 4 KiB | 512 KiB |
| `cancellation` | 24 ticks | 512 B | 4 KiB | 64 B | 512 KiB |
| `retained-only` | 40 ticks | 8 KiB | 4 KiB | 128 B | none |

In `turn` mode, the probe promotes the retained graph to a continuation arena,
then releases scratch before suspension. Resume acquires fresh scratch, checks
the retained graph, and retires both owners. In `request` mode, scratch remains
owned until resume completes. Both modes perform the same volatile byte writes
and reads over disposable scratch before suspension. Provider bytes remain
owned until provider acknowledgment; output bytes remain owned until consumer
acknowledgment. A cancellation request releases no buffers by itself. The
cancellation scenario cancels exactly IDs divisible by three.

The frozen comparison uses fresh Clang 19.1.5 builds at O0 and O2, three
repetitions, 32 offered requests, seed 17, both modes, and all six scenarios
(72 workload runs). The mode order alternates by repetition. It charges an
8 MiB application arena budget, including fixed metadata and cached backing;
the shared cache holds at most 64 chunks of 16 KiB (1 MiB). Each mailbox has a
128 KiB output-byte limit. The Windows Job Object enforces a 64 MiB process
commit limit and the probe checks that an over-limit allocation is rejected.
Memory-capacity rejections are failures for this non-pressure comparison, not
successful completions.

The JSON reports copy bytes, used arena bytes, charged backing and cache peaks,
allocator/free calls, provider/output ownership, drain state, and timing.
`arenaReservedBytes` and its peak count 16 KiB charged arena chunks, including
cached chunks; they do not report the virtual-address extent Windows reserves
for those allocations. `privateBytes` and `workingSetBytes` are sampled after
drain, while `processPeakCommittedBytes` is the Job Object's peak. Elapsed
milliseconds are derived from QPC and are probe time, not server latency or
saturated-server throughput.

The full preregistered comparison and JSON evidence are produced by:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Verify-NativeArenaMailbox.ps1
```

The evidence and temporary executables go under the ignored
`.agentlang/arena-probe/` directory. This probe can test checked handles,
bounded promotion, ownership acknowledgments, cleanup, and backing-footprint
behavior for these fixed handlers. It does not establish language-wide escape
analysis, general container promotion, strict LIFO allocation, or production
throughput.

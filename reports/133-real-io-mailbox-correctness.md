# 133 — Real-I/O mailbox ownership and cancellation

Status: bounded real-I/O correctness comparison passes at O0/O2 under both
arena policies. No throughput comparison is claimed. Starting revision: `5a91168`.

Final run `463112b53ca945748dd326c06f52f580` passes all seven cases under each
of RETURN/O0, KEEP/O0, RETURN/O2 and KEEP/O2. The 53,827 verifier checks include
repeated field and fragmented-receive event checks, not 53,827 independent
scenarios. All four native processes exit zero, exact normalized state traces
agree, and all 85 recorded source hashes match the current files. The provider
stops gracefully: 48 accepted connections, 44 completed responses, four I/O
failures in the intentional cancellation windows, and no other failure category.

Both policies preserve typed state across real socket completions, cancellation,
and failed-resume retry in this bounded driver. KEEP remains attached until
terminal acknowledgement; cancellation then releases its slot. These results
support continuing the native runtime work, without selecting a throughput winner.

This step connects the two native mailbox arena policies to actual loopback
socket completions. The language fixture uses fixed-width attempt/completion
counters and replaces its latest response, avoiding report 132's growing
transcript. Seven independently specified cases cover Unicode/NUL, empty text,
repeated replacement, cancellation, failed-resume retry, two pending mailboxes,
and a 4 KiB request plus 4 KiB completion. Exact expected values were frozen
before the first LLVM-generated real-I/O fixture execution.

Terminal cancellation preserves the State produced by Begin and retires its
Continuation. RETURN trims retained metadata without changing payload bytes;
KEEP publishes the attached State before releasing its arena. KEEP Begin checks
that State can fit retained storage so an admitted valid operation can cancel.
The host must wait for I/O completion or cancellation acknowledgement before
calling this API; the runtime cannot infer kernel ownership of host buffers.
This follows the distinction between requesting cancellation and completing it
in [Microsoft's I/O cancellation contract](https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations).

The native driver uses Windows overlapped receives and an I/O completion port.
The separate F# provider echoes bounded length-framed bytes and deliberately
fragments responses. The first sandbox protocol probe was denied loopback
access; host-side execution is needed for the integrated socket check.
The driver handles partial data and terminal receive notifications under the
[WSARecv contract](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsarecv).

The current native runtime and driver retain correctness instrumentation and
diagnostic snapshots. Setup includes synchronous connection/request sending.
Their elapsed time and reserved arena bytes do not establish service throughput
or total process memory. A release reset path, sustained-load driver and matched
F# application remain outstanding.
On an unexpected driver failure, outstanding static I/O buffers stay alive until
process teardown; the failure path is not a reusable production host shutdown
implementation. Successful acceptance runs explicitly drain their operations.

Agent efficacy remains a separate question: report 125 demonstrates successful
edits and reuse in its small comparison, without a reliability lead over F#.

Initial integrated attempts stopped before native execution. The verifier first
wrapped a JSON scalar in a collection (`e940ea90c39e495599589b9a76ccf7c7`), then
launched the bootstrap DLL without `dotnet`
(`701cb5c7cc754d8abcf88087c818dab8`). After those harness corrections, both LLVM
modules compiled, but the native link command treated `ws2_32.lib` as an input
file rather than a library lookup (`1c37b289fa7a49328fe1b84c61f5ff77`). The command
now uses `-lws2_32`, matching the standalone host build. Each attempt retained
its process logs and failed evidence report; the frozen expected values remain
unchanged. The third attempt's provider stopped cleanly with no accepted requests.

The fourth attempt (`e9e9048d6e27412ea9055a91886f1eec`) executed all four native
variants with exit zero. Its overall gate failed: I/O events did not capture
their registered mailbox names, and assertion-only token checks emitted values
into the verifier's normalized result collection. Exact state/status checks
passed, but this attempt is not an acceptance pass. The provider recorded 48
accepted connections, 44 completed responses and four I/O failures associated
with the intentional cancellation windows, with no timeout, invalid frame,
busy rejection or internal error.

Fresh existing regressions pass with the cancellation API: policy run
`581fd92bc9e84e3490ee325650248c81` passes 234 checks; ordinary mailbox run
`0f05ab6996f3431ebe40f6963d5159b0` passes 198 checks. These are separate from
the new real-I/O acceptance gate. The full Release and LLVM suite results in
report 132 precede this change and are not presented as a fresh report 133 run.

Direct cancellation component tests also pass at O0, O2 and trap-based undefined
behavior instrumentation for both the mailbox controller and retained bank.
They cover wrong-thread rejection, failed validation/publication preserving the
pending operation, and repair followed by same-token cancellation. Deliberately
corrupt host metadata is a defensive boundary test, not a normally reachable
language operation. RETURN cancellation's metadata trim does not rewrite the
retired payload bytes.

Reproduce the integrated check locally on Windows with loopback access:

```powershell
pwsh -NoProfile -File scripts/Verify-OwningMailboxRealIo.ps1
```

The [evidence archive](evidence/133-real-io-mailbox/133-real-io-mailbox.zip) contains
333 payload files plus its manifest: final source snapshots, generated IR and
manifests, captured process output, earlier failed attempts and focused
regressions. Every archived payload hash was verified after compression.
Compiled binaries/dependency build trees are excluded, and earlier failed-attempt
source revisions are not reconstructed. See the
[storage index](evidence/133-real-io-mailbox/storage-index.json).
Archive SHA-256: `a94c8a406267efdfc3dacd7649fd9899ccdfefe15aaa43835a72e5732cabbf59`.

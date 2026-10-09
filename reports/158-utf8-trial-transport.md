# UTF-8 trial transport

The broker and both CLI processes now explicitly select UTF-8. An actual
Windows terminal probe starting with code page 437 preserves `Ω π 🌿 配置`
exactly through the broker and language runtime. Redirected-pipe regression
tests also preserve non-ASCII request values, conventional file paths and
contents. The broker still rejects malformed UTF-8 before dispatch and accepts
a subsequent valid request.

This addresses transport friction observed in report 156. It is not a new
participant trial, and establishes no improvement in agent reliability, reuse,
turn count or task duration. Report 156's original observations remain intact.

## Reproduction and change

The broker reads raw standard-input bytes with a strict UTF-8 decoder. Under a
legacy Windows console input code page, terminal characters became OEM bytes
before that decoder saw them. Pinning only the broker fixed the rejection but
exposed a second fault: the child CLI decoded the valid UTF-8 pipe using its
legacy encoding. Success without checking the exact value would have missed
this corruption.

Three preserved terminal traces show the progression:

| Stage | Outcome |
| --- | --- |
| Baseline | `TRIAL_INVALID_UTF8` |
| Broker encoding only | Successful response containing corrupted text |
| Broker and CLI encoding | Exact original Unicode value |

All three sessions closed through `host.close` with exit code zero. Their
decoded comparison is saved as `pty-comparison.json` in the evidence archive.
Both language and conventional CLI entry points now select UTF-8 input/output.
The conventional command handler is separate from process startup so its
in-process tests can retain their injected readers and writers.

## Local validation

- AgentLang CLI: 11 groups, 149 assertions passed.
- Conventional CLI: 9 groups, 344 assertions passed.
- Broker focused verifier: 31 checks passed, including exact Unicode through
  the real CLI and rejection/recovery after an invalid byte sequence.
- Accepted Release builds completed with zero warnings and errors.
- Diff checks passed; CI remains manual-only.

Fresh Release builds use `-m:1 -p:NuGetAudit=false`. Executable test runners
use workspace-local TEMP/TMP. The broker command is
`scripts/Verify-SubagentTrialHostV2.ps1 -CliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll`.
The two CLI test runners are their Release `net9.0` DLLs.

The first language CLI test build had an F# nested-indexer syntax error,
corrected with an intermediate binding. The first conventional runner failed
because encoding initialization reset its injected input reader; separating
startup from command handling fixed that failure. The first broker verification
selected Stack syntax implicitly while requesting syntax version 2. Explicit
Flow selection fixed the fixture, and response inspection now guards against
unsuccessful replies. Failed attempts are retained alongside accepted evidence.

No parser, type/effect semantics, semantic IR, native ABI or arena policy changed.
The complete native/business gate was not repeated for this transport change.
Real I/O adapters and user-authored dictionary overrides remain separate work.
The next efficacy comparison should use these corrected startup paths rather
than infer agent improvement from coordinator transport checks.

The [evidence manifest](evidence/158-utf8-trial-transport/manifest.json) records
26 archive entries, 50,858 bytes and SHA-256
`c257ea23e6f9594177f2752f94a0b2cf9cc1afc18000bc5e29d6d823b03c695f`.

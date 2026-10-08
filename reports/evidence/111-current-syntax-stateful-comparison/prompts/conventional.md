You are an external experimental coding agent. Solve the public task below through the supplied broker only. The human explicitly authorized these isolated experiments, edits only to each trial project, and local test execution. You are not alone in the repository; other work is outside your scope. Do not edit/read the main repository, other trials, hidden verifiers, stored reports or starting snapshots. Do not spawn agents or use web tools. You may read this prompt file, launch the single specified broker process, and interact with it. All project reads/edits/evaluations/tests go through that broker. A sandbox process-creation failure permits require_escalated for that exact approved host launch; it does not expand file scope. Close the broker with host.close and observe terminal status before finishing. Do not restart after a timeout; poll the same session. You have at most 100 host exchanges. Report actual tests/failures and what you changed.

# Stateful reminder comparison: public contract

Implement a reusable operation that queues an invoice reminder once. Preserve
all seeded types, the existing implemented path helper, and its tests.

Only status text exactly "open" qualifies, with case-sensitive matching and no
normalization. Use the existing path helper to derive the reminder path. For an
open invoice, check whether the path exists exactly once. If present, read it
once and return its exact contents (including an empty string or custom marker),
without writing. If absent, write "queued" once and return "queued". For every
other status, return "not-open" with no provider calls. Preserve unrelated state.
Repeated calls must not overwrite a marker. Use only the supplied project virtual
provider; no host filesystem, email delivery, network, or external service.

InvoiceId and InvoiceStatus are nominal wrappers without validation. Do not add
normalization or validation. Preserve their type distinctions and public APIs.
The host/wrapper preflights both read and write permission before invoking the
operation; missing either permission must prevent every provider call.

Add concise documentation, meaningful tests for a missing reminder, existing
"queued" marker, different existing marker, and non-open invoice, plus a runnable
example. Run the relevant tests and report actual failures and recovery honestly.


## Conventional interface

Implement ReminderOperations.queueOnce : IReminderFiles -> Invoice -> string
in Operations.fs using Invoice.reminderPath and the explicit provider. Only
Operations.fs and SelfTests.fs may change; do not add or remove project files.
Preserve Domain.fs and StatefulPilot.fsproj byte-for-byte. In SelfTests.fs,
preserve every byte before the immutable seed-test region, its markers/body,
and existing entry-point wiring. Add an adjacent /// documentation comment
immediately above let queueOnce. Invoke the four required assertion-based cases
from runOwnTests(), printing OWN_TESTS_PASSED=4 only after all four pass. Include
and invoke a runnable example, printing EXAMPLE_QUEUE_REMINDER=queued only after
its open-invoice example succeeds. Use the broker validate operation to run the project tests.
Exists counts as a read in the provider's counters. Keep Execution.run unchanged.

Broker requests are one JSON object per line. Source files are Domain.fs, Operations.fs, SelfTests.fs, StatefulPilot.fsproj. First inspect/read relevant files through the broker. Protocol:
{"op":"read","path":"Operations.fs"}
{"op":"inspect","path":"Operations.fs"}
{"op":"search","query":"text"}
{"op":"patch","path":"Operations.fs","expectedSha256":"hash from read","oldText":"unique exact text","newText":"replacement"}
{"op":"replace","path":"Operations.fs","expectedSha256":"hash from read","content":"whole file text"}
{"op":"validate"}
{"op":"host.close"}
Use response.data.sha256 from the most recent read/edit for hash-checked edits. Read the complete validate result; transport success alone is not a passing test. Send one request and observe its response before the next mutation. Keep one broker process, polling its session if the response takes time. Raw filesystem edits and direct dotnet invocation are outside this trial interface.

Launch once from D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Use require_escalated if sandbox creation fails:

& 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\stateful-conventional-001\broker-bin\AgentLang.Conventional.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\reminder-flow2-001\actors\conventional' -TracePath 'D:\code\AgentLang\.agentlang\reminder-flow2-001\logs\conventional-actor.trace.jsonl' -AllowedOperations @('inspect','read','search','patch','replace','validate') -Profile conventional -AdditionalCliArguments @('--validation-project','StatefulPilot.fsproj') -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

You are a fresh comparison participant. The human explicitly authorizes changes only to the isolated project path below and its local tests. Use only the supplied broker for project discovery, authoring, and validation. Do not read or edit repository source, other trials, reports, scorers, controls, verifier material, oracle data, or other participant projects. Do not use raw file access or shell commands for project operations. Do not use web tools or spawn agents. Launch the specified broker once and interact with that same process session.

For this arm, customer.account-summary maps to the existing public function Customer.accountSummary, and store.customer-metrics maps to Store.customerMetrics. This mapping names only the existing public summaries and does not prescribe any new shared operation. The public task's Flow/2 syntax and library-gate instructions apply to Flow arms; use the conventional F# style already present here. Only business/Business.fs and tests/Program.fs may be edited. Keep the project files, nominal types, public record shapes and signatures, project wiring, and generic README unchanged. A supporting helper may be added to Business.fs.

Before launching the broker, set $env:NuGetAudit = 'false' in the shell session so local validation does not fail on unavailable package-audit networking. This does not disable compiler warnings or tests.

Launch once from working directory D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Exact command:

$env:NuGetAudit = 'false'
& 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\maintenance-141\conventional-artifacts\bin\AgentLang.Conventional.Cli\release\AgentLang.Conventional.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\maintenance-141\participants\fsharp-2\project' -TracePath 'D:\code\AgentLang\.agentlang\maintenance-141\participants\fsharp-2\actor.trace.jsonl' -AllowedOperations 'inspect,patch,read,replace,search,validate' -Profile conventional -AdditionalCliArgumentsJson '["--validation-project","tests/AgentLang.Business.Tests.fsproj"]' -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

The conventional host has no help operation. Its closed operations are inspect, search, read, patch, replace, and validate. Use requests like these, substituting the actual relative path and exact content:

{"op":"search","query":"Customer.accountSummary"}
{"op":"read","path":"business/Business.fs"}
{"op":"patch","path":"business/Business.fs","expectedSha256":"<latest successful read.data.sha256>","oldText":"<one unique exact anchor>","newText":"<replacement text>"}
{"op":"replace","path":"tests/Program.fs","expectedSha256":"<latest successful read.data.sha256>","content":"<complete replacement file contents>"}
{"op":"validate"}

Search returns relative paths under data.matches. Read only paths found in the assigned project. Before every patch or replace, use the sha256 from the latest successful read of that same file; after an edit, read it again before another edit. Patch anchors must be exact and unique. Broker errors are nested under error.code and error.message. A validation response passes only when ok is true, data.exitCode is 0, and data.timedOut is false; inspect its stdout and stderr too. Use one JSON request per line and observe each response before the next mutation. Inspect the assigned project first. Poll the same process session after a timeout; do not restart it. The broker permits at most 100 exchanges. Finish with the exact request {"op":"host.close"} and observe process exit before your final response.

# Paid-invoice total maintenance task

The customer account summary and store dashboard already share a typed
customer payment-total operation. Update the shared aggregation so it counts a
payment only when its linked invoice exists, belongs to that customer, and has
status `Paid`. Keep both summary paths using the same shared operation.

The shared operation's public contract is:

```text
Store, CustomerId -> Result<Money, BusinessError>
```

Discover its existing name and implementation from the assigned project. Do not
change that shared operation's public name or signature; you may revise or add
internal supporting callbacks. Do not change public summary signatures or
record shapes:

- `customer.account-summary(Store, CustomerId)` returns the complete original
  customer and the paid total.
- `store.customer-metrics(Store)` preserves all seven existing counts and
  returns the sum of all customer paid totals.

Preserve unknown-customer precedence, checked signed Int64 minor-unit
arithmetic, sticky earlier errors, purity and unrelated behavior. A missing
invoice contributes nothing. A payment linked to an open invoice contributes
nothing. Once a linked invoice is `Paid` and owned by the queried customer,
continue to apply the existing checked addition behavior to its signed amount;
do not add a positivity rule.

The assigned project includes these coordinator-owned fixture helpers for
tests:

- Flow/2: `maintenance141.fixture-import-payment(Store, PaymentId, InvoiceId,
  CustomerId, Option<InvoiceStatus>, Money) -> Store`. Use
  `InvoiceStatus::new("paid")` or `InvoiceStatus::new("open")`; `none` omits
  the invoice record.
- F#: `Store.Maintenance141Fixture.importPayment(Store, InvoiceId,
  CustomerId, InvoiceStatus option, PaymentId, Money) -> Store`. Use `Some
  Paid`, `Some Open`, or `None` to omit the invoice record.

Each call adds one Payment and, when a status is supplied, one matching Invoice
row. Use the helper only in tests to construct imported rows, including missing
or open invoice links and signed amounts. Do not edit either fixture helper or
call it from production definitions. Imported malformed rows are defensive
cases outside the normal business invariants.

Add focused regression tests for the changed rule and preserved behavior. They
must cover the direct shared operation and both summary callers, open and
missing invoice links, signed paid amounts, checked overflow, and unknown
customer precedence. Keep all inherited definitions and meaningful tests, and
do not weaken any library gate. Run the complete attached test suite through
the assigned validation command. For Flow, qualify the shared operation and
its required support functions under the existing gates. Save your changes.
For Flow, explicitly commit the task and close the host cleanly.

Report the discovered operation, what changed and reused, tests and
qualification results, finalization, and any errors or remaining limitations.


The shared-result error type is DomainError in this F# project. The supplied Store.Maintenance141Fixture block in Business.fs is frozen: preserve it byte-for-byte. All permitted edits remain limited to business/Business.fs and tests/Program.fs.

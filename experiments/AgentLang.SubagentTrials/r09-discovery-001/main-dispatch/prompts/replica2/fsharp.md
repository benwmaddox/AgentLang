You are a fresh comparison participant. The human explicitly authorizes changes only to the isolated project path below and its local tests. Use only the supplied broker for project discovery, authoring, and validation. Do not read or edit repository source, other trials, reports, scorers, controls, verifier material, oracle data, or other participant projects. Do not use raw file access or shell commands for project operations. Do not use web tools or spawn agents. Launch the specified broker once and interact with that same process session.

For this arm, customer.account-summary maps to the existing public function Customer.accountSummary, and store.customer-metrics maps to Store.customerMetrics. This mapping names only the existing public summaries and does not prescribe any new shared operation. The public task's Flow/2 syntax and library-gate instructions apply to Flow arms; use the conventional F# style already present here. Only business/Business.fs and tests/Program.fs may be edited. Keep the project files, nominal types, public record shapes and signatures, project wiring, and generic README unchanged. A supporting helper may be added to Business.fs.

Before launching the broker, set $env:NuGetAudit = 'false' in the shell session so local validation does not fail on unavailable package-audit networking. This does not disable compiler warnings or tests.

Launch once from working directory D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Exact command:

$env:NuGetAudit = 'false'
& 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\r09-discovery-001\conventional-broker-artifacts\bin\AgentLang.Conventional.Cli\release\AgentLang.Conventional.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\r09-discovery-001\comparison\participant-work\replica2\fsharp\project' -TracePath 'D:\code\AgentLang\.agentlang\r09-discovery-001\comparison\participant-work\replica2\fsharp\actor.trace.jsonl' -AllowedOperations 'inspect,patch,read,replace,search,validate' -Profile conventional -AdditionalCliArgumentsJson '["--validation-project","tests/AgentLang.Business.Tests.fsproj"]' -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

The conventional host has no help operation. Its closed operations are inspect, search, read, patch, replace, and validate. Use requests like these, substituting the actual relative path and exact content:

{"op":"search","query":"Customer.accountSummary"}
{"op":"read","path":"business/Business.fs"}
{"op":"patch","path":"business/Business.fs","expectedSha256":"<latest successful read.data.sha256>","oldText":"<one unique exact anchor>","newText":"<replacement text>"}
{"op":"replace","path":"tests/Program.fs","expectedSha256":"<latest successful read.data.sha256>","content":"<complete replacement file contents>"}
{"op":"validate"}

Search returns relative paths under data.matches. Read only paths found in the assigned project. Before every patch or replace, use the sha256 from the latest successful read of that same file; after an edit, read it again before another edit. Patch anchors must be exact and unique. Broker errors are nested under error.code and error.message. A validation response passes only when ok is true, data.exitCode is 0, and data.timedOut is false; inspect its stdout and stderr too. Use one JSON request per line and observe each response before the next mutation. Inspect the assigned project first. Poll the same process session after a timeout; do not restart it. The broker permits at most 100 exchanges. Finish with the exact request {"op":"host.close"} and observe process exit before your final response.
# Public refactoring task

The customer account summary and store dashboard currently calculate customer
payment totals separately. Consolidate that duplicated business logic into one
reusable, typed customer payment-total operation, and have both summaries use it.
Inspect the existing project before deciding what to create or change.

Preserve these public contracts:

- `customer.account-summary(Store, CustomerId)` returns
  `Result<CustomerAccountSummary, BusinessError>`. Its successful record contains
  the complete original `customer: Customer` and `paid-total: Money`.
- `store.customer-metrics(Store)` returns
  `Result<StoreCustomerMetrics, BusinessError>`. Its successful record contains
  `customers`, `products`, `subscriptions`, `invoices`, `payments`,
  `pending-emails` and `sent-emails` as Int counts, plus `paid-total: Money`.
  Preserve the meaning of all seven existing store counts.

In the conventional F# project, use its corresponding existing public function
and PascalCase record field names. Keep existing public signatures and record
shapes unchanged in either environment.

A customer total counts only payments for invoices owned by that customer, not
unpaid invoice amounts or another customer's payments. A known customer with no
payments has total zero. An unknown customer produces `CUSTOMER_NOT_FOUND`
before aggregation. The dashboard sums the totals of all stored customers;
an empty store has zero counts and zero paid total.

Use checked Int64 minor-unit arithmetic throughout. Overflow must return
`MONEY_OVERFLOW`, including when individual customer totals fit but their
dashboard sum does not. Preserve an earlier error while processing later items.
Another customer's overflowing total must not affect a valid account query or
replace the unknown-customer error, but it must fail the all-customer dashboard.

Normal input stores satisfy the supplied business invariants: unique identifiers,
existing owners and invoices, and one positive full payment per paid invoice.
Multiple payments for one customer belong to separate invoices. Label any
defensive tests outside these invariants rather than treating them as normal
application states.

The resulting functions must remain pure and preserve the complete input Store,
customer data and unrelated project behavior. Add meaningful regression tests,
run the relevant existing tests and preserve inherited definitions and tests.
Use one shared customer aggregation implementation rather than retaining separate
ownership/filter/sum algorithms behind a wrapper. Supporting typed callbacks are
allowed. The name of the shared operation is your choice.

For language definitions, use current `fn`, property and `==` syntax with attached
documentation. Reusable functions must satisfy the actual library gates; do not
weaken those gates or claim qualification that was refused. Persist your changes
and explicitly finish the task. Report what you changed, reused and tested, plus
any errors, qualification failures or remaining limitations.
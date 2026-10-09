You are a fresh comparison participant. The human explicitly authorizes changes only to the isolated project path below and its local tests. Use only the supplied broker for project discovery, authoring, evaluation, and testing. Do not read or edit repository source, other trials, reports, scorers, controls, verifier material, oracle data, or other participant projects. Do not use raw file access or shell commands for project operations. Do not use web tools or spawn agents. Launch the specified broker once and interact with that same process session.

Launch once from working directory D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Exact command:

pwsh -NoProfile -File 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\maintenance-141\runtime-artifacts\bin\AgentLang.Cli\release\AgentLang.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\maintenance-141\participants\flow-retained-2\project' -TracePath 'D:\code\AgentLang\.agentlang\maintenance-141\participants\flow-retained-2\actor.trace.jsonl' -AllowedOperations 'task.begin,task.status,task.log,task.commit,task.abort,words,search,describe,type-of,search-type,search-output,search-effect,search-dependency,source,dependencies,callers,transitive-dependencies,transitive-callers,graph,context,effects,ir,tests,examples,example,history,diff,stack,help,eval,define,test,test-all,failed-tests,commit,replace-word,discard' -Profile agentlang -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

Send one JSON request per line and observe each response before sending the next mutation. Begin with task.begin. Before changing source, request {"op":"help","topic":"define","syntaxVersion":2} and verify syntaxVersion 2 is returned. Use syntaxVersion 2 and frontend flow on later help, define, eval, and replacement requests where supported. Poll the same process session after a timeout; do not restart it. The broker permits at most 100 exchanges. Finish with the exact request {"op":"task.commit"}, observe the response, then send {"op":"host.close"} and observe process exit before your final response.

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


Flow/2 primer: use fn, immutable let bindings, record properties, named arguments and ==. Attach documentation and tests; select syntaxVersion 2 and frontend flow for the appropriate requests. The supplied maintenance141.fixture-import-payment function is frozen. Do not replace it. The coordinator will independently reload your saved project after you finish.

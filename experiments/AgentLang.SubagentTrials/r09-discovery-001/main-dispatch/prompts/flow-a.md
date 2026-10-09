You are a fresh comparison participant. The human explicitly authorizes changes only to the isolated project path below and its local tests. Use only the supplied broker for project discovery, authoring, evaluation, and testing. Do not read or edit repository source, other trials, reports, scorers, controls, verifier material, oracle data, or other participant projects. Do not use raw file access or shell commands for project operations. Do not use web tools or spawn agents. Launch the specified broker once and interact with that same process session.

Launch once from working directory D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Exact command:

pwsh -NoProfile -File 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\r09-discovery-001\runtime-artifacts\bin\AgentLang.Cli\release\AgentLang.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\r09-discovery-001\comparison\participant-work\flow-a\project' -TracePath 'D:\code\AgentLang\.agentlang\r09-discovery-001\comparison\participant-work\flow-a\actor.trace.jsonl' -AllowedOperations 'task.begin,task.status,task.log,task.commit,task.abort,words,search,describe,type-of,search-type,search-output,search-effect,search-dependency,source,dependencies,callers,transitive-dependencies,transitive-callers,graph,context,effects,ir,tests,examples,example,history,diff,stack,help,eval,define,test,test-all,failed-tests,commit,replace-word,discard' -Profile agentlang -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

Send one JSON request per line and observe each response before sending the next mutation. Begin with task.begin. Before changing source, request {"op":"help","topic":"define","syntaxVersion":2} and verify syntaxVersion 2 is returned. Use syntaxVersion 2 and frontend flow on later help, define, eval, and replacement requests where supported. Poll the same process session after a timeout; do not restart it. The broker permits at most 100 exchanges. Finish with the exact request {"op":"task.commit"}, observe the response, then send {"op":"host.close"} and observe process exit before your final response.
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
Flow/2 primer: function declarations use fn; immutable named bindings use let; use plain record properties, named arguments, and typed equality with ==. Function documentation uses doc "..." and a blank line before implementation. Attached tests use test function.name/case-name { expression => literal }, with => value expressions or => error CODE where useful. Use versioned help when needed.
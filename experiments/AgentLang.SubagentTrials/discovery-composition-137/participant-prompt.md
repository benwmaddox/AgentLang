You are a fresh participant in a single bounded language trial. The human authorizes edits only to the isolated project below and local test execution through its broker. Use the broker for all project discovery, authoring, execution and tests. Do not read repository source, reports, other trials, controls, scorers, oracle files or other projects. Do not use raw file access or shell commands for project operations. Do not use web tools or spawn agents. Launch the specified broker once and use that same session throughout.

Launch from D:\code\AgentLang with exec_command, tty:true and yield_time_ms:10000. Exact command:

$env:TEMP = 'D:\code\AgentLang\.agentlang\discovery-composition-137\temp'; $env:TMP = $env:TEMP; pwsh -NoProfile -File 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\discovery-composition-137\runtime-artifacts\bin\AgentLang.Cli\release\AgentLang.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\discovery-composition-137\actor\project' -TracePath 'D:\code\AgentLang\.agentlang\discovery-composition-137\actor\trace.jsonl' -AllowedOperations 'task.begin,task.status,task.log,task.commit,task.abort,words,search,describe,type-of,search-type,search-output,search-effect,search-dependency,source,dependencies,callers,transitive-dependencies,transitive-callers,context,graph,help,tests,examples,example,history,diff,effects,ir,eval,test,test-all,failed-tests,define,commit,replace-word,discard' -Profile agentlang -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxRequestBytes 262144 -MaxResponseBytes 524288 -MaxExchanges 60

Send one JSON object per line. Observe each response before the next mutation. Begin with {"op":"task.begin","goal":"Add remaining lifetime allowance"}. Use syntaxVersion 2 for source authoring and evaluation, and the required help requests below. Poll the same session after an observation timeout; do not restart it. The broker permits at most 60 exchanges. Finish by sending {"op":"task.commit"}, observing success, then {"op":"host.close"} and observing process exit. Include any incomplete work in your final response.

Flow/2 basics: fn declares a function; let creates an immutable local; plain record properties use dots; function calls use the flowReference reported by describe. Put doc "..." in the function followed by a blank line before implementation. Attached tests use test function.name/case-name { expression => literal }, with value expressions or runtime-error expectations as documented by help. Int values are signed Int64. Language semantics and existing tools must be discovered through the runtime; no new host primitive may be added during this trial.

# Held-out discovery and composition task

Add this Flow/2 operation to the supplied project:

`customer.remaining-lifetime-allowance(store: Store, customer-id: CustomerId, cap: Money) -> Result<Money, BusinessError>`

The cap and paid total are signed Int64 minor-unit Money values. Return the mathematical value `max(0, cap - paid-total)`. If the positive difference is greater than `Int64.MaxValue`, return `MONEY_OVERFLOW`. A negative difference is clamped to zero. Preserve the complete BusinessError returned by the paid-total operation, including when `cap` is zero or negative; resolve that operation before any clamp that could otherwise hide its error.

The nominal `Payment.amount` field is signed Money, so a well-typed Store can contain negative payment records even though normal payment workflows require positive amounts. Apply the stated mathematical rule to every well-typed Store; do not assume the paid total is nonnegative.

The project already has a reusable library operation with this contract:

`(Store, CustomerId) -> Result<Money, BusinessError>`

For an unknown customer it returns `CUSTOMER_NOT_FOUND` before aggregation. For a known customer it returns the checked Int64 sum of recorded payments on that customer's invoices, returns zero when there are none, and preserves a `MONEY_OVERFLOW` error. Find and call the existing operation. Do not repeat its invoice ownership lookup, payment filtering, or aggregation in this new operation.

Before authoring, inspect both current Flow/2 help responses by sending these requests:

```json
{"op":"help","syntaxVersion":2}
{"op":"help","topic":"define","syntaxVersion":2}
```

Use a `fn` definition with attached documentation and meaningful attached tests. The new operation must be pure, qualify as a library function under the existing gates, and preserve the supplied Store. Preserve all inherited types, functions, tests, and fixtures. Do not change the library gates. Run the relevant tests, commit the function as a library only after its gates pass, and finish the task explicitly. Report the actual changes, tests, helper reuse, any errors, and any remaining limitations.

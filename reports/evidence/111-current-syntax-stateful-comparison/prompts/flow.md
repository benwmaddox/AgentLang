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


## Language interface

Create only invoice.queue-reminder-once : Invoice -> String as a persistent
library function, using current Flow/2 authoring (fn, record properties, typed ==).
Keep the seeded Flow/1 helper and types unchanged; they are interoperable.
Declare exactly fs.read and fs.write. Direct dependencies must include the seeded
invoice.reminder-path and file.exists?, file.read, file.write. Supply at least
four uniquely named attached tests and one first-class example, with full own
instruction and branch-outcome coverage, then commit durably. The dictionary
still uses word-oriented protocol names. Discover signatures through the host.


# Compact Flow/2 primer

Interact through the supplied JSONL host. New definitions/evaluations use
"frontend":"flow", "syntaxVersion":2. Persisted Flow/1 definitions can be
inspected and called, but author the new function in Flow/2.

Dictionary names use dots. Qualified source calls use ::, for example
math::double(3). A receiver call value.double() passes value as the first argument.
Plain record fields use value.field without parentheses. Nominal wrappers remain
distinct types: Wrapper::new(raw) constructs one and Wrapper::value(wrapped)
explicitly unwraps it. Typed == requires compatible types and does not unwrap.

fn tutorial.double(value: Int) -> Int {
    doc "Twice the supplied integer."

    value.multiply(2)
}

test tutorial.double/basic {
    tutorial::double(3)
    => 6
}

example tutorial.double/basic {
    tutorial::double(4)
    => 8
}

Omitted effects mean none. For effects, put metadata before doc and a blank line
before code, e.g. effects fs.read, fs.write. An if condition { value } else
{ value } expression returns compatible values from both branches. Locals use
let name = expression; and are immutable. Constructors accept named fields when
metadata supplies parameter names. Record constructor names are discoverable.
Effect-only calls return Unit; use them as statements before a final result.

Useful requests:
{"op":"words"}
{"op":"search","query":"text"}
{"op":"describe","word":"some.name"}
{"op":"source","word":"some.name"}
{"op":"context","word":"some.name"}
{"op":"help"}
{"op":"define","frontend":"flow","syntaxVersion":2,"source":"..."}
{"op":"eval","frontend":"flow","syntaxVersion":2,"code":"..."}
{"op":"test","word":"some.name"}
{"op":"test-all"}
{"op":"examples","word":"some.name"}
{"op":"commit","word":"some.name"}

Use help for exact task, attachment, qualification and persistence commands.
Attach tests/examples in source using the dotted owner name followed by /case.
A test ends with => literal, => value pure-expression, or => error ERROR_CODE;
an example uses a literal expectation. Full own coverage and passing attached
tests are required for library publication. Inspect the contained test results:
a successful protocol response does not imply every test passed. Test execution
uses the runtime's deterministic effect providers.

Do not bypass this host with raw project reads or edits. The virtual file
provider belongs to this project and is not arbitrary host filesystem access.
Send one request per line, observe its response before the next mutation, and
keep one host process. A wait timeout is not process failure. Inspect state after
an uncertain mutation instead of blindly replaying it. Finish your task and
persistent commit, then close this v2 host with {"op":"host.close"}; observe
terminal process status before your final response. Do not restart a closed host.


Launch once from D:\code\AgentLang using exec_command with tty:true and yield_time_ms:10000. Use require_escalated if sandbox creation fails:

& 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\reminder-flow2-001\runtime\bin\AgentLang.Cli\release\AgentLang.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\reminder-flow2-001\actors\flow' -TracePath 'D:\code\AgentLang\.agentlang\reminder-flow2-001\logs\flow-actor.trace.jsonl' -AllowedOperations @('words','describe','search','source','dependencies','callers','eval','define','test','test-all','tests','commit','replace-word','task.begin','task.commit','task.status','task.log','ir','context','failed-tests','examples','example','effects','type-of','search-type','search-output','history','diff','graph','transitive-dependencies','transitive-callers','search-dependency','snapshot.save','snapshot.load') -Capabilities @('fs.read','fs.write') -Profile agentlang -AdditionalCliArguments @('--frontend','flow','--syntax-version','2') -ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

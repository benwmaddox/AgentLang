You are a fresh external experimental coding agent. The human explicitly authorized this isolated experiment, edits to your trial project and local tests. You are not alone in the repository; all other work is outside your scope. Use only the supplied broker for discovery, editing, evaluation and testing. Do not read repository source, other trials, oracle/scorer/control material or reports. Do not use web tools or spawn agents. You may read this one prompt file, launch the specified broker once, and interact with that session. A sandbox process-creation failure permits require_escalated for that exact launch only; it does not expand file scope.

Send one JSON request per line and observe the response before the next mutation. A protocol success response may contain failing tests: inspect results. Poll an existing session after a timeout; do not restart it. Close with {"op":"host.close"} and observe process exit before your final response. The broker permits at most 100 exchanges. Report actual work, tests, failed attempts, reused project operations and limitations candidly.

# Public task

Repair the two-invoice reminder workflow. It must process both invoices independently, first then second, and return their two results in that order. A first invoice that is ineligible must not suppress an eligible second invoice.

For each invoice, only status exactly "open" qualifies (ordinal/case-sensitive, without normalization). An ineligible invoice returns "not-open" with no provider calls. For an open invoice, check its reminder path once. If the path exists, read it once and return its exact contents without writing; otherwise write "queued" once and return "queued". Both invoices may have the same identifier: the second must observe the first one's completed state changes. Preserve unrelated files and all existing contents, including empty strings. Repeating the workflow must not overwrite markers. Use only the supplied virtual provider.

The supplied execution boundary preflights both read and write capabilities before processing either invoice. Missing either must reject execution with CAPABILITY_DENIED and no provider calls. Keep that boundary and existing nominal types/public contracts intact. Repair any tests whose expectations encode the defect, add meaningful regression tests, run them, and preserve unrelated behavior. Inspect the environment to locate the affected operation; no target/helper name is supplied here. Report actual edits, tests, failures, and remaining limitations.

# Conventional F# interface

Use the existing typed F# project and explicit virtual provider. Only Operations.fs and SelfTests.fs may change. Keep Domain.fs and StatefulPilot.fsproj byte-identical. Preserve unrelated operations, inherited tests and entry-point wiring. Repair the affected workflow and its faulty test expectations, and add regression tests or small helpers if useful. Keep documentation alongside functions. Do not weaken the public contract or bypass the capability boundary. Preserve inherited output markers, and make the pair-test count marker reflect tests actually executed.

Available JSON requests:
{"op":"read","path":"Operations.fs"}
{"op":"inspect","path":"Operations.fs"}
{"op":"search","query":"text"}
{"op":"patch","path":"Operations.fs","expectedSha256":"hash from read","oldText":"unique exact text","newText":"replacement"}
{"op":"replace","path":"Operations.fs","expectedSha256":"hash from read","content":"whole file text"}
{"op":"validate"}
Use the newest response.data.sha256 for edits. Validate runs the project locally; read its full result. Before closing, inspect the saved code and run validation. Raw file edits and direct shell builds are outside this actor interface.

Launch once from D:\code\AgentLang using exec_command, tty:true, yield_time_ms:10000:

& 'D:\code\AgentLang\scripts\Start-SubagentTrialHostV2.ps1' -CliDll 'D:\code\AgentLang\.agentlang\pair-repair-001\conventional-broker\AgentLang.Conventional.Cli.dll' -ProjectPath 'D:\code\AgentLang\.agentlang\pair-repair-001\actors\conventional-2' -TracePath 'D:\code\AgentLang\.agentlang\pair-repair-001\logs\conventional-2.trace.jsonl' -AllowedOperations 'inspect,read,search,patch,replace,validate' -Profile conventional -AdditionalCliArguments @('--validation-project','StatefulPilot.fsproj') -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

# Agent experiment harness

The project owner's selected experiment path is fresh Codex subagents using a compact prompt and the runtime command protocol. These agents are external development tools; the language contains no AI machinery. A subagent trial must save its prompt, runtime interaction log, project states, and host-run acceptance results. Exact token usage and framework context are unavailable unless reported by the subagent interface. Protocol-only instructions do not enforce host tool isolation. See [the evaluation protocol](EVALUATION.md).

The harness runs a task against the AgentLang JSON dispatch API, one provider turn at a time. It supports a deterministic scripted provider for offline tests and an OpenAI Responses API provider for live experiments. Conventional repository editing and multi-task benchmark orchestration are not implemented yet.

## Run the offline customer example

From the repository root:

```powershell
dotnet run --project experiments/AgentLang.Benchmarks -- run `
  --task experiments/AgentLang.Benchmarks/fixtures/customer-discount-task.json `
  --provider scripted `
  --script experiments/AgentLang.Benchmarks/fixtures/customer-discount-script.json `
  --seed-source examples/customer.agent
```

This works without an API key. The script inspects a word, evaluates a premium customer, and ends with an assistant response. The task's independent oracle also checks the regular-customer result and existing tests.

## Run an OpenAI-backed task

Set `OPENAI_API_KEY` in the process environment, then pass a model explicitly:

```powershell
dotnet run --project experiments/AgentLang.Benchmarks -- run `
  --task experiments/AgentLang.Benchmarks/fixtures/customer-discount-task.json `
  --provider openai `
  --model <model-name> `
  --mode growing `
  --project-root .agentlang/growing/customer `
  --seed-source examples/customer.agent
```

The provider sends requests to the Responses API with `store:false`. Each follow-up request includes the complete input history, including model output items, reasoning items returned by the API, function calls, and matching function outputs. The harness requests `reasoning.encrypted_content`, does not use `previous_response_id`, and uses `call_id` to pair each function result with its call. Function tools use strict object schemas and a small closed AgentLang command surface. The API key is sent only as the HTTP authorization header and is not included in request or run logs.

The loop rejects incomplete provider responses, empty final answers, turn overflow, tool overflow, and requests above the configured UTF-8 byte budget. Malformed tool calls with a usable `call_id` receive a structured error so the model can recover. Unknown commands are rejected by the harness even if a scripted provider bypasses schema validation.

The system prompt always carries a compact language primer, even when a task adds its own system guidance. Definitions use `word name : Inputs -> Outputs`, an `effects` declaration, a body, and `end`; locals bind with `let name` and read with `$name`. Tests use `test word/case`, a body, `=> expected-literal`, and `end`. The define tool requires `candidate` or `temporary` lifetime. Project commits require passing attached tests. A `library` commit also requires those tests to exercise every instruction and every control-flow outcome, including both `if` branches and declared collection iteration outcomes.

## Flat and Growing runs

Flat mode stores its working project under the unique run directory, so each invocation begins from its own copy seeded by `--seed-source` when provided. Growing mode reuses `--project-root` across invocations and seeds it only when it has no `dictionary.agent`; this is how committed words become available to later tasks. If omitted, growing projects use `.agentlang/growing-project`.

Before starting the task, the harness captures the dictionary and task history. The model may stage words, use temporary words, and commit individual tested words at project or library quality while the task transaction stays open. The harness evaluates every configured oracle before calling the final task commit. Any provider error, limit, failed oracle, or failed final commit aborts the task and restores the captured dictionary and task history. The final-state file records the state after commit or rollback. Flat runs require their project to live at `<run-directory>/project`; Growing runs reuse their configured project root.

## Task format

Task files are JSON objects with `id`, `goal`, and a non-empty `oracle` array. `systemPrompt` and `initialContext` are optional. An oracle entry has `operation`, optional `args`, optional `expectOk` (defaults to true), optional `textContains`, optional `data` (checked as a recursive subset), and optional `minimumPassingTests`. `test`, `test-all`, and `failed-tests` entries additionally require actual passing results (and no failures for `failed-tests`).

The included task checks `eval` results and a minimum test count:

```json
{
  "id": "customer-discount-smoke",
  "goal": "Reuse the customer vocabulary to verify the premium discount.",
  "oracle": [
    {
      "operation": "eval",
      "args": { "code": "\"premium\" 100.0 customer.new customer.discounted-balance" },
      "data": { "stack": ["90"] }
    },
    { "operation": "test-all", "minimumPassingTests": 4 }
  ]
}
```

## Limits and recorded measurements

The default caps are 12 model turns, 40 runtime tool calls, 4096 generated output tokens per model response, and 262144 bytes for each serialized provider request. Override them with `--max-turns`, `--max-tool-calls`, `--max-output-tokens`, and `--context-budget-bytes`. Context bytes include the model name, instructions, task text, all prior input/output items, and the complete tool schemas on every call. The report distinguishes prepared request bytes (including requests rejected locally by a cap) from bytes actually sent, and records the peak prepared request size. Its token estimate is `ceil(total sent request bytes / 4)` and is marked as a rough estimate. Actual input/output/total token usage is recorded only when the provider returns it; missing or uncertain usage, including a failed provider request, remains JSON `null` rather than zero.

Each run directory contains `prompt.txt`, `trace.jsonl`, `runtime.log`, `initial-state.json`, `final-state.json`, and `report.json`. The trace includes full request bodies, raw provider response bodies, tool calls and results, oracle checks, and task commit/abort records. These files can contain project source and model prompts, so they are local experiment artifacts under `.agentlang/` by default and should not be committed blindly.

The scripted response fixture format is a JSON object with a `responses` array. Each item can contain `status`, `output` (Responses output items), `output_text`, and `usage`. Missing usage stays unknown. The OpenAI adapter tests use a canned HTTP handler; the test suite makes no live API calls.

Useful commands:

```powershell
dotnet build AgentLang.sln
dotnet run --project tests/AgentLang.Harness.Tests
dotnet run --project tests/AgentLang.Business.Tests
```

The harness currently runs one task per process. Flat/Growing retention is available through project placement, but there is no conventional F# baseline runner, multi-task scheduler, provider token counter, or live benchmark report yet.

OpenAI request behavior follows the [Responses function-calling guide](https://developers.openai.com/api/docs/guides/function-calling), the [Responses create API reference](https://developers.openai.com/api/reference/resources/responses/methods/create), and [reasoning guidance for preserving items across function calls](https://developers.openai.com/api/docs/guides/reasoning#keeping-reasoning-items-in-context).

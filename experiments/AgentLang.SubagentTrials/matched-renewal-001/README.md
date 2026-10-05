# Matched renewal fixture set

This directory prepares three starting environments for one future matched
renewal task. It is fixture and protocol groundwork; it contains no new-agent
run, no measured comparison, and no claim about token, context, or latency
savings.

`fixtures/flat.agent` contains only the `Customer` and `Subscription` schemas.
The generated constructors/accessors are available from those schemas, but the
fixture contains no authored words, tests, or examples. `fixtures/growing.agent`
adds the two retained customer words and seven attached tests from the externally
verified task-2 starting fixture ([provenance and limits](../../../reports/004-subagent-vocabulary-pilot.md)).
It deliberately adds no answer to the renewal task.

For a fresh AgentLang arm, install the selected fixture as the project's legacy
`dictionary.agent` before starting the pinned host. In particular, `growing.agent`
contains persisted `maturity library` and `revision 1` metadata: it is imported
as existing project state, not submitted as a `define` request. The host assigns
legacy word IDs during initial load. This does not yet establish a reproducible
identity baseline across fresh processes. Saving the legacy seed alone does not
persist the assigned IDs. Before actual trials, implement and verify migration
to a committed generation with persisted identities, then pin that generation
for every continuation. That preparation step is pending; these fixtures are
not ready-to-run benchmark snapshots.

`fixtures/conventional/MatchedRenewal.fsproj` is a small typed F# analogue. It
contains the same toy `string`/`float` fields, baseline premium classification
and discount helpers, eight renewal eligibility self-tests, and a compileable
`Renewal.balance` placeholder that only returns the baseline discount. Its
default test run should therefore build successfully and visibly fail the
premium + annual + renewable case. A separate `--baseline-jsonl` mode lets the
host compare the baseline discount helper with the retained language word on
the same `{ "kind", "balance" }` cases without evaluating renewal policy.

All three modes share [task.md](task.md). The task names the required result
signature but does not list the helpers available in any environment; discovery
is part of the task. The AgentLang modes use the `Customer Subscription ->
Float` word signature, and the F# mode uses `Renewal.balance`.

These fixtures intentionally use floating-point balances to match the prior
toy pilot. They are not the strict Money/Email/ID/time business contract, and
they are not a full 40–60-word business domain. The Conventional CLI wraps only
the closed `ConventionalDispatcher` operations. `--validation-project` is host
configuration: a JSONL request may ask to validate, but cannot choose the
command, project, or arguments. The dispatcher confines file operations to
its project root, but running an F# project is not an OS sandbox.

Start the Conventional JSONL host from the repository root with:

```powershell
dotnet run --project experiments/AgentLang.Conventional.Cli/AgentLang.Conventional.Cli.fsproj -- `
  --project experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/conventional `
  --validation-project MatchedRenewal.fsproj `
  --jsonl
```

Each request is one JSON line such as `{"op":"search","args":{"query":"discount"}}`;
`{"op":"validate"}` invokes only the host-configured `dotnet run` target.
The F# fixture's independent baseline adapter can be exercised directly with
`dotnet run --project experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/conventional/MatchedRenewal.fsproj -- --baseline-jsonl`.
It accepts one strict `{ "kind": string, "balance": finite-number }` case per
line and returns the original fields plus the established baseline result.

No fresh matched agent task has been run. Framework thread-capacity prevented
creating a fresh trial thread; this fixture preserves the task setup only, and
the available external-agent result in report 004 is exploratory, not a
Flat/Growing/Conventional comparison.

# Durable Flow Runtime acceptance report

Status: the eleventh focused Runtime run passed all eleven groups and 203
assertions, including mixed Flow/Stack generated-record attachments and a
Stack-only v2-to-v1 manifest migration followed by fresh-Engine reload. The
final publication gate passed all 26 checks with a clean Release build; source
CI remains pending. The test project also passed compile-only builds against
the fresh Core checkpoint with zero warnings and errors. The first Runtime run stopped because qualified Flow calls used dot
syntax; the fixtures now use `namespace::word(...)`.
The second passed durable/reload and fresh-process CLI checks, then exposed a
migration oracle that compared Stack writer output with raw input; the fixture
now captures the stored Stack bytes and references before migration. The third
run confirmed named snapshots require an active manifest; unsupported
EmptyAuthority named-snapshot coverage was removed in favor of the supported
task-abort path. The fourth reached empty-authority rollback and found that the
`words` inventory includes builtins; the fixture now compares exact baseline
inventory metadata and IDs after abort and fresh reload. The updated suite is
awaiting another run and makes no release-readiness claim. The fifth run passed
those corrected fixtures and exposed a Runtime issue when Stack `file.write` is
evaluated after Flow publication: lowering failed with
`IR_SOURCE_ORIGIN_MISSING`. The Runtime source fix now preserves Stack source
origins after Flow publication; the fixture also covers Stack word/test/example
calls into Flow before and after reload. The sixth Runtime run then failed while registering the Stack
wrapper: its Stack parser reported `NAME_UNKNOWN_WORD` for the already-committed
Flow target. The source fix now allows the Stack wrapper, test, and example to
resolve the committed Flow word. The seventh Runtime run passed those interop
checks and the populated snapshot/provider checks, then reached persisted
binding tamper verification. The runtime identified the forged call with the
more precise owner `durable.increment/basic`, while the fixture expected only
the owner word. The assertion now checks the owner and case, and the eighth
focused run passed all nine groups and 172 assertions. The ninth run exposed a
`STORAGE_PROJECT_MISMATCH` when the new generated-record fixture reloaded its
mixed project export. The Runtime source fix restored the export/source-object
comparison; the tenth run passed ten groups and 189 assertions. The isolated
Stack-only v1 migration fixture was then added, and the eleventh run passed all
eleven groups and 203 assertions.

## Ownership

`tests/AgentLang.Flow.Runtime.Tests` will own integration checks that cross
`Runtime.Engine`, content-addressed Storage, and a newly constructed Engine.
`tests/AgentLang.Flow.Tests` remains responsible for parser/lowerer/compiler
behavior and source-backed call-binding reconciliation. `tests/AgentLang.Storage.Tests`
remains responsible for v1/v2 serialization, object membership, limits, and
snapshot envelope compatibility; its Runtime-only Flow rejection assertion will
be removed when the Runtime path supports Flow. `tests/AgentLang.Harness.Tests`
continues to cover agent/provider orchestration. The new suite must be included
in `AgentLang.sln` and `scripts/Validate.ps1`.

The adapter contract is now concrete. `define` opts in with `frontend: "flow"`
and one authored `source`, plus optional full-source `tests` and `examples`;
`eval` opts in with `frontend: "flow"` and an expression in `code`. Omission
keeps the Stack route, an unknown explicit selector returns
`RUNTIME_FRONTEND_UNSUPPORTED`, and malformed Flow input keeps its structured
`FLOW_*` diagnostic. Flow definition replacement uses `replace: true` with
`expectedRevision`; attachment removal uses source-hash CAS. Persisted call
drift is reported as `FLOW_RUNTIME_BINDING_MISMATCH` with owner, role, path, and
source span.

The new `tests/AgentLang.Flow.Runtime.Tests` project now contains vertical
fixtures for exact-source publication and fresh-Engine reload, frontend
selection/no fallback, Stack-to-Flow migration under the same WordId, rejection
of retained Stack cases until every case is replaced, stable-ID replacement and
attachment hash CAS, failed replacement nonpublication, temporary
discard/promotion/task cleanup, task abort from both empty and populated
authority, rejection of named snapshots before a manifest exists, populated
snapshot restore, virtual-file and clock state, persisted-binding tamper
rejection, retained dot-call stability and ambiguous-target rejection, library
branch-coverage isolation, Stack wrapper/test/example calls into Flow across
reload, Stack-generated record accessor tests/examples across a fresh Engine
reload, v1 aggregate recovery of Stack-generated cases, and a fresh-process CLI
evaluation. The eleventh focused run passed all eleven groups and 203
assertions. The solution and validation gate include the project.

## Highest-risk acceptance oracles

1. **Authored source survives a full restart.** Explicitly define a named-parameter
   Flow word with a test and example, then commit it. Compare the exact authored
   bytes with the word/test/example objects and their hashes; verify the revision
   records Flow/1, the stable WordId, maturity/revision metadata, and complete
   definition/actual/expected/example call bindings. Check that the aggregate
   export has an explicit Flow marker and does not substitute lowered RPN for the
   authored definition. Construct a new `Runtime.Engine` for the same project,
   then check `source`, named parameter information in `describe`, attached
   `tests`/`examples`, test execution, and Flow evaluation against the reloaded
   executable snapshot. After the Flow commit, define a Stack wrapper that calls
   the Flow word by its RPN dictionary name, attach a Stack test and example, and
   require both before and after a fresh Engine reload. This is the primary
   authority oracle; in-memory staging alone does not satisfy it.

2. **Persisted bindings are verified, not trusted.** Start from a valid committed
   Flow project, change one persisted target identity or structural call path
   while keeping all source objects and Storage-level references valid, and
   publish that storage-valid manifest. A fresh Engine must reject it with the
   documented binding diagnostic and source site. The target must not silently
   resolve to a different word in the new compiler vocabulary. A separate edit
   that adds a new word and would redirect an unchanged retained dot call must
   fail before publication; assert the prior manifest hash and executable source
   remain authoritative and that the diagnostic identifies the original caller
   and call site.

3. **Replacement preserves identity and checks retained callers.** Replace a
   committed Flow definition under the same stable ID. A compatible replacement
   must keep that ID, advance the revision, preserve the old authored revision,
   and allow retained source to re-resolve to the same ID at the new revision.
   A proposed edit that changes a retained call's target ID or makes its dot stage
   ambiguous must fail with exact caller/site details and leave `CURRENT` and the
   live Runtime state unchanged. Exercise a caller with named arguments or a dot
   receiver so the authored call order and single receiver evaluation are
   observable; compiler-only argument-order checks remain in `Flow.Tests`.

4. **Failed gates do not publish.** For a failed candidate test, a failing
   durable-caller test, and a library word with one missing actual branch
   outcome, compare the pre/post manifest hash, revision list, authored objects,
   and observable behavior. Only a fully accepted edit may become reachable.
   For the library case, make both branches return the same value, let the actual
   test execute one branch, and let its pure expected expression execute the
   other. The commit must still report incomplete actual coverage. Add a real
   actual case for the missing branch and then require success. This detects
   accidental mixing of expectation coverage into the tested word's trace.

5. **Temporary, discard, and task rollback match authored state.** A temporary
   Flow definition must not appear in the manifest. Discard it and verify the
   word and attached cases disappear; promote it and verify promotion retains
   its existing stable ID before its first durable revision. In a separate task,
   begin from a committed Flow project, stage and durably commit a Flow revision,
   then abort. Compare the restored manifest hash, export bytes, revision and
   attachment object bytes, source query, and behavior with the captured state.
   The generation should advance through restore while no staged Flow revision
   remains current.

   From EmptyAuthority, also require `snapshot.save` to return
   `STORAGE_SNAPSHOT_REQUIRES_MANIFEST`. Then begin a task, commit a Flow owner,
   abort, and compare the restored export and full pre-task words inventory
   (including builtin names and IDs) with the in-memory and fresh-Engine state.

6. **Named snapshot loading rehydrates Flow in a fresh Engine.** Save a committed
   Flow snapshot with virtual files and a fixed clock, then make a later durable
   change. In a new Engine, load the earlier named snapshot and verify the
   manifest identity, source/ID/attachments, test behavior, virtual files, and
  clock all return together. Storage already tests v1 snapshot-envelope/v2
  manifest compatibility; this runtime case proves language state is rebuilt
  from a supported populated snapshot.

The CLI fixture exercises the explicit frontend selector through a real
JSON-lines process against a committed temporary project, rather than another
call to the same Engine instance.

## Validation

Focused validation after the Runtime API is frozen:

```powershell
dotnet run --project tests/AgentLang.Flow.Runtime.Tests -c Release
dotnet run --project tests/AgentLang.Flow.Tests -c Release
dotnet run --project tests/AgentLang.Storage.Tests -c Release
dotnet run --project tests/AgentLang.Acceptance -c Release
```

Then run `dotnet build AgentLang.sln -c Release` and
`./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/flow-durable-validation.json`
in the root-owned serial validation slot. The focused no-dependencies compiles
completed successfully. Runtime attempts are recorded in
`reports/evidence/045-first-runtime-tests.json`,
`reports/evidence/045-second-runtime-tests.json`,
`reports/evidence/045-third-runtime-tests.json`,
`reports/evidence/045-fourth-runtime-tests.json`,
`reports/evidence/045-fifth-runtime-tests.json`,
`reports/evidence/045-sixth-runtime-tests.json`,
`reports/evidence/045-seventh-runtime-tests.json`,
`reports/evidence/045-eighth-runtime-tests.json`,
`reports/evidence/045-ninth-runtime-tests.json`,
`reports/evidence/045-tenth-runtime-tests.json`, and
`reports/evidence/045-eleventh-runtime-tests.json`. The sixth run exposed a mixed
Stack/Flow registration issue after the earlier source-origin failure. The
seventh run passed the source-origin and mixed-frontend interoperability
fixtures and stopped at the persisted attachment-tamper diagnostic assertion;
the fixture now expects the case-specific owner name. The eighth focused run
passed the then-current nine groups and 172 assertions. The ninth run exposed a
`STORAGE_PROJECT_MISMATCH` in the new generated-record fixture; the Runtime
source fix restored the export/source-object comparison. The tenth run passed
ten groups and 189 assertions, followed by the eleventh run passing all eleven
groups and 203 assertions including the v1 migration oracle. Final publication
validation is recorded in `reports/evidence/045-publication-validation.json`:
all 26 checks passed and the Release build completed with zero warnings and
errors. Source CI remains pending.

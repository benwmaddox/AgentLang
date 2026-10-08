# Conventional actor post-solution mutation plan

## Scope and inputs

Work only under `.agentlang/pair-repair-001/post-mutations/conventional-1`. Use the closed `.agentlang/pair-repair-001/actors/conventional-1` project as the immutable source and the existing `.agentlang/pair-repair-001/scoring/score-pair.ps1` as the frozen independent oracle. Do not edit the actor, tests, scorer, starts, runtime, or other actors.

Create two project copies, each containing the actor's four source files. Keep `Domain.fs`, `SelfTests.fs`, and `StatefulPilot.fsproj` byte-identical to the actor in both copies. Change only `Operations.fs` in each copy:

1. In `wrong-second-id`, retain the second invoice's status and process it using the first invoice's identifier for its reminder path.
2. In `write-back-existing-second`, retain first invoice processing. Inline only second invoice processing so an existing second marker is read, written back unchanged exactly once, and returned unchanged. Preserve ordinary missing-path and non-open behavior.

## Validation

For each copy, run a fresh Release build and attached self-tests, saving the exact command output. Then run the frozen F# scorer with that copy as `-ActorProject` and a unique score path under this folder. Do not pass `-RequireFullPass`; record how the unchanged actor tests and independent 12-case oracle respond separately.

After validation, compare each copy's four source hashes to the original actor. Verify the actor's source inventory is unchanged. Record mutation source hashes, build/test output, scorer output, and any failed checks here under the owned folder.

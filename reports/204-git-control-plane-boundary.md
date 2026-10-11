# 204 — Reserve Git control-plane paths from language I/O

Status: accepted after fresh local Release validation and independent source review.

A valid language `fs.write` grant previously allowed writes to the project's
root `.git` metadata. An inert disposable baseline probe changed synthetic
`.git/hooks/pre-commit` bytes through the public CLI. No hook was executed and no
working repository metadata was changed. An initial probe used the wrong JSON
field and failed to evaluate; that preparation failure is preserved separately.

The shared real-filesystem resolver now reserves the canonical root `.git` path
and subtree from read, write and existence operations, including a `.git` pointer
file. Canonical normalization precedes the case-insensitive exact first-segment
check. The diagnostic retains `EFFECT_FILE_PATH_RESERVED` and describes
host-managed metadata. Ordinary neighboring `.git-data` files remain accessible.
Virtual test providers are unchanged. No dependency, effect or native ABI changed.

Disposable CLI fixtures cover direct, normalized, uppercase and Windows ambiguous
spellings, synthetic config/hook byte preservation, a separate Git pointer-file
root, ordinary sibling access and fresh dictionary reload. Existing Windows alias
rejection remains in effect. No Git initialization, hook execution or remote
operation is part of these tests.

Validation on the final frozen source:

- `dotnet build AgentLang.sln -c Release -m:1 -p:NuGetAudit=false`: zero warnings/errors.
- `dotnet tests/AgentLang.Cli.Tests/bin/Release/net9.0/AgentLang.Cli.Tests.dll`: 13 groups, 305 assertions.
- `dotnet run --no-build -c Release --project tests/AgentLang.Flow.Runtime.Tests`: 44 groups, 2,191 assertions.
- Independent source review: scoped approval; final pins match. `git diff --check` passed.

Package audit was explicitly disabled for the fresh build; no package was added.
All validation ran locally. This milestone uses focused regressions rather than
repeating the unrelated full 37-check benchmark/preflight suite. Original logs
before the final diagnostic wording correction are retained alongside final logs.

The [evidence archive](evidence/204-git-control-plane-boundary/validation.zip)
and [member index](evidence/204-git-control-plane-boundary/validation.index.json)
retain probe requests/results, preparation failures, source review, final receipts,
logs and accepted source snapshots. Root reopened and verified every member.

This is a bounded path policy, not an OS sandbox: nested repositories, hardlinks
and hostile concurrent filesystem mutations remain outside its tested boundary.
It does not change the closed agent experiment's runtime or efficacy conclusions.

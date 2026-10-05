# Conventional repository tools

`AgentLang.Conventional` provides the file and validation operations for the
conventional F# baseline. It is a small deterministic host dispatcher intended
to give an external coding agent familiar repository-style tools while keeping
the benchmark's acceptance oracle outside the agent-editable project root.
It is separate from the language runtime and has no model-provider dependency.

The dispatcher exposes five closed operations: `inspect(path)`, `read(path)`,
`search(query)`, `replace(path, expectedSha256, content)`, and `validate()`.
Each takes a JSON object with exactly the documented fields and returns a
structured JSON result. Unsupported operations and extra or malformed fields
return structured errors. Each call is appended to an ordered metadata-only
operation log; the log omits source contents, environment values, and process
output.

`inspect` returns path, extension, byte and line counts, and a SHA-256 content
hash. `read` also returns the UTF-8 source text. `replace` is a definition-level
whole-file replacement: callers must provide the hash from a recent read. A
stale hash leaves the file unchanged. The replacement is written to a bounded,
temporary file, flushed, and moved over the target atomically. Use a fresh read
after replacement to verify the saved text and hash.

`search` performs deterministic, case-insensitive literal matching across
UTF-8 source, project, solution, JSON, and Markdown files. Paths and results
are sorted. It reports one-based line numbers and bounded previews. The host
can configure limits for file size, file count, total directory entries,
directory depth, and result count. Defaults are 1 MiB per file, 5,000 files,
20,000 directory entries, depth 64, and 200 matches. It skips common generated
directories such as `.git`, `bin`, and `obj`; hitting a traversal or result limit is reported
instead of silently presenting the partial result as complete.

The host fixes the validation target when constructing the dispatcher. An
agent can request only that `dotnet build` or `dotnet run` action for the
configured F# or C# project; it cannot choose a command, executable, arguments,
or project path. Validation has host-configured timeout and output caps. Common
credential-named environment variables are removed from the child environment,
and process environments are never written to the operation log.

Repository paths must be relative to the configured project root. Absolute
paths, traversal, Windows-invalid names and alternate data stream syntax,
reserved Windows device names, trailing spaces or dots, and existing reparse
points are rejected. Reads are UTF-8 and bounded to 1 MiB by default. Search is
bounded during directory enumeration as well as file reads. Static checks
reduce accidental traversal but cannot eliminate races if another process
changes links or files concurrently. This dispatcher is not an operating
system sandbox.

In particular, `validate()` runs the project's code with the current user's
permissions. It may access files and resources outside the configured root;
the dispatcher does not claim to confine compiled code. Use a disposable
synthetic project for experiments, keep independent oracle files outside its
editable root, and do not run untrusted code on a machine whose permissions
would make that unsafe. Validation output is returned to the caller, so a test
program that prints a value can expose it even though the dispatcher does not
log environment values.

The executable acceptance suite creates an isolated temporary F# console
project and exercises path rejection, link rejection when the host supports
symbolic links, bounded deterministic search, hash-checked replacement,
metadata-only operation logs, and successful, failing, and timed-out fixed
validation commands:

```powershell
dotnet build experiments/AgentLang.Conventional/AgentLang.Conventional.fsproj
dotnet run --project tests/AgentLang.Conventional.Tests/AgentLang.Conventional.Tests.fsproj
```

The conventional tool foundation does not yet provide an agent loop, a
provider integration, a multi-file transaction, or a sandboxed build runner.
It supplies the repository operations and logs that a later experiment runner
can compose. The current synthetic project proves the tooling behavior, not
equivalence to the small-business language benchmark.

# Conventional repository tools

`AgentLang.Conventional` provides the file and validation operations for the
conventional F# baseline. It is a small deterministic host dispatcher intended
to give an external coding agent familiar repository-style tools while keeping
the benchmark's acceptance oracle outside the agent-editable project root.
It is separate from the language runtime and has no model-provider dependency.

The dispatcher exposes six closed operations: `inspect`, `read(path)`,
`search(query)`, `replace(path, expectedSha256, content)`,
`patch(path, expectedSha256, oldText, newText)`, and `validate()`. Requests use
canonical top-level fields alongside `op`, for example
`{"op":"read","path":"src/main.fs"}`. Each operation accepts exactly the
documented field set and returns a structured JSON result. Unsupported
operations and extra or malformed fields return structured errors. Each call
is appended to an ordered metadata-only operation log; the log omits source
contents, environment values, and process output.

`{"op":"inspect"}` returns a deterministic project overview. Its `operations`
array describes all six operations with `op` and `argumentSets`; each argument
set lists the accepted top-level fields with their names, string types, and
required status. `inspect` accepts either an empty argument set or a `path`
string, so callers can discover the project or inspect one file. The overview
also returns the configured relative `validationProject`, a bounded `files`
array of supported source paths, and `truncated`. It enumerates paths using
search's deterministic DFS and generated-directory skips, but does not read or
hash source files. The result and file caps use the configured search limits;
entry and depth limits also apply. `truncated` is set only when traversal
observes a path or bound candidate beyond the returned set. A directory with
more than the per-directory entry limit returns `SEARCH_DIRECTORY_LIMIT`.

`{"op":"inspect","path":"src/main.fs"}` preserves file inspection and
returns path, extension, byte and line counts, and a SHA-256 content hash.
`read` also returns the UTF-8 source text. The JSONL CLI continues to accept its
existing nested `args` object for compatibility, while the overview documents
the canonical top-level request fields.

`replace` is a definition-level whole-file replacement: callers must provide
the hash from a recent read. A
stale hash leaves the file unchanged. The replacement is written to a bounded,
temporary file, flushed, and moved over the target atomically. Use a fresh read
after replacement to verify the saved text and hash.

`patch` replaces one nonempty exact text anchor in the current UTF-8 file.
It requires a fresh file hash and exactly one ordinal match, including
overlapping matches. Use unique surrounding context to insert text; empty
`newText` deletes an anchor. There is no fuzzy matching or newline normalization.
Missing/ambiguous anchors return `PATCH_ANCHOR_NOT_FOUND` /
`PATCH_ANCHOR_AMBIGUOUS`; empty anchors return `PATCH_ANCHOR_EMPTY`.
Invalid output encoding and oversized output fail before writing. Surrounding
content, BOM, newlines and Unicode are preserved. A successful patch reuses the
atomic final hash check and returns normalized path, updated hash and bytes,
without echoing source. This reduces the full-file request payload imposed on
the original pilot; it does not establish agent efficiency by itself.

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
symbolic links, bounded deterministic search, hash-checked replacement/patching,
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

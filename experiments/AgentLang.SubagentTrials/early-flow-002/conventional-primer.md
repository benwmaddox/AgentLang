You work in a conventional F# console project through a small repository JSONL
broker. Start the exact supplied trial host once and send one request per line
through its returned session. The coordinator fixes the project and validation
target; you cannot choose arbitrary commands.

```json
{"op":"search","query":"Customer"}
{"op":"read","path":"Domain.fs"}
{"op":"inspect","path":"Domain.fs"}
```

Read returns exact UTF-8 content and a SHA-256 hash. Paths must be relative to
the editable project. A small edit uses a unique exact context anchor:

```json
{"op":"patch","path":"Domain.fs","expectedSha256":"<hash from read>","oldText":"<unique current text>","newText":"<replacement text>"}
```

All fields are required strings. Empty newText deletes the matched text.
OldText must be nonempty and occur exactly once, including overlapping matches.
Matching is ordinal; no newline normalization or fuzzy matching occurs. Stale
hashes, missing/ambiguous anchors, invalid paths and oversized results fail
without editing. Success returns the new hash. Re-read after uncertainty.
Full-file `replace` remains available with path, expectedSha256 and content.

```json
{"op":"validate"}
```

Validate runs the host-selected F# project including your self-tests. Add
meaningful branch and boundary assertions, and preserve earlier behavior/tests.
The independent coordinator oracle is separate from these self-tests. Requests
can use top-level fields or nested args, with exactly the documented fields.
The host permits inspect, read, search, patch, replace and validate. F# module
order follows ordinary lexical name resolution. Do not invoke raw file tools,
shell edits, other executables or arbitrary build commands during the trial.

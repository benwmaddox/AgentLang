# Test-file dependency replacements

Status: implemented in Flow/2. Focused checks, the documented example and all
37 full local Release checks pass; see [report 162](../reports/162-test-file-dependency-overlays.md).

Normal CLI `file.*` calls use real project-rooted files by default; explicit
`--filesystem virtual` simulates non-test execution too. Attached tests always use
fresh virtual files, including setup, expectations, publication and reload
qualification. A `test-file` adds typed dictionary replacements on top of that
provider isolation. Dictionary persistence is host tooling and still writes
project metadata; the tested language code does not access host files.

```flow
fn settings.load(path: String) -> Bool {
    effects fs.read
    doc "Reads an on/off setting."

    file.read(path) == "on"
}

test-file settings {
    override fn file.read(path: String) -> String {
        doc "Supply settings without reading host files."

        if path == "enabled.txt" { "on" } else { "off" }
    }

    test settings.load/enabled {
        settings.load("enabled.txt")
        => true
    }

    test settings.load/disabled {
        settings.load("disabled.txt")
        => false
    }
}
```

Submit this source through `define` with `frontend: "flow"` and
`syntaxVersion: 2`, then run `test settings.load`. Outside those tests,
`settings.load("enabled.txt")` reads the real file in the default real mode. The replacement does not
require a fixture file to exist. Standalone tests may instead use `file.write`
to populate their virtual filesystem, as shown in [library I/O testing](LIBRARY-IO-TESTING.md).

`override fn` is accepted only inside a `test-file`. A wrapper contains at least
one test and all its tests belong to one function. Its label is descriptive;
each nested test's full `owner/case` header selects its owner and case. Several wrappers can appear in an aggregate
project document. Each case receives fresh replacement dispatch and provider
state; passing tests, failed assertions and runtime errors all leave production
dispatch unchanged. Expectations execute separately with fresh virtual state.

Replacements have exactly the original input/output types and declare a subset
of its effects. Omitted effects mean pure. A pure fixture still retains the
original call's capability check: the example needs `fs.read` permission in
tests. Replacement does not grant permission or change production effects.

Nested calls and static list callbacks see the replacement. Dispatch uses the
original resolved identity and active revision, rather than its display name.
Fixture names and IDs are private to the ephemeral test program. Production
bodies, IDs, history and call bindings remain unchanged. This interpreter
implementation does not establish future JIT invalidation behavior.

Tests of a caller exercise its real body against the fixture. A fixture's body,
branches, inputs and returns never qualify the original function it replaces.
Replacing the tested function itself cannot make it library ready. Authored
library dependencies still need their own real-body qualification and tests.
Test results report `activeOverrides` separately from coverage.

The initial implementation supports authored functions and trusted primitives
with closed signatures, including `file.read`, `file.write` and `file.exists?`.
Generated operations and polymorphic primitives cannot be replaced. Fixture
calls follow ordinary Flow name resolution; use qualified names when ambiguous.

Manifest v4 preserves a complete wrapper as one shared test source object,
including stable target bindings for replacement headers and body calls. Reload
rechecks the current types and effects. Rename rewrites the bound header; case
removal rewrites the shared wrapper, dropping it when its last case is removed.
Replace an existing wrapped case by submitting its complete wrapper, rather
than splitting away its replacements.

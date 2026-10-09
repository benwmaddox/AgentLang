# Test-file dependency replacements

Status: design and implementation work in progress. The syntax below is a
proposal, not currently executable. Normal real filesystem operations and
automatic isolated test providers are a separate implementation step.

A test file may declare replacements for external dependencies, such as
`file.read`, or for an authored I/O wrapper. Production source cannot declare or
install these replacements. Every test in the file gets a fresh overlay and
fresh simulated provider state; one test's calls cannot leak changes to another.
Common file declarations are shared source, not shared mutable test state.

Conceptual source form:

```text
test-file settings {
    override fn file.read(path: String) -> String {
        effects fs.read
        doc "Return a fixture without reading the host filesystem."

        "on"
    }

    test settings.load/enabled {
        settings.load("settings.txt")
        => true
    }
}
```

The exact wrapper spelling remains to be resolved against the parser. The
required semantics are file scope, nested-call visibility, typed replacement,
automatic disposal and durable reload. A per-test-only implementation does not
complete the shared test-file requirement.

Validate the exact original target and input/output signature. Replacement
effects must be compatible with the original declaration; callers retain the
original effect contract and host capability requirements even when a fixture
returns a literal. Compile a test-specific dictionary/program so transitive
callers and static callbacks see the replacement. Run that closure interpreted
until a future JIT has correct generation invalidation.

Keep original production IDs, bodies, history and dispatch untouched. No fake
body, fake return observation or expected-value expression can qualify the
original function. A replaced target cannot qualify itself as library. Tests of
callers may exercise their real bodies against substituted dependencies, while
each library dependency still needs its own qualification against its real body.
Report active replacements separately from coverage.

Current persistence stores each test attachment independently. Do not silently
discard file-level fixtures when splitting a document into attachments. Retain
the full test-file context durably as a shared source object referenced by the
owning revisions. The existing test source refs can hold shared full-file text,
but each ref must be parsed as a file and its cases selected by owner and name,
not zipped one-to-one with attachments. Verify source spans, binding identities,
test selection and replacement scope after save/reload. Rewrite and case removal
must update the shared context atomically rather than split away its fixtures.

The first implementation slice will use Flow/2 and one tested owner per wrapper,
with multiple cases sharing its declarations. Standalone test declarations remain
valid. Overrides are test metadata and never enter the production source inventory.
Bindings for fixture-body calls need a distinct test-override role and structural
path rooted at the fixture declaration. Audit storage validation, serialization,
fingerprints and reload oracles together; an older manifest reader cannot safely
interpret a shared multi-case source object as one ordinary test. Rename and case
removal must rewrite the wrapper once and update all affected source references.
Bind each override header to its original target identity as well, even when the
fixture body is a literal. Reload verifies that identity and current signature
and effects; rename rewrites the header using the binding. The ephemeral dispatch
map pins the active revision for the duration of that test.

Implementation must preserve production call-binding identities. An ephemeral
test program can contain verified fixture bodies, with a test-only dispatch map
from original call identity to fixture body. Resolve that map before recording
an original function entry or return; otherwise a fake could supply finite-value
coverage. Check the original signature and effect contract before dispatch, and
record fixture execution separately. Original persisted IR and metadata remain
unchanged. Both direct calls and static callbacks must use that dispatch boundary.
Use distinct synthetic fixture function IDs in the ephemeral verified program.
Dispatch by the original compiled target identity (including user revision),
never by display name. The interpreter's common resolved-call boundary already
handles nested calls and static list callbacks; intercept there after original
effect preflight and before original entry/return instrumentation. The original
body and its coverage obligations remain present. Fixtures must have owner-keyed
source sites, rather than share the standalone test body's site namespace.
These are reviewed implementation constraints; the overlay is not yet implemented.

Acceptance must demonstrate real non-test reads/writes; zero host I/O during test
setup/body/expectations/publication; capability denial in both contexts; nested
and callback routing; matching signatures and effects; restoration after passing
tests, failed assertions and runtime errors; isolation between cases, files and
sessions; unchanged production dispatch after reload; and failure to qualify an
original function using its fake. Tests and documentation must distinguish
provider simulation from source-defined dictionary replacement.

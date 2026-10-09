# Authored effect-count assertions

Flow/2 tests can assert exact counts for the currently observable virtual
providers. The implementation is described in [report 113](../reports/113-effect-count-assertions.md).
The examples use current dotted calls and newline-separated statements. See
[current Flow/2 syntax](CURRENT-SYNTAX.md) for separator and formatting rules.

A test can assert the target function's returned value and its attempted virtual
provider calls:

```flow
fn marker.read(path: String) -> String {
    effects fs.read
    doc "Reads the stored marker contents."

    file.read(path)
}

test marker.read/existing {
    file.write("example/marker", "held")
    marker.read("example/marker")
    => "held" effects {
        fs.read: 1
        fs.write: 0
    }
}
```

The setup write is outside `marker.read`, so the assertion sees one read and
zero writes. A helper called by `marker.read` is inside its invocation and its
provider calls count too. Two target invocations aggregate their counts. Calling
the target during setup also counts: tests have no separate exercise marker.
Effects outside target invocations and pure expectation evaluation are excluded.

The first implementation observes `fs.read`, `fs.write`, `clock.read` and
`console.write`. Other categories are explicitly unsupported by this assertion
surface. An omitted supported category means zero; `effects {}` requires no
observed provider calls. The named target must actually run at least once.
Counts are nonnegative bounded integers, and duplicate categories are invalid.
Only Flow/2 tests for authored Flow functions support this suffix; examples and
Flow/1 tests do not.

`file.exists?` and `file.read` each count as `fs.read`. A typical existing-marker
branch that checks existence and reads the contents therefore expects two reads
and zero writes. `file.write` counts as a write even when the bytes are unchanged.
Attempts that raise provider errors still count. Capability rejection before the
target begins does not satisfy the invocation requirement.

These assertions do not grant capabilities or replace effect declarations.
Expectation expressions remain statically pure. A failed assertion fails the
test and consequently blocks library publication or replacement. Return/error
expectations and effect assertions are evaluated as separate verdicts so both
failures can be reported together. Tests without the suffix retain their existing
behavior.

This checks operation counts, not final provider contents, event order, external
IO, or every possible input. Keep independent state and domain acceptance tests.
Report 111's scripted extra-write control is the regression case: unchanged
return values must no longer hide an unwanted write from an opted-in test.

## Checking final virtual-file state

Use ordinary provider reads after the target call to observe known paths. Put
those reads in the actual test body; the expected value must remain pure.

```flow
fn configuration.publish(source: String, destination: String) -> Unit {
    effects fs.read, fs.write
    doc "Copies source contents to the destination."

    let contents = file.read(source)
    file.write(destination, contents)
}

test configuration.publish/known-final-state {
    file.write("config/source.cfg", "candidate-v2")
    file.write("config/destination.cfg", "stale")
    file.write("config/sentinel.cfg", "sentinel-safe")
    configuration.publish("config/source.cfg", "config/destination.cfg")
    let sourceAfter = file.read("config/source.cfg")
    let destinationAfter = file.read("config/destination.cfg")
    let sentinelAfter = file.read("config/sentinel.cfg")
    let withDestination = string.concat(sourceAfter, string.concat("|", destinationAfter))
    string.concat(withDestination, string.concat("|", sentinelAfter))
    => "candidate-v2|candidate-v2|sentinel-safe" effects {
        fs.read: 1
        fs.write: 1
    }
}
```

The three setup writes and three observation reads are outside the target, so
its expected counts remain one read and one write. Incorrect destination
contents can fail the value assertion even when those counts match. Writing the
correct contents twice can pass the value assertion while failing the count
assertion. These complementary controls are recorded in
[report 154](../reports/154-provider-state-assertion-probe.md).

This observes the named paths, not the entire provider map. It uses each test's
isolated virtual filesystem; it does not inspect production filesystem state.

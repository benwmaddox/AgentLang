# Testing library functions without real I/O

Normal `file.*` calls use real files in the project directory. Every attached
test uses a fresh isolated in-memory filesystem instead, including test setup
and tests rerun during library publication. Test file operations do not access
host files, even when the surrounding session uses real I/O.
The CLI may still write dictionary/project metadata when committing; that is
host tooling, separate from the language function's I/O.

The real function body executes. Its filesystem provider is simulated in tests.
The `effects fs.read` declaration and host capability checks still apply.

```flow
fn settings.enabled?(path: String, fallback: Bool) -> Bool {
    effects fs.read
    doc "Reads an on/off flag, using the fallback when the file is absent."

    if file.exists?(path) {
        file.read(path) == "on"
    } else {
        fallback
    }
}

test settings.enabled?/present-on {
    file.write("flag.txt", "on")
    settings.enabled?("flag.txt", false)
    => true effects {
        fs.read: 2
        fs.write: 0
    }
}

test settings.enabled?/present-off {
    file.write("flag.txt", "off")
    settings.enabled?("flag.txt", true)
    => false effects {
        fs.read: 2
        fs.write: 0
    }
}

test settings.enabled?/absent-false {
    settings.enabled?("flag.txt", false)
    => false effects {
        fs.read: 1
        fs.write: 0
    }
}

test settings.enabled?/absent-true {
    settings.enabled?("flag.txt", true)
    => true effects {
        fs.read: 1
        fs.write: 0
    }
}
```

The setup `file.write` populates the in-memory provider. It is outside the
target invocation, so it does not count toward the target's `fs.write: 0`
assertion. Existence checking and reading each count as one read. The missing
file tests also demonstrate that another test's setup does not leak into them.

## Library qualification

Send `define` with the source above and `syntaxVersion: 2`, then:

```json
{"op":"test","word":"settings.enabled?"}
{"op":"describe","word":"settings.enabled?"}
{"op":"commit","word":"settings.enabled?","library":true}
```

The host must allow `fs.read` and `fs.write` for the test and its setup. A test's
isolated provider does not grant capabilities. Normal CLI execution uses real
files by default; `--filesystem virtual` explicitly simulates an entire session
when needed for experiments. That option is unnecessary for attached tests.

Use `--allow fs.read --test-allow fs.read,fs.write` when production should only
read, but the isolated tests need to write fixture files. The test grants cannot
authorize a real write. Without `--test-allow`, tests use the normal grants.
Library requalification on project reload also uses these explicit test grants.

The gate reruns the attached tests against the proposed dictionary. This
example exercises both branches, every executable instruction, both values of
the `fallback` parameter, and both Bool return values. Its dependencies are
trusted primitives. An authored helper would need its own library qualification
and tests; mocking it could not qualify its original body.

The `effects { ... }` assertions are opt-in behavior checks. Complete library
coverage and dependency qualification are mandatory; exact I/O counts are
required here because these tests explicitly assert them.

Verified results for this example:

- All four tests pass, and the function commits with `maturity: library`.
- Keeping only `present-on` gives a passing test but publication fails with
  `LIBRARY_COVERAGE_INCOMPLETE`: the absent-file branch is untested.
- Adding an unnecessary extra read preserves the return values, but the two
  existing-file tests fail their effect counts and commit fails with
  `COMMIT_TESTS_FAILED`.

Coverage is necessary, not a proof of correctness. These tests cannot establish
that a future real filesystem adapter handles operating-system errors,
concurrency, permissions or encoding correctly. That adapter needs separate
integration tests. Counts also do not prove contents or ordering; see
[effect assertions](EFFECT-ASSERTIONS.md) for final virtual-state checks.

## Replacing dependencies in test files

The example above executes real function bodies with a virtual provider.
Flow/2 test-file replacements additionally let a test file define a typed
fixture for an authored I/O wrapper or a closed-signature primitive such as
`file.read`. Nested calls and static callbacks see it, with automatic cleanup.
The original signature and capability contract remain checked. A fixture
cannot supply coverage or finite-value observations for the original function
it replaces; a library dependency still needs its own qualification.

See [test-file dependency replacements](TEST-FILE-DEPENDENCIES.md) for the
source form, current validation status and shared-source persistence rules.
Normal execution reads and writes real files; both provider-based tests and
replacement-based tests keep language filesystem I/O virtual.

See [library testing rules](TESTING.md), [finite coverage](FINITE-COVERAGE.md),
and [the syntax milestone report](../reports/155-dot-calls-and-newline-syntax.md)
for validation evidence.

# Current Flow/2 syntax

Select Flow/2 with `syntaxVersion: 2` in runtime requests or
`--syntax-version 2` in the CLI. Flow/1 remains the default when the version is
omitted. Keep the selected version consistent for definitions, tests, and
replacements.

## Dotted calls, properties, and bound receivers

Flow/2 uses dots for namespace calls and generated constructors. A plain record
property is also written with a dot, but has no call parentheses:

```flow
record Customer {
    field balance: Int
    field email: String
}

fn customer.active?(customer: Customer) -> Bool {
    customer.email == "active"
}

fn customer.read-balance(customer: Customer) -> Int {
    customer.balance
}

fn customer.current-balance(customer: Customer) -> Int {
    customer.read-balance()
}

test customer.current-balance/example {
    customer.current-balance(customer.new(balance = 42, email = "active"))
    => 42
}
```

Here `customer.email` and `customer.balance` read declared properties.
`customer.read-balance()` resolves the bound-root function
`customer.read-balance` and passes the local `customer` record as its first
argument. An unbound namespace call supplies its arguments explicitly, as in
`file.read(path)`. Generic constructors use the same dotted form:
`option.some<Int>(value)` and `result.ok<Int, String>(value)`.

Static list callback references name dictionary functions, for example
`values.map(customer.active?)`; they cannot capture caller-local values. In
ordinary call and property expressions, a local root name takes precedence
over the same namespace root. If resolution through that local fails, lookup
does not fall back to the namespace. Prefix a dictionary root with a dot to
select it explicitly despite a shadowing local:
`.customer.read-balance(customer)`. The same form selects an exact unqualified
name, as in `.identity(value)`.

Discovery metadata may include `flowReference` and
`flowReferenceSyntaxVersion`. Follow the reported version when copying a
reference. When `flowReferenceSyntaxVersion` is 2, references use leading-dot
exact roots such as `.customer.read-balance(customer)` and `.identity(value)`;
this keeps them unambiguous when a local name could shadow the namespace.

Keep a postfix dot on the same line as its receiver. A dot at the start of the
next line begins a new exact-root call, even inside grouping; for example,
`.file.write(...)` on a new line is a separate call, not a continuation of the
previous expression. Call argument lists can span lines.

Flow/2 still accepts older `::` call spellings when reading source. New Flow/2
source and formatted output use dots. Flow/1 help and examples retain their
version-1 spellings.

## Newlines and optional semicolons

Newlines separate function and test statements, record fields, validator
declarations, enum cases, and effect-count entries. Multiple entries on one
line need semicolons; the last entry before a closing delimiter may omit its
semicolon. The formatter omits optional semicolons. For example, these fields
are separated by a newline:

```flow
record Coordinate {
    field x: Int
    field y: Int
}
```

Effects and documentation belong before executable code and are followed by a
blank line. An omitted function effect declaration means the function is pure:

```flow
fn marker.read(path: String) -> String {
    effects fs.read
    doc "Reads the marker contents."

    file.read(path)
}
```

## Match arms

A single-expression arm can omit its block. Keep a block when an arm has
multiple statements:

```flow
enum RenewalState {
    case pending
    case renewed
}

fn renewal.label(state: RenewalState) -> String {
    match state {
        pending => "Pending"
        renewed => {
            let label = "Renewed"
            label
        }
    }
}

test renewal.label/pending {
    renewal.label(RenewalState.pending())
    => "Pending"
}
```

See [closed enums](CLOSED-ENUMS.md) for exhaustiveness and enum construction,
and [authored effect assertions](EFFECT-ASSERTIONS.md) for test-only effect
counts.

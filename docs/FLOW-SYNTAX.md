# Flow syntax foundation

This document describes the first, opt-in authoring frontend. It lowers into
the existing checked expression tree and verified IR; it does not add a second
runtime. The legacy stack parser and Runtime protocol remain unchanged in this
slice, and Flow source is not yet durable project source.

```text
word customer.discounted-balance(customer: Customer) -> Float {
    effects none
    doc "Applies the current premium discount policy."
    let balance = customer.balance();
    if customer.premium?() {
        balance.multiply(0.9)
    } else {
        balance
    }
}
```

Definitions use named, typed parameters and an ordered, nonempty output vector.
The existing scalar form `-> Int` remains canonical for one output; multiple
outputs are written `-> (Int, String)`. These parentheses describe the output
vector and do not introduce tuple values. Locals are immutable; branches have
lexical scope, and each branch must produce the same output types in the same
order. A branch-local name cannot escape. Calls are ordinary statically resolved
word calls. `value.stage(args)` supplies `value` as the stage's first input and
evaluates it once before explicit arguments. Calls and stages reject unresolved
or ambiguous names instead of falling back to a different interpretation.

For an unqualified dictionary identity, `::identity(value)` is an
absolute-root call that selects only the exact key `identity`. This is useful
when both `identity` and `math.identity` exist. The forms `identity(value)` and
`word identity` retain short-name lookup and remain ambiguous in that case;
`math::identity(value)` selects `math.identity`. Absolute-root names contain
one identifier only, so `::math::identity(value)` is rejected rather than
silently becoming another qualification form. Root calls accept positional or
named arguments and can be chained as receivers, for example
`::identity(value).abs()`.

Destructure every output at once with `let (...)`; each binding retains the
source span of its name and stores values in reverse stack-pop order so the
declared order is preserved:

```text
word split(value: Int) -> (Int, String) {
    effects none
    return (value, "label")
}

word use(value: Int) -> String {
    effects none
    let (number, label) = split(value)
    return label
}
```

`return value` and `return (first, second)` finish the current lexical block
with one or more scalar results. A Return is terminal within its word, branch,
or match-case block, but a branch Return supplies the value of its enclosing
expression: a following statement after `let (...) = if ...` continues in the
outer block. Ordinary `let`, argument, receiver, condition, payload, and
isolated-expression positions still require exactly one output. Multi-output
calls never spread implicitly. An effect-only statement should call a word
whose signature declares the single output `Unit`.

Standalone Flow tests and examples use the same expression syntax and have an
explicit terminal expectation:

```text
test customer.renew/basic-success {
    customer::renew(customer::example-eligible())
    => true
}

test customer.renew/value-expectation {
    customer::balance(customer::example-premium())
    => value money::from-cents(9000)
}

test customer.renew/denied {
    customer::renew(customer::example-ineligible())
    => error RENEWAL_NOT_ALLOWED
}

example invoice.total/basic {
    invoice::total(invoice::example-small())
    => 42.50
}
```

The slash separates the canonical dotted owner name from the case name. A
test accepts a literal, a stable runtime error code, or a separately compiled
pure value expression; an example accepts a literal only. The actual test body
must produce one value for literal and value-expression assertions, and the
expected expression must produce the same closed type. Actual and expected
bodies retain separate executable traces, source origins, and coverage credit.
The Flow parser stops the top-level body at `=>`; nested match-arm `=>` tokens
remain part of the nested expression. `FlowParser.parseTest` and
`FlowParser.parseExample` parse these as standalone source objects, with
deterministic renderers on `FlowSource`.

`map`, `filter`, and `each` also accept one static callback reference:

```text
customers.map(customer::normalize)
customers.filter(customer::active?)
customers.each(email::send)
customers.map(word normalize)
customers.map(::normalize)
```

A namespace-qualified reference uses `::`, a short callback name must use
`word name`, and `::name` selects one exact root dictionary key. For example,
`customers.map(::normalize)` is exact even if `customer.normalize` also exists.
The reference is dictionary metadata, not a value or local variable, and is
valid only as the sole positional argument to one of these three list stages.
Ordinary arguments such as `.map(localValue)` remain normal calls and are
never reinterpreted as callback names. Resolution selects a dictionary word by
identity first, then checks that it accepts one list element and returns one
value. `map` returns a list of the callback output type, `filter` requires a
`Bool` result and preserves the input element type, and `each` requires `Unit`
and returns `Unit`. Callback dependencies and effects are checked even for an
empty list; no callback executes until effect preflight succeeds.

The source namespace separator is `::`; dictionary identities use dots. For
example, `billing::invoice::total(invoice)` resolves to
`billing.invoice.total`. A dot stage is intentionally a distinct syntax:
`invoice.total()` means the first-input stage named `total`, never a qualified
word. Its name and input type must resolve uniquely.

The frontend handles scalar, record/refinement, ordinary, named, and dot calls,
`if` expressions, typed container constructors, exhaustive `Option` and
`Result` matches, and statically named list callbacks. It has no closures,
infix operators, reassignment, globals,
implicit method dispatch, or arbitrary .NET calls. Named arguments require
parameter metadata; authored Flow words provide it directly, record
constructors use declared field names, and trusted words require an explicit
catalog entry. Named argument expressions execute once in written order even
when local temporaries are needed to bind them in declaration order.

Container constructors require explicit, closed type arguments. The six
recognized forms are:

```text
list::empty<T>()
list::singleton<T>(value)
option::none<T>()
option::some<T>(value)
result::ok<T, E>(value)
result::error<T, E>(value)
```

`T` and `E` may be built-in types, declared record/refinement types, or nested
`List`, `Option`, and `Result` types. Empty and inactive values keep the
declared types at runtime; they are never inferred from a payload. The exact
qualified names above are syntax forms. Other names in those namespaces remain
ordinary statically resolved calls.

Matches cover both cases and accept either source order. Canonical rendering
puts `some` before `none`, and `ok` before `error`:

```text
match maybe-email {
    some address => {
        address.value()
    }
    none => {
        "missing"
    }
}

match renewal {
    ok receipt => {
        receipt.id()
    }
    error problem => {
        0
    }
}
```

Each arm must return one or more values, and both arms must return the same
closed output vector in the same order. A payload name exists only inside its arm and cannot shadow an
outer local. Result arms may use the same spelling because their bindings are
independent. Every arm is type-checked and contributes its calls and effects,
even when a particular runtime value selects the other arm. Branch coverage
records both outcomes (`some`/`none` or `ok`/`error`) for library-quality gates.

The parser accepts LF, CRLF, and lone-CR line endings and records spans from
absolute UTF-16 source offsets. Parser safety limits are one million source
code units, one hundred thousand tokens, one hundred thousand code units per
quoted string, 128 expression/type nesting levels, and 100,000 expanded
syntax nodes per Flow expression or word. The shared budget includes expression
nodes, type nodes (including every declared output), each destructuring name,
and every expression member in a return vector. The completed expression AST is
traversed iteratively before recursive lowering or rendering, so postfix
receiver chains and nesting combined across calls, conditionals, matches, and
container types share the same depth bound. Signature roots, statement blocks,
and binding names are streamed through this bounded walk rather than copied
into an unbounded intermediate collection. The same structural check runs at
public rendering and lowering entry points, so host-built Flow ASTs receive
both bounds before any recursive traversal too.

`FlowParser.parseWord`, `FlowParser.parseExpression`, `FlowParser.parseTest`,
and `FlowParser.parseExample` are explicit entry points. A Flow parse error
never invokes the legacy parser. The source renderer is deterministic, but
canonical Flow text is currently inspection/test metadata, not a storage
format. Standalone test/example parsing, lowering, and compilation exist in
Core; generated lexical `Scope` operations remain internal lowered
expressions. Durable source, Runtime attachment dispatch, rename, reload, and
protocol cutover remain later migration work.

Focused checks are run with:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release
dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release
```

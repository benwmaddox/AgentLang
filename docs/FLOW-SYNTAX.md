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

Definitions use named, typed parameters and one output. Locals are immutable;
branches have lexical scope, and each branch must produce one value of the same
type. A branch-local name cannot escape. Calls are ordinary statically resolved
word calls. `value.stage(args)` supplies `value` as the stage's first input and
evaluates it once before explicit arguments. Calls and stages reject unresolved
or ambiguous names instead of falling back to a different interpretation.

The source namespace separator is `::`; dictionary identities use dots. For
example, `billing::invoice::total(invoice)` resolves to
`billing.invoice.total`. A dot stage is intentionally a distinct syntax:
`invoice.total()` means the first-input stage named `total`, never a qualified
word. Its name and input type must resolve uniquely.

The first frontend handles scalar, record/refinement, ordinary, named, and dot
calls plus `if` expressions. It has no closures, infix operators, reassignment,
globals, implicit method dispatch, or arbitrary .NET calls. Named arguments
require parameter metadata; authored Flow words provide it directly, record
constructors use declared field names, and trusted words require an explicit
catalog entry. Named argument expressions execute once in written order even
when local temporaries are needed to bind them in declaration order.

The parser accepts LF, CRLF, and lone-CR line endings and records spans from
absolute UTF-16 source offsets. Parser safety limits are one million source
code units, one hundred thousand tokens, one hundred thousand code units per
quoted string, and 128 nested constructs. These limits are checked before
unbounded syntax work.

`FlowParser.parseWord` and `FlowParser.parseExpression` are explicit entry
points. A Flow parse error never invokes the legacy parser. The source renderer
is deterministic, but canonical Flow text is currently inspection/test
metadata, not a storage format. In particular, generated lexical `Scope`
operations are internal lowered expressions and are not added to the legacy
source parser. Durable source, test/example integration, rename, reload, and
protocol cutover remain later migration work.

Focused checks are run with:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release
dotnet run --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj -c Release
```

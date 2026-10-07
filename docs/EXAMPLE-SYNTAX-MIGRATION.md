# Examples for the revised function syntax

Status: Flow/2 implementation; select `syntaxVersion: 2` or CLI `--syntax-version 2`.
The implementation boundary is described in [report 085](../reports/085-frontend-library-revision-plan.md).
The customer, refined-types and containers teaching files in `examples/` use
this version. Business fixture files remain Flow/1 compatibility fixtures.

## Customer example

This is the intended replacement for the teaching example in
`examples/customer.agent`. Record declarations and attached test syntax remain
as they are today; function declarations, field access and equality change.

```text
record Customer {
    field kind: String;
    field balance: Float;
}

fn customer.premium?(customer: Customer) -> Bool {
    doc "Whether this customer has premium status."

    customer.kind == "premium"
}

fn customer.discounted-balance(customer: Customer) -> Float {
    doc "Apply a 10% discount to premium customers."

    let balance = customer.balance;
    if customer.premium?() {
        float::multiply(balance, 0.9)
    } else {
        balance
    }
}

test customer.premium?/premium {
    customer::premium?(customer::new(kind = "premium", balance = 100.0))
    => true
}

test customer.premium?/regular {
    customer::premium?(customer::new(kind = "regular", balance = 100.0))
    => false
}

test customer.discounted-balance/premium {
    customer::discounted-balance(customer::new(kind = "premium", balance = 100.0))
    => 90.0
}

test customer.discounted-balance/regular {
    customer::discounted-balance(customer::new(kind = "regular", balance = 100.0))
    => 100.0
}

example customer.discounted-balance/premium {
    customer::discounted-balance(customer::new(kind = "premium", balance = 100.0))
    => 90.0
}
```

`customer.kind` reads a plain record field. `customer.premium?()` calls a
dictionary function with the receiver as its first argument. These remain
different operations, both resolved and type checked before execution.
`==` compares values of the same compatible type; it does not unwrap nominal
types or convert between them.

This retains the small Float demonstration, not the exact-money business
fixture. Real business examples retain nominal `Money`, signed integer minor
units, and checked arithmetic. Email and physical-unit types also retain
explicit construction and unwrapping; the syntax change weakens no types.

## Metadata layout and library intent

Omitted effects mean `none`. If written explicitly, effects precede documentation,
with a blank line separating metadata from executable code:

```text
fn tutorial.positive?(value: Int) -> Bool {
    effects none
    doc "Whether the value is greater than zero."

    value.greater-than(0)
}
```

Ordinary `fn` definitions can be development helpers or entry functions.
The planned `library fn` declaration expresses intent to qualify as reusable
library vocabulary. It does not bypass test, coverage, dependency or publication
gates. The current runtime still qualifies functions through commit commands;
the new declaration and stricter library policies are pending implementation.
See [the revision plan](../reports/085-frontend-library-revision-plan.md).

## Migration boundary

| Example surface | Update policy |
| --- | --- |
| New-syntax teaching examples | Use this preview and identify it as pending implementation |
| README quickstart and `examples/*.agent` | Keep executable Flow/1 until the new frontend passes validation |
| Runtime help and generated source | Change alongside the parser, lowering and source-version contract |
| Business examples | Migrate without changing strong types, effects or task policies |
| `examples/legacy` and historical trial evidence | Preserve their original syntax and bytes |
| Frozen trial fixtures and source snapshots | Preserve; use separately versioned inputs for later studies |

Before making the revised examples executable, validate field/method resolution,
strict typed equality, default pure effects, explicit effect rejection, metadata
spacing and formatting idempotence. Then run the migrated examples through
define, tests, commit and reload, and check historical source-version loading.
Documentation previews alone do not establish those implementation results.

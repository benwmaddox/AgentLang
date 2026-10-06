# Closed container types

The prototype provides three built-in parameterized types: `List<T>`, `Option<T>`, and `Result<T, E>`. Their parameters are closed, declared types. User words cannot introduce generic variables. Container types can nest in signatures and record fields, for example `Result<List<Int>, String>` or `Option<Email>`.

The type checker preserves nominal types exactly. `List<Email>` does not accept `List<String>`, even when `Email` wraps a string. Both sides of a `Result<T, E>` remain part of its type regardless of which side currently holds a value. Empty and inactive cases therefore require explicit type arguments:

Type-generated constructors and accessors must have unique names. A candidate type is rejected with `NAME_GENERATED_COLLISION` if generated names overlap each other, a user word, a primitive, or a reserved syntax form. This prevents declaring an accessor such as `list.map` that source parsing would always treat as syntax.

```agentlang
list::empty<Int>()
option::none<Email>()
result::error<List<Int>, String>("offline")
```

Payload constructors check the value before execution:

```agentlang
list::singleton<Int>(42)
option::some<Email>(Email::new("dev@example.com"))
result::ok<Int, String>(7)
```

`list.count`, `list.append`, `list.concat`, `list.get`, and `list.is-empty?` are trusted dictionary words. `list.get` returns a typed `Option<T>`; an out-of-range index keeps the list element type. Concatenation requires both lists to have the same exact element type. Every list is capped at 10,000 values. An append or concatenation that exceeds this limit reports `RUNTIME_VALUE_LIMIT` before allocating the result.

The higher-order forms take one statically named word. They do not evaluate arbitrary quotations or dynamically selected callbacks:

```agentlang
customers.map(customer::email)     # Customer -> Email; returns List<Email>
customers.filter(customer::active?) # Customer -> Bool; returns List<Customer>
emails.each(email::send)            # Email -> Unit; returns Unit
```

The compiler checks the callback signature and unions its effects into the enclosing word before execution, including for an empty list. Callback words are included in dependency and caller metadata. `list.map`, `list.filter`, and `list.each` are language syntax forms rather than executable dictionary entries; `describe`, `source`, `search`, and `words.constructs` expose their signatures, callback rules, effects, and coverage outcomes with `kind: "syntax"`.

Use explicit match blocks for safe case handling:

```agentlang
match option::some<Int>(7) {
    some value => { value }
    none => { 0 }
}

match result::ok<Int, String>(7) {
    ok value => { value }
    error reason => { 0 }
}
```

Every case is required and must return the same output types without changing outer locals. Case payload locals exist only in their own case; they cannot shadow an outer local, and they are removed when the case ends. Case bodies are type checked even when a particular value would not select them at runtime.

Library words must pass their own tests across every branch outcome. Coverage reports also distinguish the control-flow outcomes relevant to containers:

- `match-option`: `some`, `none`.
- `match-result`: `ok`, `error`.
- `list.map` and `list.each`: `empty`, `nonempty`.
- `list.filter`: `empty`, `nonempty`, `keep`, `drop`.

An iteration outcome records whether the instruction saw an empty or nonempty list; filter `keep` and `drop` outcomes are recorded for individual elements. Tests attached only to a callback do not count as coverage for its caller. These deterministic outcomes give a clear minimum coverage contract, but they do not prove correctness for every possible input or value. `describe <word>` reports the currently observed coverage and the missing source locations/outcomes.

The examples above use Flow. Explicit Stack authoring retains its legacy forms;
see [the preserved Stack example](../examples/legacy/containers.agent).

The runnable Flow example is [containers.agent](../examples/containers.agent); it expects the nominal `Email` type from [refined-types.agent](../examples/refined-types.agent).

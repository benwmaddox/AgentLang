# Next bounded strong-type agent trial

Status: planned after refactoring checkpoint 069; no actor result yet. A read-only
Luna/max planning pass identified an executable trial using existing language
features. No new runtime feature or full-domain expansion is a prerequisite.

Seed the illustrative refined `Email` and distinct Float-backed
`MetersPerSecond`/`KilometersPerHour` wrappers, with an existing Delivery record
whose fields require Email and MetersPerSecond. Ask a fresh external agent to
inspect those capabilities and add a tested pure `delivery.speed-kph : Delivery
-> KilometersPerHour`. Conversion must explicitly unwrap, multiply by 3.6 and
wrap the result. Preserve the seeded types and frozen validator closure.

Authoritative syntax from [Flow Runtime acceptance](../tests/AgentLang.Flow.Runtime.Tests/Program.fs#L2271)
and [the example](../examples/refined-types.agent):

```text
type Email : String {
    validate email::valid?;
}
type MetersPerSecond : Float { }
type KilometersPerHour : Float { }
record Delivery {
    field contact: Email;
    field speed: MetersPerSecond;
}
```

Construct with `Email::new(...)`, explicitly unwrap with `Email::value(...)`,
and use analogous unit constructors/accessors. Invalid refined construction
reports `REFINEMENT_FAILED`; Flow tests can assert `=> error REFINEMENT_FAILED`.
Expected nominal values use `=> value KilometersPerHour::new(...)`. The validator
must have String -> Bool signature and no direct or transitive effects. Its
transitive word closure is frozen once the type persists; the same rule is
verified on load. Existing examples deliberately use a small email policy rather
than implementing the Internet email standard, and unit wrappers have no range
refinement. State those policies explicitly in the fixture.

Before actor launch, freeze behavior vectors, prompt/source/binary/host pins and
the starting tree. Require own library coverage and passing attached tests,
then independent conversion vectors after durable reload. Separately submit
ill-typed definitions on a disposable acceptance copy: wrong unit for the
record's speed field, plain String for contact, and a typed Int where Float is
required. Require structured type errors with no partial staging. Check invalid
Email construction at runtime, unchanged seeded types/validator history and no
implicit unit conversion. Do not treat nominal wrapping as unit algebra or
range validation.

Use subagents, not a model API integration. Actual model tokens/turns and a
matched conventional comparison remain separate future evidence. After this
small cross-unit trial, expand toward the required strong business domain and
stateful evaluation; LLVM and memory research stay deferred.

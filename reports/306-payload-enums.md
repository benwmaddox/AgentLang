# 306 — Item 2a: enums whose cases carry a payload

Item 2 is typed routes: a route table declared as an enum, one case per route,
each case carrying its path parameters, with `at "/rooms/{room}"` paths. Flow
enums had no payloads, so the item runs in three steps:
- **(a)** Enums whose cases carry a value, in the language and the interpreter.
  Native builds reject them with a diagnostic.
- **(b)** Native builds lay payload enums out as a true sum.
- **(c)** `at` routes, and Campfire's router built on them.

## (a) The language

```flow
enum Route {
    room(RoomId)
    around(RoomAround)
    sidebar
}

fn campfire.route-name(route: Route) -> String {
    match route {
        room id => string.concat("room ", int.to-string(RoomId.value(id)))
        around place => "around"
        sidebar => "sidebar"
    }
}

let route = Route.room(RoomId.new(5))
```

**One payload value per case,** written the way `Option` and `Result` are:
- A case declares at most one payload type: `room(RoomId)`. A case with
  several fields uses a record, as `around(RoomAround)` does.
- A match arm binds the payload with a name, `room id =>`, just as `some value`
  does. Or it ignores the payload, `room =>`.
- Binding a case that has no payload is an error (`FLOW_MATCH_ENUM_BINDING`).
- The constructor takes the payload: `Route.room(id)`. A bare case is built as
  before, `Route.sidebar()`.
- Equality compares the case, then the payload.
- Payload enums can sit in records, lists and options like any other value.

Multi-field cases, such as `room(id: RoomId, at: Int)`, could be added later as
sugar over a generated record. They are not needed for routes.

**How it runs.** The payload lives in every layer, with nothing desugared, so
errors and test failures show the real types:
- The Core enum definition records one optional payload type per case.
- Values are `EnumValue(type, case, payload)`.
- An IR `MatchEnum` arm can bind its payload to a local, as `MatchOption`'s
  some-arm does. The verifier checks that:
  - only a payload case binds;
  - the local has the payload's type;
  - the local does not escape the arm.
- The constructor's generated target takes the payload as its input.
- The interpreter carries the payload in its runtime value. Values a host
  passes in are checked against the declared payload: a payload case without
  its value is rejected, and so is a value on a bare case.

**Payload-free enums are unchanged:**
- Their source text, fingerprints, IR JSON and native layout are byte-for-byte
  what they were.
- A binding adds fingerprint text only when an arm has one, so stored
  dictionaries and existing words keep their identities.

**Native builds** reject a payload enum, with `IR_OWNING_STACK_ENUM_PAYLOAD`,
until (b). The legacy LLVM backend already rejects every enum.

**Checks:**
- A new suite, `AgentLang.PayloadEnums.Tests`, has 116 assertions:
  - syntax: payload declarations, binding and ignoring arms, and formatting
    that round-trips;
  - type errors: a binding on a bare case, a payload of the wrong type, a
    missing payload, a missing case, and a payload used as the wrong type;
  - every attached test of a route enum with Int, record, `Option` and `List`
    payloads plus a bare case;
  - interpreter entry points with payload values in and out, including an
    81-pair equality matrix;
  - payload enums inside a record, a list and an option;
  - host values with a missing or unexpected payload, both rejected;
  - the native rejection.
- **Suites:** all 23 pass. The gate now includes the new suite, plus the
  value-inspection, storage, discovery, vocabulary and source suites that the
  changed types touch.
- **Campfire:** unchanged. The verified build takes 39.2 s, and its
  `module.ll` is byte-identical to the item-1b build.

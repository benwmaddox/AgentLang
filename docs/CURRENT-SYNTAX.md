# Current Flow/2 syntax

Select Flow/2 with `syntaxVersion: 2` in runtime requests or
`--syntax-version 2` in the CLI. Flow/1 remains the default when the version is
omitted. The selected version applies to source supplied in that request.
Retained tests and examples keep their own source version when a function is
replaced; they are checked against the new function without translation. A
submitted document still uses one selected syntax version throughout.

Payload-free enum cases are constructor calls, including their empty argument
list: `PublishOutcome.created()`. They are different from record properties such
as `customer.email`. An exact-root call also keeps its parentheses:
`.PublishOutcome.created()`.

An attached test compares a constructed value with a value-expression expectation:

```flow
test configuration.publish-safely/created {
    file.write("source.txt", "configuration")
    configuration.publish-safely("source.txt", "destination.txt")
    => value PublishOutcome.created()
}
```

Bare `=>` accepts supported literals; it cannot precede an enum constructor.
The expected expression does not supply target-function coverage. Runtime
`help` with topic `define` or `examples` and syntaxVersion 2 includes the complete
`tutorial-enum-tests` source and executable define/test/library-commit requests.

For example, request authoring help with
`{"op":"help","syntaxVersion":2,"topic":"define"}`. The `frontend` field
belongs on definition/evaluation requests; it is not accepted by `help`.

## Dotted calls, properties, and bound receivers

Flow/2 uses dots for namespace calls and generated constructors. A plain record
property is also written with a dot, but has no call parentheses:

```flow
record Customer {
    balance: Int
    email: String
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
An explicit leading dot is accepted for the six built-in generic container
constructors, for example `.option.none<Int>()` and `.list.singleton<Int>(1)`.
They use the same typed constructor semantics; formatting removes the redundant
leading dot. Empty-payload constructors require `()`.

Static list callback references name dictionary functions, for example
`values.map(customer.active?)`; they cannot capture caller-local values. In
static callback position, `.customer.active?` selects the same dictionary name
as `customer.active?`; formatting uses the latter spelling. The one-segment
exact-root callback `.identity` retains its leading dot. In
ordinary call and property expressions, a local root name takes precedence
over the same namespace root. If resolution through that local fails, lookup
does not fall back to the namespace. Prefix a dictionary root with a dot to
select it explicitly despite a shadowing local:
`.customer.read-balance(customer)`. The same form selects an exact unqualified
name, as in `.identity(value)`.

`items.map(context, word)` passes a context value to every call of a static
word reference. The context is evaluated once and borrowed: each call receives
the same value unchanged, and the word's first parameter has the context type.
The result is `List<U>`, where `U` is the word's output type. Only `map` takes a
context; `filter` and `each` with a context raise `FLOW_CALLBACK_CONTEXT_MAP_ONLY`.
For example:

```
record PageContext {
    prefix: String
}

fn page.item(context: PageContext, name: String) -> Html {
    effects none
    html"<li>{context.prefix}{name}</li>"
}

names.map(context, page.item)
```

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
    x: Int
    y: Int
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

## Names

A word name is a dotted path of segments, such as `campfire.views.room-page`;
dots separate namespace segments. A segment starts with a letter or `_` and
continues with letters, digits, `-`, `_`, `?` or `!`, so a name never contains
`(` or `)`. Type names are PascalCase identifiers.

## Enums

An enum lists its case names, one per line or separated by `;`, like record
fields. Payload-free cases are built with a constructor call such as
`RenewalState.pending()`. The older `case pending` spelling is still accepted
while sources migrate; the formatter writes bare names.

## Match arms

A single-expression arm can omit its block. Keep a block when an arm has
multiple statements:

```flow
enum RenewalState {
    pending
    renewed
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

## Native refinement boundary

The selected owning-native backend preserves distinct nominal identities for
Int and String wrappers. A String-backed refinement uses its
frozen pure String -> Bool validator at construction and external entry, also
inside active record, Option and Result payloads. The bounded NonEmptyString
example is not a general Email validator. Refined mailbox admission is covered
by [report 180](../reports/180-native-refined-mailbox.md); String wrappers without
a predicate and their remaining validation status are tracked in
[report 183](../reports/183-native-nominal-string.md). This is a backend extension;
the authoring syntax and library qualification rules are unchanged. See the
[native typing scope](NATIVE-NOMINALS-IMPLEMENTATION.md) and
[report 174](../reports/174-native-refined-string.md) for the original refinement slice.

## Strings are UTF-8

Strings hold Unicode text and are stored as UTF-8 in native builds.

| Primitive | Returns |
| --- | --- |
| `string.byte-length(s)` | the length in UTF-8 bytes (constant time) |
| `string.char-length(s)` | the number of Unicode characters (scalar values) |
| `string.length(s)` | deprecated alias of `string.byte-length`, removed by 2026-11-01 |

Text with an unpaired UTF-16 surrogate has no UTF-8 form and is rejected
wherever it enters:

- **source literals:** `FLOW_INVALID_STRING`;
- **interpreter inputs and host values:** `STRING_INVALID_UTF16`;
- **native inputs:** `STRING_INVALID_UTF16`.

Search and escaping work on whole characters.

## List literals

`[a, b, c]` builds a `List<T>` from one or more elements in order. Every element
must have the first element's exact type, and a trailing comma is allowed. An
empty list is written `list.empty<T>()`:

- `[]` is rejected with `FLOW_LIST_LITERAL_EMPTY`;
- mixed element types are rejected with `FLOW_LIST_LITERAL_TYPE`.

A native build sizes the list once and copies each element once. Lists have no
element-count limit. `list.append` and `list.concat` copy each element once
into a sized result.

```flow
fn row(name: String) -> String {
    effects none
    string.join(["<li>", string.html-escape(name), "</li>"])
}
```

## Refined literals and Html

A refined type is a library type, not a compiler feature. When a literal is
passed straight to a refined constructor (`Html.new("<li>")`), the compiler runs
the type's validator at compile time through the interpreter. An invalid literal
fails the build with `REFINEMENT_LITERAL_INVALID`. Non-literal construction is
still checked at run time (`REFINEMENT_FAILED`). Validators are pure, so a
literal proven at compile time skips the run-time check in native builds.

`string.html-escape` replaces exactly `& < > " '` with `&amp; &lt; &gt; &quot;
&#39;`, matching Rails `ERB::Util.html_escape` byte for byte. `string.join`
concatenates a `List<String>`, sizing the result once and copying each part
once. An `Html` type is library code:

```flow
type Html : String {
    validate html.safe?
}

fn html.escape(text: String) -> Html {
    effects none
    Html.new(string.html-escape(text))
}
```

See [report 262](../reports/262-string-join-html-escape.md).

`string.replace(text, old, new)` returns `text` with every occurrence of `old`
replaced by `new`. Matching is ordinal over UTF-8 bytes, scanning left to right
and never overlapping: `string.replace("aaaa", "aa", "b")` is `"bb"`. When no
occurrence matches, the result equals `text`. When `old` is empty, the result is
`text` unchanged, so the call never inserts `new` between characters. It has no
effects.

```flow
fn shout(text: String) -> String {
    effects none
    string.replace(text, " ", "_")
}
```

## Html and templates

`Html` is a built-in type: safe markup, a `String` underneath. Programs do not
declare it (`FLOW_HTML_BUILT_IN`). It is added to a program automatically the
first time the program uses `Html`, `html.raw` or a template.

- **`Html.new(text)` escapes.** Any String becomes safe text: `& < > " '` are
  escaped exactly like Rails `ERB::Util.html_escape`. It cannot fail.
- **`html.raw(text)` keeps markup.** The markup must be a well-formed fragment
  (`string.html-fragment?`):
  - a literal is checked at compile time (`REFINEMENT_LITERAL_INVALID`);
  - any other value is checked when it is called (`REFINEMENT_FAILED`).

  It is the only way run-time markup becomes Html, so it is easy to find in
  review. Programs cannot redefine it.
- **`Html.value(html)`** gives the markup back as a String, at no cost.

```flow
fn page.item(name: String) -> Html {
    effects none
    html"<li>{name}</li>"
}

fn page.card(title: String, items: List<Html>) -> Html {
    effects none
    html"""
        <article class="card" data-title="{title}">
          <h2>{title}</h2>
          <ul>{items}</ul>
        </article>
        """
}
```

### Templates

- **Forms.**
  - `html"…"` is one line and uses JSON escapes, as string literals do.
  - `html"""…"""` is raw: quotes need no escaping, and it may span lines.
- **Multi-line text.** It follows the C# raw-string rule. When the opening `"""`
  ends its line:
  - that newline is dropped;
  - the closing `"""` must sit on its own line;
  - the closing line's indentation is removed from every line;
  - CRLF reads as LF.
- **Holes.** `{expr}` is a hole. `{{` and `}}` are literal braces.
- **Hole types.**
  - A `String` hole is escaped, like `Html.new`.
  - An `Html` hole is inserted as-is.
  - A `List<Html>` hole inserts its items in order.
  - Any other type is a compile error (`FLOW_TEMPLATE_HOLE_TYPE`); convert it
    first, for example with `int.to-string`.
- **Compile-time check.** The whole markup skeleton is checked at compile time
  (`FLOW_TEMPLATE_MARKUP`), with each hole treated as an opaque, balanced unit:
  - elements must be balanced and close in order (void elements and `/>`
    excepted);
  - a bare `<` is an error;
  - a hole may sit in element content;
  - a String hole may also sit inside a quoted attribute value or in
    `<script>`/`<style>` text (escaped, as ERB does, so it cannot end the
    element);
  - a hole may not sit in a tag or attribute name, an unquoted value, between
    attributes or in a comment, and an Html hole may not sit in raw text.
- **Html by construction.** The IR verifier re-checks every template, so the
  result is `Html` by construction, and no backend runs the validator on it.
  `Html.new` compiles to the same operation as `html"{text}"`. Native code
  measures the result once, reserves it once, and writes each byte once.
  Literal text is copied straight from read-only data.
- **`string.html-fragment?`** is the same checker at run time. It also accepts
  `<!-- … -->`, `<!doctype …>` at the top level, and raw `<script>`/`<style>`
  text, and it limits nesting to 256 elements.

See [report 266](../reports/266-html-templates.md).

## compact { }

`compact { ... }` is an expression block that must produce exactly one value
(use a record to carry several). The interpreter simply evaluates the block. A
native owning-AOT build moves the result to the block's arena mark and reclaims
the block's temporaries. The compiler must prove that nothing else (outer
values, surviving locals, effectful operations) can still refer to them;
otherwise it rejects the block with `IR_OWNING_COMPACT_UNPROVEN`. Ordinary
scopes, returns and list operations never relocate. See
[report 261](../reports/261-compact-blocks.md).

```flow
fn label(value: Int) -> String {
    effects none
    compact {
        let text = int.to-string(value)
        string.concat(string.concat("#", text), "!")
    }
}
```

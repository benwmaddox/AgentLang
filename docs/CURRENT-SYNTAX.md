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

An attached test is a list of steps. `let` binds intermediate values and
`expect <Bool expression>` checks one; a test may hold several, in any order:

```flow
test word.name/case {
    let total = word.name(1, 2)
    expect total == 3
    expect word.ok?(total)
}
```

- `expect a == b` reports both values when it fails (`TEST_ASSERTION_FAILED`);
  any other false `expect` fails with the same code.
- A final bare Bool expression is an implicit `expect`, so
  `test x/y { string.contains(a, b) }` is a complete test.
- `expect effects { fs.read: 1 }`, as the last step, asserts exact
  target-scoped effect counts.
- `expect` is only a top-level statement of a Flow/2 test body; functions and
  nested blocks reject it (`FLOW_EXPECT_OUTSIDE_TEST`). A step-list test must
  leave nothing on the stack.

The older `=>` expectation forms remain readable during migration (legacy).
A legacy test compares a constructed value with a value-expression expectation:

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

## inline fn

`inline` before `fn` marks a small helper whose body the native backend may
emit at each call site instead of a native call:

```flow
inline fn campfire.ua.byte(text: String, index: Int) -> Int {
    string.byte-at-or(text, index, -1)
}
```

It is a hint. The interpreter ignores it; the optimized native build (O2)
inlines the word when its emitted body is small, and otherwise calls it as
usual (O0 builds never inline, to keep native frames small). Calls are never
recursive in Flow, so any word may carry it. `inline` is a modifier only
directly before a top-level `fn` (not on `override fn`), so names such as
`inline` elsewhere are unaffected, and the formatter keeps it.

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

## Operators

Flow/2 has C-like binary infix operators (bitwise precedence as in Rust). An operator glyph is an operator
only when it has whitespace (space, tab or newline) on both sides:

```flow
fn order.total(count: Int, price: Int, fee: Int) -> Int {
    count * price + fee
}
```

Without the spaces nothing changes: `a-b` and `room-id` are names, `-1` is a
negative literal (so `f(-1)` and `x - -1` both work), and `List<String>` or
`option.none<Bytes>()` are generic brackets. Parentheses group:
`(a + b) * c`.

| Level (high to low) | Operators | Operands | Lowers to |
| --- | --- | --- | --- |
| 1 | `*` `/` `%` (left-assoc) | Int, Int | `multiply`, `divide`, `int.remainder` |
| | `*` `/` | Float, Float | `float.multiply`, `float.divide` |
| 2 | `+` `-` (left-assoc) | Int, Int | `add`, `subtract` |
| | | Float, Float | `float.add`, `float.subtract` |
| 3 | `<<` `>>` (left-assoc) | Int, Int | `int.shift-left`, `int.shift-right` |
| 4 | `&` | Int, Int | `int.bit-and` |
| 5 | `^` | Int, Int | `int.bit-xor` |
| 6 | `\|` | Int, Int | `int.bit-or` |
| 7 | `<` `<=` `>` `>=` (do not chain) | Int, Int or Float, Float | `int.less-than` ... / `float.less-than` ... |
| | | String, String | `string.less-than` ... (UTF-8 byte order) |
| 8 | `==` `!=` (do not chain) | two values of one type | `equals`; `!=` is `equals` negated |
| 9 | `&&` | Bool, Bool | `if a { b } else { false }` |
| 10 | `\|\|` | Bool, Bool | `if a { true } else { b }` |

- There are no implicit conversions. Int with Float, `%` on Float, and nominal
  scalars such as `MessageId : Int` are `FLOW_OPERATOR_TYPE` errors; unwrap
  or convert explicitly (`int.to-float`, `id.value()`). Join strings with
  `string.concat`, not `+`.
- `&&` and `||` short-circuit: the right side runs only when it decides the
  result, so `b != 0 && a / b > 2` never divides by zero.
- Int `+ - *` keep the primitives' overflow checks (`RUNTIME_OVERFLOW`); `/`
  and `%` by zero fail with `RUNTIME_DIVIDE_BY_ZERO`. `/` truncates toward
  zero and `%` takes the dividend's sign, as in C: `-7 % 3` is `-1`,
  `7 % -3` is `1`, and `Int64.MinValue % -1` is `0`.
- **Bitwise operators** work on Int's 64 bits (two's complement). They bind
  tighter than comparisons, as in Rust and Go rather than C. A bitwise operand of
  a comparison or equality must still be parenthesized, so `x & 1 == 1` is
  `FLOW_BITWISE_PARENS`; write `(x & 1) == 1`. `int.bit-not(x)` flips every bit.
- **Shifts** take a count from 0 to 63; any other count fails with
  `RUNTIME_SHIFT_RANGE`. `>>` keeps the sign (arithmetic shift). Unlike `+ - *`,
  `<<` has no overflow check: bits shifted out are dropped.
- **String comparison** is by UTF-8 bytes, which is Unicode code-point order and
  Ruby's `String#<=>`, not locale collation. `string.compare(a, b)` gives -1, 0
  or 1.
- `a < b < c` is `FLOW_COMPARISON_CHAIN` and `a == b != c` is
  `FLOW_EQUALITY_CHAIN`; parenthesize or join with `&&`.
- `expect a == b` still reports both values; `expect a != b` is an ordinary
  Bool check.
- The formatter writes one space around each operator and only the
  parentheses that precedence needs.

## Loops and var

Flow/2 has mutable locals and two loop forms. There is no `while`.

```flow
fn sum-evens(values: List<Int>, limit: Int) -> Int {
    effects none
    var total = 0
    for value, index in values {
        if index >= limit {
            break
        }
        if value % 2 != 0 {
            continue
        }
        total = total + value
    }
    total
}
```

- `var name = value` declares a mutable local anywhere in a body. `name = value`
  reassigns it. Only a `var` can be reassigned; assigning a `let`, a parameter,
  a loop name or an unknown name fails (`FLOW_ASSIGN_IMMUTABLE`,
  `FLOW_ASSIGN_UNKNOWN_LOCAL`). The new value must have the exact declared type
  (`FLOW_ASSIGN_TYPE`). Like `let`, a `var` cannot shadow another local.
- `for i in start..end { }` counts Int `i` from `start` up to `end - 1`. Both
  bounds are Int expressions evaluated once, before the loop. The loop is empty
  when `end <= start`. `..` needs no spaces, but `n-1` is one name, so write
  `0..n - 1`.
- `for item in items { }` visits a `List<T>` in order. `for item, i in items { }`
  also binds the Int index `i`.
- Loop names are immutable and scoped to the body. A loop is a statement and
  produces no value. A value a body statement computes is discarded. Use a
  `var` declared before the loop to carry results out. `return` is rejected in
  a loop body (`FLOW_RETURN_IN_LOOP`).
- `break` leaves the innermost loop. `continue` starts its next iteration. Both
  must be the last statement of their block (`FLOW_LOOP_CONTROL_NOT_LAST`).
  Outside a loop body, or inside a `compact { }` within one, they fail with
  `FLOW_LOOP_CONTROL_OUTSIDE_LOOP`.
- An `if` whose branches both produce nothing is a statement. Its `else` may be
  omitted: `if done { break }`. Loops also work inside test bodies.
- **Budget.** Loops may be unbounded in source, but each invocation of an entry
  point may run at most 10,000,000 loop iterations in total, counted across all
  loops and called words (`IrLoops.DefaultIterationBudget` in `TypedIR.fs`). One
  more iteration fails with `RUNTIME_LOOP_BUDGET_EXCEEDED`, in the interpreter
  and in native code alike.
- **Memory (owning AOT).** A loop is a real native back edge. Everything an
  iteration allocates sits above the loop's arena mark. At the end of each
  iteration (`continue` included), the `var`s the body assigned are moved down
  to the mark, and the rest of the iteration is released. A loop that only
  updates an Int `var` therefore uses constant arena. A loop that grows a
  String `var` peaks near three times the final value, not at the sum of all
  intermediates. The compiler must prove that only those `var`s refer to an
  iteration's allocations, as it does for `compact { }`. When the body passes an
  allocation to an effect that may keep it, iterations are not reclaimed. The
  loop still runs correctly, but its memory grows.
- **Accumulating a list.** When a loop's only use of a List `var` is
  `items = list.append(items, x)`, each append costs one copy of `x`, not of
  the list, so building n elements is linear in time and memory. Reading
  `items` any other way in the loop body (or in a nested loop) falls back to
  copying the list on each append.
- Loops and `var` are supported by the interpreter and the owning-stack
  backend. The older arena LLVM backend rejects them
  (`IR_LLVM_LOOP_UNSUPPORTED`).

## else if

In Flow/2, `else if` chains without nesting:

```flow
if build == "412" { "2.0" } else if build == "419" { "2.0.4" } else { "" }
```

The nested `if` becomes the else block's only statement; the formatter prints
it back as `else if`.

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


`string.byte-at(text, index)` returns `Option<Int>`. For scanning loops use
`string.byte-at-or(text, index, fallback)`, which returns the byte (0-255) or
`fallback` outside `0..byte-length`. Natively it is an inline bounds check and
one load, with no Option and no runtime call.

## Stdlib value types: Bytes and Json

`Bytes` and `Json` are built-in type names (reserved, like `String`). Neither
has a literal: Bytes come from `bytes.from-utf8`, `base64.decode` or crypto
primitives, and a Json value only from `json.parse` and its accessors
(`json.field`, `json.index`, ...). A Json value is the exact text of one
validated JSON value. A parameter named like a namespace (`json`, `cookie`)
turns `json.field(json, ...)` into a bound-receiver call, so name it
`document`. The primitives are listed in [the stdlib plan](STDLIB-PLAN.md),
section 3.

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
  - native code skips the check when the text is provably Html already:
    `Html.value` of an Html value (also through `let`), a literal fragment, or
    `string.concat` of those.

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
  - A `String` hole is escaped, like `Html.new`. So is a refined String (a
    nominal `type Name : String`, or `Url`).
  - An `Int` hole is written in decimal, with no `int.to-string`.
  - An `Html` hole is inserted as-is.
  - A `List<Html>` hole inserts its items in order.
  - Any other type is a compile error (`FLOW_TEMPLATE_HOLE_TYPE`); convert it
    first.
- **Holes by context** (`FLOW_TEMPLATE_HOLE_CONTEXT`; report 302). HTML
  escaping protects text and quoted attribute values, but not a URL's scheme,
  script or CSS, so:
  - **URL attributes** (`href`, `src`, `action`, `formaction` and the rest): a
    hole before any `/`, `?`, `#` or `:` of the value must be a `Url` or an
    `Int`. `href="/rooms/{id}"` takes any String; `href="{target}"` needs
    `Url.new(target)`.
  - **`on*` handlers, `style` and `srcdoc`, and `<style>` text** take only Int
    holes.
  - **`<script>` text** takes no holes, unless the script's `type` is an HTML
    template (`text/template`, `text/html`, `text/x-…`). Put values in a
    `data-` attribute instead.
- **`Url` is built in**, like `Html`: `type Url : String { validate
  string.url-safe? }`, added to the first document that uses it. A safe URL
  has no control characters and no leading or trailing space. If it has a
  scheme before any `/`, `?` or `#`, the scheme is `http`, `https`, `mailto` or
  `tel`. Relative URLs are safe. `Url.new` with a literal is checked at compile
  time; any other value is checked at run time (`REFINEMENT_FAILED`). Test it
  first with `string.url-safe?` to fall back instead of failing.
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

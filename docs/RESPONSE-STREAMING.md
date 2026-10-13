# Writing responses straight to the socket

Status: design proposal for review (2026-10-13). Nothing here is implemented.

## Problem

Today a view builds its page as one `String`.

- Every join or concat level copies the bytes again. Report 262's 382 KB
  prototype copies the page about 6×, and about 3× once list building is
  native.
- The host then copies the finished string out of the arena, and today it also
  transcodes it from UTF-16 to UTF-8.

The request needs one thing: the right bytes on the socket.

## Idea: `Html` is a list of parts, not one string

A template does not concatenate. It produces a **part list**, an ordered list
of byte ranges. Each part is one of:

| Part kind | Where the bytes live | Copies |
| --- | --- | --- |
| literal | read-only data in the compiled binary, emitted once at compile time | 0 |
| fresh | this request's arena, for example an escaped value written once by `html.escape` | 1 (the escape itself) |
| nested | another part list, so a partial included in a layout is spliced in, not copied | 0 |
| cached | an immutable byte range from a cache (see below) | 0 |

At response time the host knows the exact total length, because the sum of the
part lengths is the `Content-Length`. It issues one gather write
(`WSASend` with a `WSABUF` array on Windows, `writev` on Linux) straight from
those ranges. The page string never exists.

Copy volume drops to roughly 1× of the dynamic content. Static markup, usually
most of a page, is never copied at all.

### Why it fits the language

- **Readable.** Authors write the same templates (both styles):

  ```flow
  fn message.row(m: Message) -> Html {
      effects none
      html"<li class=\"row\" id=\"m-{m.id}\">{m.body}</li>"
  }

  fn room.page(ms: List<Message>) -> Html {
      effects none
      html {
          <ul class="messages">
          {for m in ms { message.row(m) }}
          </ul>
      }
  }
  ```

  The representation is invisible. `Html` stays the type, and joining `Html` is
  O(number of parts), not O(bytes).
- **Safe by construction.**
  - Literal parts are validated at compile time.
  - Interpolated `String`s are always escaped.
  - Interpolated `Html` is already safe.
  
  A template result therefore needs no run-time validator. `Html.new(runtimeString)`
  is still validated at run time, every time.
- **Immutable strings make it sound.** Parts are borrowed ranges, and nothing
  can change them before the write.
- **UTF-8 makes it direct.** With UTF-8 internal strings (planned), arena bytes
  are wire bytes, so no transcoding pass is needed.
- **Arena lifetime matches.** The request's arena is released after the write
  completes. The request stays on its worker, so no new locks are needed.

### Materializing

`Html.value(h)` or `html.text(h)` produce a `String` with one sized copy. Use
them only when a real string is needed, such as a test assertion, a hash, or a
cache key.

## How this changes the language

Today `Html` is a refined `String`, and a `String` is one contiguous run of
bytes. A part list cannot be a `String`, so the language gains one type.

### A new built-in type: `Body`

`Body` is an immutable sequence of text fragments (the name is open).

| Operation | Meaning | Cost |
| --- | --- | --- |
| `body.of(text: String) -> Body` | one fragment that references the String | no copy |
| `body.join(parts: List<Body>) -> Body` | concatenation | O(number of parts): copies part references, never bytes |
| `[a, b, c]` passed to a join | fused, as with strings today | no list built |
| `body.text(b: Body) -> String` | materialize | one sized copy |
| `body.byte-length(b) -> Int` | total length | O(parts), or cached |
| `b1 == b2` | content equality | streaming compare, no copy |

Under the hood a `Body` value holds a small table of fragment references
(offset and length), not the bytes. A fragment can be:

- a literal in the binary;
- a String in the request arena;
- later, a cache entry.

### `Html` refines `Body` instead of `String`

```flow
type Html : Body {
    constructor literal-only        // runtime markup only via html.raw (inside html.*)
}

fn html.raw(markup: String) -> Html { ... }  // validated policy, then wrapped
fn html.escape(text: String) -> Html { ... } // one escape copy, then a fragment
fn html.text(h: Html) -> String { body.text(Html.value(h)) }  // explicit materialize
```

Templates produce `Html` directly:

- each literal part is a static fragment;
- each `{text}` is an escape fragment;
- each `{html}` is spliced in by reference.

A template result needs no run-time validator: literals are proven at compile
time, escapes are safe by construction, and `Html` parts are already `Html`.
Run-time markup still enters only through `html.raw`, which validates its
`String` argument every time. The validator never has to run over a part
list.

### Responses

An entry returns `Html` or `Body`, for example
`Response { status: Int, headers: ..., body: Body }`.

- The host sums the fragment lengths for `Content-Length`.
- It sends the fragments with one gather write (`WSASend` / `writev`).
- The request arena is released after the write completes.

The page string never exists.

### What authors notice

- **Templates read the same.** Only the cost model changes: building markup
  is cheap, and turning it into a `String` is the one visible copy.
- **String operations are not direct.** `string.contains(h, ...)` on an `Html`
  is a type error, so you call `html.text(h)` first. This keeps accidental
  materialization visible in code review.
- **Equality and tests work on content**, so interpreter and native results
  compare as before. The interpreter may represent `Body` as a plain String;
  the observable behaviour is identical.

### What the compiler and runtime must do

- **Relocation.** `compact { }` on a `Body` must move the fragment table and
  the fragments it references inside the block's region, and rewrite their
  offsets. Static and older fragments stay put. The arena-lifetime proof
  already identifies which region a fragment belongs to.
- **A new value layout.** The fragment table's layout, a third address space
  for static fragments (shared literals, deferred until further testing), and
  bounds checks on each fragment.
- **Host ABI.** It returns a fragment table instead of one String, and the host
  writes it.

### Copy count for the 40 × 9.6 KB page

| Step | Today | With `Body` |
| --- | --- | --- |
| escape each name | 1× | 1× |
| row template | 1× (fused join) | references only |
| join rows | 1× | references only |
| `<ul>` wrapper | 1× | references only |
| UTF-16 to UTF-8 output | 1× (host) | 0 with UTF-8 strings |
| **Total** | **4× plus transcoding** | **about 1×** (only the escape) |

## Cached parts

The same mechanism serves cached and fresh results together. A cache entry is
an immutable UTF-8 byte range. A part list can reference it like any other
part, so one response can mix:

- a cached sidebar,
- fresh message rows,
- static layout literals.

All of them go out in one gather write with no copying.

The cost is that a cache shared across workers needs a safe way to keep an entry
alive while a response is still writing it. The options are:

1. **Per-worker caches.** No cross-thread dependency. Memory grows with the
   worker count, and the hit rate is lower.
2. **Shared read-mostly cache with epoch reclamation.** Readers pin an epoch per
   request. Retired entries are freed when no worker holds an older epoch.
   Readers take no lock, but it is a cross-thread dependency.
3. **Shared cache with reference counts.** Atomic increment and decrement per use
   is simple, but it adds contention on hot entries.

Under the project rule, option 2 or 3 is a new cross-thread dependency and must
be discussed before adoption. Option 1 can come first, and it is enough for the
later "with cache" comparison against Rails' and Rust's fragment caches.

## Costs and open questions

- **Gather-write limits.** `IOV_MAX` is 1024 on Linux, and `WSASend` limits
  apply too. Long part lists go out in batches. Adjacent tiny parts can be
  coalesced into a small per-request buffer when that is cheaper than another
  iovec entry.
- **Compression.** gzip or brotli consume the parts as a stream, so there is no
  zero-copy then. The benchmark serves uncompressed today.
- **Partial writes and backpressure.** The worker keeps the part cursor and
  arena until the send completes.
- **Testing.** Parity tests materialize the part list and compare bytes with the
  interpreter, which can either represent `Html` as parts or materialize
  eagerly. The semantics are identical.

## Proposed order

1. Native list building and templates that lower to a single sized join.
   This needs no host change.
2. UTF-8 internal strings.
3. A part-list `Html` representation in the owning AOT backend with host
   gather writes, plus a per-request coalescing buffer.
4. Per-worker caches for the "with cache" comparison. Shared-cache options go to
   discussion.

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

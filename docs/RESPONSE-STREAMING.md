# Writing responses straight to the socket

Status: design decided 2026-10-13 (stream into a response buffer); not yet implemented.

## Problem

Today a view builds its page as one `String`.

- Every join or concat level copies the bytes again. Report 262's 382 KB
  prototype copies the page about 6×, and about 3× once list building is
  native.
- The host then copies the finished string out of the arena, and today it also
  transcodes it from UTF-16 to UTF-8.

The request needs one thing: the right bytes on the socket.

## Decision (user, 2026-10-13): stream into a response buffer

A part-list `Body` type was considered and rejected. Values holding references
into other arena regions would pin those regions or force `compact` to deep-copy
them, which weakens bump freeing. Literals in the binary do not have that
problem (they are immortal and never move), but shared literals are deferred
until after further testing.

Instead, rendering **streams in document order**, as Forth's `EMIT`/`TYPE`,
PHP's `echo` or ERB's output buffer do:

```flow
fn room.page(room: Room, messages: List<Message>) -> Unit {
    effects out.write
    out.write(layout.head(room))      // pure template -> Html, written once
    messages.each(message.render)     // each row writes; its temporaries can then be freed
    out.write(html"</ul></body></html>")
}
```

- **`out.write` is a typed effect** confined to the handler layer.
  - Templates stay pure (they return `Html`) and testable.
  - The compiler fuses `out.write(template(...))` and flattens nested joins
    (destination-passing), so template parts are written straight into the
    response instead of being assembled first.
- **Response buffer, then flush (the default).**
  - Writes go to an append-only output region outside the arena that the
    program never reads back.
  - The host sends it when the handler finishes, so the status and
    `Content-Length` stay correct even if rendering fails partway.
  - Chunked or early flushing is a later per-route option.
- **Bump freeing gets stronger.** No value references another region. Once a
  row is written, its scope's temporaries are dead and can be rewound, so
  memory stays at about one row regardless of page size.
- **Copies.** About 1× of the dynamic content: an escape writes once, and
  literals are copied once into the buffer. With UTF-8 strings the buffer holds
  wire bytes, so there is no transcoding.
- **Reuse.** A fragment that is stored, cached or turned into a `String` is
  built as a normal `Html` value, which costs one copy.
- **Interpreter and tests.** `out.write` appends to a captured string, so parity
  compares the same bytes.

## Cached fragments

A cached fragment is an immutable UTF-8 byte range. `out.write(cached)` copies
it into the response buffer once. Later, a gather write could send buffer
segments and cache entries side by side, without that copy, if measurements
justify it.

A cache shared across workers needs a safe way to keep an entry alive while a
response still uses it. The options are:

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

- **Errors after a partial render.** With the default buffer, nothing has been
  sent yet, so the handler's error response replaces the buffer. Chunked
  flushing, when added, must define what happens after the first flush.
- **Buffer capacity.** The output region has its own capacity. Overflowing it
  is a diagnostic, like `OWNING_STACK_CAPACITY`.
- **Compression.** gzip or brotli would read the buffer as a stream. The
  benchmark serves uncompressed today.
- **Testing.** The interpreter captures `out.write` into a string, so parity
  compares bytes.

## Order

1. Free list retag and in-place `list.append` / `list.concat`.
2. UTF-8 internal strings.
3. Templates: the literal-only `Html` constructor, `html"…"` with multi-line
   strings, and compile-time syntax validation.
4. `out.write` into the response buffer, with join flattening and
   destination-passing for templates.
5. The apples-to-apples Campfire page.
6. Later: per-worker caches for the "with cache" comparison. Shared-cache
   options go to discussion.

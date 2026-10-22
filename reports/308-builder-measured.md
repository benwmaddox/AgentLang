# 308 — Item 6, the rest: a builder, measured first

For the next round of speed work after item 1b, you chose a builder or rope
type. That type is the general fix for the 80 `FLOW_APPEND_IN_LOOP` sites of
report 304, where a loop keeps re-copying a growing `String` or `List`. Before
designing it, I measured where the copying actually goes on the uncached room
page.

## Where loop copying costs

Every `for` loop's back edge calls `al_owning_loop_relocate`. It copies each
loop-carried value that was given a new arena value during the iteration down
to the loop's mark, then releases the iteration. On a fresh profile of the
item-7a build, it costs 4.4% of CPU (flat), from two callers:

| Caller | Share of CPU |
| --- | ---: |
| `rich-html.tag` (the tag reader, once per character) | 2.2% |
| `campfire.server.whole-room-page` (`rendered = list.append(rendered, <message Html>)`) | 0.55% |

`perf annotate` splits the cost two ways:
- about a quarter is in the copy loop;
- the rest is the call itself and its scan of the descriptors.

## Step 1: skip a relocation that would move nothing

Trusted builds already skipped the release when a loop carries only scalars and
the iteration allocated nothing. Now they also skip it when the loop carries
non-scalar values, as long as the cursor is still at the mark. The reasoning:
- **Nothing allocated means nothing to move.** An iteration that allocated
  nothing cannot have given any carried value a new arena value, so the
  relocation would be a no-op.
- **One inline compare** replaces the call, the descriptor stores and the
  reloads.
- **Diagnostic builds unchanged:** they still make every call.

**Speed** (ABBA against item 7a):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,208 | 2,251 | 2,265 | 2,215 |
| Uncached CPU ms per request | 1.891 | 1.856 | 1.851 | 1.891 |
| Cached req/s | 7,161 | 7,154 | 7,184 | 7,116 |
| Cached CPU ms per request | 0.565 | 0.565 | 0.566 | 0.574 |

- **Uncached:** 2% more requests a second, and 2% less CPU per request.
- **Cached:** unchanged.
- **Profile:** `loop_relocate` falls from 4.36% to 3.96%.
- **Checks:** all 45 sampled responses are byte-identical. All 24 suites pass.

## What a builder could still buy

- **Hot sites.** All 72 self-append sites in Campfire and the stdlib are in
  words the native server contains, but only the two above show up in the
  profile. The rest run too rarely, or over too few items, to matter.
- **`rich-html.tag`'s 2.1% is not an accumulator.** Its iterations do allocate
  something on each character, but the carried values hardly grow. A builder
  would not touch it. The cost is whatever each iteration allocates, and
  finding that is the next lead (see below).
- **`whole-room-page`'s 0.57% is the one accumulator:** 40 rendered messages
  appended to a `List<Html>`. A builder for Html there could remove most of
  it, but it needs:
  - an Html-producing variant (Html pieces kept as they are, String pieces
    escaped);
  - its own storage that the back edge never relocates;
  - rejection at every host boundary.

So **a builder buys at most about 1% on this page**, and it is a new type with
its own storage. Per your rule of measuring at each step, I stopped here
rather than build it.

## For you to decide

- **Build the builder anyway?** It is still worth it for clarity: 72 sites
  read better with a builder than with self-appends, and a page with longer
  loops would gain more. But on today's room page it is a small speed win for
  a medium amount of work.
- **The better next speed lead:** find what `rich-html.tag` allocates on every
  character. If it is a temporary that could live in a register or a stack
  slot, most of the remaining 2.1% goes away, along with the same cost in
  every loop like it.

## A finding along the way

`rich-html.space?` is a one-line predicate called on most characters of every
tag. It is not inlined: each call costs a full call, including releasing its
frame and updating the live-bytes count. Marking it `inline fn` (report 280) is
a one-word change. Measure it together with the search for what each iteration
allocates.

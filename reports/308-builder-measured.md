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

## Step 2: the mark is per iteration

After step 1, `rich-html.tag` still spent 2.1% in relocation. It turned out to
be re-copying, not some allocation on every character:
- **The loop's mark is taken once, before the first iteration.** Once a carried
  value had been reassigned, relocation left it at the mark. After that, the
  cursor stayed above the mark even in iterations that allocated nothing.
- **So step 1's skip almost never fired.** Every back edge copied `attributes`,
  `value-prefix` and `attr-value` up and back down again.

**Fix:** each iteration records where it starts allocating, and the back edge
skips relocation when the cursor is still there. An iteration that allocated
nothing moved no carried value. An older value stays where it is until an
iteration that does allocate compacts it, so memory stays bounded.

**Speed** (ABBA against the item-8 build):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,292 | 2,373 | 2,402 | 2,286 |
| Uncached CPU ms per request | 1.883 | 1.792 | 1.793 | 1.874 |
| Cached req/s | 7,687 | 7,799 | 7,751 | 7,829 |
| Cached CPU ms per request | 0.536 | 0.531 | 0.529 | 0.536 |

- **Uncached: 4% more requests a second, and 5% less CPU per request.**
- **Cached:** unchanged.
- **Profile:** `al_owning_loop_relocate` falls from 3.96% to 1.50% of CPU.
- **Checks:** all 45 sampled responses are byte-identical, and all 25 suites
  pass.

## What a builder could still buy

- **Hot sites.** All 72 self-append sites in Campfire and the stdlib are in
  words the native server contains, but only `whole-room-page` shows a real
  accumulator in the profile. The rest run too rarely, or over too few items,
  to matter.
- **`whole-room-page`'s 0.57% is that accumulator:** 40 rendered messages
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

## A finding along the way

`rich-html.space?` is a one-line predicate called on most characters of every
tag. It is not inlined: each call costs a full call, including releasing its
frame and updating the live-bytes count. Marking it `inline fn` (report 280) is
a one-word change, and the next small lead.

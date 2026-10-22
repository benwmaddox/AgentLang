# 309 — Item 8: paired layouts

Campfire streams its room page as a head, the messages, and a tail. Until now
it produced the head and tail at runtime:
1. render the whole page with no messages;
2. search the text for the messages container's `data-messages-target` and
   the `">` after it;
3. skip the whitespace;
4. slice.

The page template was checked, but nothing checked that this split matched
it. Item 8 makes the split part of the template.

## (a) The language

```flow
fn campfire.views.page-layout(chrome: PageChrome) -> Layout {
    html"""
    <main>
      <div class="messages" data-messages-target="messages">
    {...}</div>
    </main>
    """
}

fn campfire.views.page(chrome: PageChrome, messages: List<Html>) -> Html {
    layout.wrap(campfire.views.page-layout(chrome), messages)
}
```

**Templates.**
- An `html"…"` template with one `{...}` slot is a **`Layout`**.
- **Checked as one fragment.** The whole template, with the slot treated as an
  Html hole, must be one well-formed fragment, which is the existing template
  check. So the tail closes exactly what the head opens.
- **Where a slot may sit.** The slot is element content. These are rejected:
  - two slots (`FLOW_LAYOUT_SLOT`);
  - a slot in an attribute (`FLOW_TEMPLATE_MARKUP`);
  - a slot inside `<title>`, `<textarea>`, `<script>` or `<style>`
    (`FLOW_LAYOUT_SLOT`);
  - an unbalanced shell such as `<div>{...}` (`FLOW_TEMPLATE_MARKUP`).
- **Holes.** Other holes work as in any template, on either side of the slot,
  and each hole's value stays on its own side.

**The `Layout` type** is built in, as `Url` is. It is added when a parsed
template has a slot:
- **Fields:** `record Layout { head: String, tail: String }`. `page.head` and
  `page.tail` read the two texts, for streaming with `response.write`.
- **No way to build one by hand:**
  - it has no constructor, so `layout.new` does not exist;
  - declaring your own `Layout` is `FLOW_LAYOUT_BUILT_IN`;
  - a `Layout` handed in from outside, as an entry-point input or host value, is
    refused by both the interpreter and native builds.

**`layout.wrap(layout, content)`** puts Html, or a `List<Html>`, in the slot and
returns Html. It runs no validation, because a `Layout` can only come from a
checked template, so head, content and tail are Html by construction.

**Natively:**
- **A layout template writes its head at the arena cursor, then its tail right
  after it.** A record of two Strings is just its fields' bytes in order, so
  those two writes already are the `Layout` value; nothing is copied again.
- **`layout.wrap`** finds the head and tail inside the record, then makes one
  template write of head, content and tail. A list is written in place, with no
  join first.

**A commit fix found on the way.** A word returning `Layout` uses `Html` only
inside its template. The commit's type closure looked only at signatures and
generated-word calls, so the word was compiled without the `Html` type. The
closure now also takes the types named by a body's typed steps (templates,
layouts, `db.rows` and others).

**Checks:** the new suite `AgentLang.Layouts.Tests` has 105 assertions:
- each rejection above, plus a declared `Layout`, a hand-built one, and wrapping
  a String;
- formatting that keeps `{...}`;
- a multi-line template with the slot on its own line;
- a slot at the very start and at the very end, giving an empty head or tail;
- an Int hole before the slot and an escaped String after it;
- `wrap` with Html, and with lists of 0, 1 and 40 items;
- interpreter against native at O0 and trusted O2, with texts that need
  escaping, `é🚀` and 300 bytes;
- a forged `Layout` refused on both backends.

**Suites:** all 25 pass.

CAMPFIRE

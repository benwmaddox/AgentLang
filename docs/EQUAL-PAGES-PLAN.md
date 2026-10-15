# Equal pages (plan of record, 2026-10-15)

User direction (2026-10-15):
- For the same database, Flow's room page must be byte-identical to the
  Rails page with its CSRF elements removed. SQL call counts need not match.
- Rich text: **for now, match Rails' output with Flow-only code**, sanitizing
  at render time, which with the worker store means once per cache miss. **The
  long-term goal is inbound sanitization** (sanitize once on write, store the
  safe HTML); that is described below as the next step, not this milestone.
- An HTML5 parser in Flow, stripped down to what is known to be OK: the
  allowlisted tags and attributes. Anything else is dropped or refused, never
  recovered. Message content never carries JavaScript.
- No `compact { }` juggling in Flow code; requests are short-lived.

**The target is Rails, not Rust.** The two references disagree with each
other on build output: Rust ships its own asset digests and formats its
markup differently, giving 583 differing blocks against Rails. Flow is much
closer to Rails (76 blocks at 285 start; 32 after items 1-6, all message bodies), and Rails is the original app.
Rust's deviations are recorded in the report but not copied.

## Differences at the start (Flow against Rails without CSRF, snapshot room)

| # | Difference | Plan | State |
| --- | --- | --- | --- |
| 1 | Account logo `?v=` | `accounts.updated_at`, as Rails `to_fs(:number)` | done |
| 2 | Current user's avatar `src` | `/users/<signed id>/avatar?v=<user updated_at>` | done |
| 3 | `turbo-cable-stream-source` signed stream name | Rails MessageVerifier over the room GlobalID, key `turbo/signed_stream_verifier_key` | done |
| 4 | `loaded_at` | `room.updated_at` in epoch ms (`messages_helper.rb`) | done |
| 5 | Message-list whitespace | partial `"  <div…</div>\\n"`, container prefix `"\\n    \\n    \\n"` | done |
| 6 | Empty boost list whitespace, and the 2 boosts | `_boosts` / `_boost` partials; boosters' signed avatars; all-emoji class (Ruby 3.4.10 Unicode 15.0 ranges) | done |
| 7 | Message bodies | Flow HTML parser + Rails pipeline, at render time on store misses (inbound later) | after 6 |

## What message content may contain (the allowlist Rails applies, mirrored)

- **Tags:**
  - `a abbr acronym address b big blockquote br cite code dd del dfn div dl dt
    em h1-h6 hr i img ins kbd li mark ol p pre samp small span strong sub sup
    time tt ul var`;
  - plus the editor's `s u mark table thead tbody tfoot tr th td`;
  - plus Action Text attachments.
- **Attributes:** `abbr alt cite class datetime height href lang src title width
  xml:lang data-language`, plus attachment attributes. No `name`, no `on*`.
- **URIs** (`href`, `src`, and the other URI attributes): allowed protocols
  only. `javascript:` is removed; `data:` is allowed only for
  `image/gif|jpeg|png`, `text/plain` and `text/css`.
- **`style`:** only Lexxy's highlight `color` / `background-color` with plain
  values; every other declaration is dropped.

## Rich text now: render-time match in Flow (this milestone)

Rails stores the editor's HTML as submitted (Action Text only parses and
re-serializes it) and sanitizes on every render in three layers: Campfire's
`ContentFilters` (RemoveSoloUnfurledLinkText, SanitizeTags,
SanitizeAttributes), Action Text's render sanitizer
(`lib/rails_ext/action_text_allowed_tags.rb`), and `auto_link`, which
sanitizes again (`MessagesHelper#message_presentation`). Flow does the same
work in Flow code:

- **Parser in Flow.** A strict tokenizer and tree builder for the markup the
  editor produces: elements, attributes, text, character references, void
  elements, and the implied end tags Lexxy/Trix markup relies on. The tree is a
  flat, index-based node list, since Flow has no recursive types. Input
  outside the grammar is refused, never guessed at: the message is stored as
  unrenderable, as Rails renders `_unrenderable` when presentation raises.
- **Pipeline in Flow:** the content filters, the allowlists (tags, attributes,
  URI schemes), `auto_link`, and a serializer that writes Nokogiri's
  `html_standard_serialize` format. Behaviour is taken from the Rails
  reference and cross-checked against the Rust port
  (`crates/richtext`).
- **Now:** the parser and pipeline run on the render path inside the
  message partial, only on worker-store misses. Its output is part of the
  stored fragment.
- **Later (long-term, inbound):** the same pipeline runs on write and in a
  one-time migration instead, and rendering copies the stored HTML. Storage:
  a Flow-owned table
  `flow_rich_text_presentations(record_id, record_updated_at, html)`.
  `action_text_rich_texts.body` is left untouched, so Rails and Rust still
  read the same database. A row is current when `record_updated_at` equals the
  rich text's `updated_at`.
- **When the inbound version runs:**
  - on message writes;
  - once, as a migration for rows that already exist (run through the
    interpreter);
  - for any row found without a current presentation: rendered as
    unrenderable until migrated, never sanitized on the request path.
- **Rendering** copies the stored HTML into the message partial and
  parses nothing.

## Types: `RichHtml`, a stricter refinement of Html (user, 2026-10-15)

- **Invariant:** the text parses under the stripped-down grammar and holds only
  the allowlisted tags, attributes, URIs and style values above, in canonical
  serialized form (`sanitize(x) == x`).
- **Ways in:**
  - `rich-html.sanitize(String) -> RichHtml` is total and drops what isn't
    allowed (editor input now, inbound later).
  - `RichHtml.new(String) -> Option<RichHtml>` validates without changing
    anything, for values read from the database. A row that isn't already
    clean is none and renders as unrenderable.
  - Literals are checked at compile time.
- **Way out:** `rich-html.to-html(RichHtml) -> Html` is free and total, because
  every RichHtml is valid Html.
- **Templates** take user content only as an escaped String or as RichHtml
  converted to Html.
- **Placement:** RichHtml lives in the stdlib alongside Html once Html moves
  there (PENDING-REVIEW). The compiler runs the validators.
- **This milestone:** raw body, then `rich-html.sanitize`, then `to-html`, then
  the message partial, on store misses only.
- **Inbound later:** store canonical RichHtml text, and read it back through
  `RichHtml.new`.

## Acceptance

- `pagediff.py` (Flow against Rails without CSRF, same snapshot) reports 0
  differing blocks.
- Sanitizer tests:
  - Rails' allowlist cases;
  - hostile inputs (`script`, event attributes, `javascript:` URLs,
    malformed nesting), which must be removed or refused;
  - round trips of editor markup.
- Native and interpreter parity hold; all gates pass.
- The interleaved sweep is rerun, and the uncached column is reported as
  comparable.

## After 285: equal output across the rest of the app (user, 2026-10-15)

The standing goal: for the same database, every Flow response matches Rails'
bytes (CSRF elements aside). Each step uses the same method as 285: record
Rails' output (the room page, or `.agentlang/pages-285/oracle.rb`-style
runners for partials), diff, and port until 0 differing blocks.

1. **Rich-text phase B:** the 14 snapshot messages Rails transforms. These are
   mentions and other attachments (signed GlobalID lookups), Open Graph embeds,
   sounds, `figure` stripping and auto_link. The oracle's 169 messages all
   match when this is done.
   The 14 transformed oracle cases, in build order:
   - **B1 `figure` and sounds.** Strip the disallowed `figure` around Lexxy
     tables (keeping its children). `/play <name>` messages (content type
     `sound`) render the sound partial from the app's sound list.
   - **B2 HTML5 tokenizer in Flow.** A spec-conformant tokenizer (data, tag,
     attribute-name and attribute-value states, numeric and named character
     references). It is needed because stored markup can be malformed: one
     mention stores `content="<span class=" mention"=""`, which HTML5
     tokenization reads as an attribute named `mention"`. Tree building stays
     restricted to the allowlisted elements; anything needing WHATWG recovery
     beyond that is refused. Named references outside the table Flow carries
     are refused (the editor writes raw UTF-8).
   - **B3 attachments in rich text:**
     - mentions: read the GlobalID inside the SGID, as Campfire's
       possibly-expired path does; look up the user; render the mention
       partial; sanitize it, which strips `sgid`, `data-turbo-frame` and
       `aria-hidden`;
     - Open Graph embeds, from the `content` HTML or from the
       url/href/filename/caption attributes, including the Twitter avatar
       variant and exact partial whitespace;
     - the RemoveSoloUnfurledLinkText filter.
   - **B4 message attachments** (content type `attachment`): Active Storage
     blobs and their variants and previews.
2. **Routes beyond GET/HEAD `/rooms/<id>`,** one at a time:
   - the sidebar frame;
   - message paging (`/rooms/<id>/@<message>`, before and after);
   - search;
   - users and avatars;
   - account;
   - sign-in and sign-out (bcrypt needs a decision);
   - message and boost writes, with sanitization inbound when writes land.

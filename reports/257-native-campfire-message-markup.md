# 257 — Rails-shaped message markup for the room page

Status: interpreted/native parity passed; full Rails page and migration incomplete.

The room page now gives each message the identity that the shared
`once-campfire-verification` harness validates. Before this change, each message
was an `<article id="message_N">`. Now it renders as:

```html
<div id="message_N" class="message" data-message-id="N" data-client-message-id="…"><div id="presentation_message_N" dir="auto">…</div></div>
```

This follows Rails `messages_helper#message_tag` and the
`dom_id(message, :presentation)` div at pinned revision `90b3300`. Both trusted
renderers changed together: the interpreted `ResponseRenderer.fs` and the native
`campfire_http_renderer.c`. The native build script pins the new renderer hash.
Message order was already Rails-compatible. Rails `last_page` is
`ordered.last(40)`, the newest 40 returned oldest-first. The interpreted provider
reverses its descending query, and the native adapter fills descending indexes,
so neither needed a change.

Stored rich text stays HTML-escaped. Rendering it as HTML requires a sanitizer
equivalent to Rails/ActionText, which does not exist yet. This is a documented
divergence from the Rails page, not a parity claim. Author names, avatars,
timestamps, day separators, actions and the rest of the Rails layout are also
still absent, so the page is much lighter than Rails'.

## Validation

- Interpreted Kestrel HTTP integration: 467 assertions. The room test now
  requires 40 `data-message-id` openers and 40 presentation divs.
- Native room-read gate (TrustedGenerated, Off): 238 assertions at O0/O2,
  including standalone no-CLR execution.
- Native host rebuilt from that gate, with stage profiling on as for report 256.
  Each of O0 and O2 ran against a freshly rebuilt interpreted reference on
  127.0.0.1:13774:
  - the room GET bodies are byte-identical (7,396 bytes, SHA256
    `da054f04d3b5b96e6b38d51ee2e91575ecd2882b7b62f788d958fdf839b23fd3`; the
    previous page was 4,480 bytes);
  - 291 cookie/header assertions and all 20 corpus cases pass, plus 5
    transport/reuse cases at O2;
  - both hosts exited 0, with four cleaned native workers.
- Two first attempts failed before serving any request: the host helper requires
  stage-profiled builds. They are retained in the evidence.

Report-256 throughput numbers apply to the old 4,480-byte page and are not
comparable with this page.

## Next

Serve the shared harness's Rails-built seed with the native host and validate it
with the harness's own route contract (ID window from seed SQL, message
identities and content tokens). Then run the labelled same-machine comparison
against the Rails/Rust references on port 13774.

## Sealed evidence

[Archive](evidence/257-native-campfire-message-markup/257-native-campfire-message-markup-evidence.zip)
and [manifest](evidence/257-native-campfire-message-markup/manifest.json): 65
entries, 100,573 bytes, SHA256
`cdf7bc22db2bc0c7f8c85cad6624c6f0e7e870598efc3d96e389b7a4671f42b6`. They include
the validation and parity receipts, run and build receipts, room-read gate and
HTTP test receipts, the validation and sealing tools, and the changed sources.
Fixture databases, secrets, cookies and request configs are excluded, and text
was scanned for cookie values.

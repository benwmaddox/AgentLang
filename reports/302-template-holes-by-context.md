# 302 — Item 3: template holes checked by context

HTML escaping makes text safe inside element content and quoted attribute
values. It does not make a value safe as a URL's scheme, as script, or as CSS.
The browser decodes entities in `onclick="…"` before running it, and
`href="{x}"` with `x = "javascript:…"` is a link that runs code. Before this
change, a String hole was allowed in any quoted attribute value and in
`<script>`/`<style>` text, so Flow's claim that you "can't write XSS" was not
quite true. Each hole is now checked against where it sits.

## The rules

At compile time, each template hole is checked with the type it had in the
source (`FLOW_TEMPLATE_HOLE_CONTEXT`):

| Where | Allowed holes |
| --- | --- |
| Element content, other quoted attributes | String, Int, refined String, Url (escaped); Html, List<Html> in content |
| A URL attribute (`href`, `src`, `action`, `formaction`, `poster`, `cite`, `srcset`, …) until its scheme is fixed | Url or Int only |
| The same attribute once its scheme is fixed (`/rooms/{id}`, `?q={s}`, `mailto:{s}`) | as in content |

**When the scheme is fixed.**
- A literal `/`, `?` or `#` fixes it.
- So does a literal `:`, but only if no hole comes before it.
- After a Url or Int hole, a `:` could complete a scheme the hole began, so it
  is rejected: `Url.new("javascript")` is a valid relative URL.
- Character references count as the characters they stand for: `&#58;` is a
  `:`. The `#` inside `&#106;` is not a fragment.
| `on*` handlers, `style`, `srcdoc`; `<style>` text | Int only |
| `<script>` text | none, unless `type` is `text/template`, `text/html` or `text/x-…` (HTML data) |

**New in the language:**
- **Int holes.** An `Int` hole is written in decimal: `{room-id}` needs no
  `int.to-string`. This adds the IR part `IntHole`; natively it is template mode
  4, with digits written straight into the result and no temporary String.
- **Refined String holes.** A refined String, such as `type Name : String` or
  `Url`, can fill a String hole, escaped as usual. No `.value()` is needed.
- **`Url`.** A built-in type, like `Html`: `type Url : String { validate
  string.url-safe? }`, added automatically to the first document that uses it.
  `string.url-safe?` rejects:
  - control characters and DEL anywhere (browsers drop tabs and newlines inside
    a scheme);
  - a leading or trailing space;
  - a scheme other than http, https, mailto or tel. A scheme is text before the
    first `:` that comes before any `/`, `?` or `#`.

  Relative URLs pass. A literal `Url.new("javascript:…")` fails the build
  (`REFINEMENT_LITERAL_INVALID`). Any other value is checked at run time:
  - in the interpreter, by `TrustedValues.isSafeUrl`;
  - natively, by stdlib dispatcher op 51, emitted inline like `Html`'s
    validator.

**What is checked where.** The context rules run when Flow is lowered, which
is the only producer of templates. The IR verifier still re-checks each
template's structure, and now also accepts Int holes and refined-String
operands.

## Correction (same day, before item 6)

The first version of this check let any hole through once a URL attribute had
had one hole. So `href="{u}{s}"` compiled, and an empty Url followed by
`"javascript:…"` would have made a script URL. So did `href="{u}:{s}"`, where
Url `"javascript"` is followed by a literal `:`.

The rule above replaces it. Ten new assertions cover the bypasses (`{u}{s}`,
`{u}:{s}`, `{u}:alert(1)`, `{i}{s}`, `{u}&#58;x`, `&#106;{s}`), the safe forms
(`{u}?q={s}`, `&#47;{s}`), and a duplicate `type=` on `<script>`, where the
browser reads the first.

Campfire needed no change: its only hole followed by more text is
`{Url.new(base-url)}/searches/clear`. All 22 sampled responses still match.

## Decisions for review

- **Url or Int, not just any String, at the start of a URL.** The alternative
  was to sanitize at run time, replacing an unsafe URL with `#` as some
  frameworks do. That changes output silently. A type makes the decision
  visible where the URL is made, in the same way as `Html`.
- **`style` takes only Int holes.** That is strict. Campfire's two style holes
  were both numbers (`view-transition-name: edit-room-{id}`). A CSS-token type
  could come later if real code needs one.
- **Script text takes no holes at all.** A JSON-encoded hole type (with `<`
  escaped) would be the safe way to put data in a script. Campfire has none, so
  it is left for when it's needed.
- **The URL attribute list is fixed.** It is the HTML attributes whose value is
  a URL. `data-*-url` attributes are not on it: the browser doesn't navigate to
  them. A script that does is outside what a template check can see.

## Campfire

The build found 13 holes at the start of URL attributes and 2 non-Int `style`
holes. All are values Campfire builds itself, apart from one:

- **The Referer goes into an `href`.** `campfire.views.user-nav` put `back`,
  the request's Referer, into `href="{back}"`. Rails' `link_back` does the
  same. Browsers don't send `javascript:` referrers, but this is exactly what
  the rule is for. The page now uses the Referer only when `string.url-safe?`
  accepts it, and `/` otherwise.
- **The other 12** are avatar, sound-image, download and base URLs. Each is
  wrapped at the hole with `Url.new(…)`, checked at run time. Retyping the
  record fields as `Url` would move the check to where each URL is built; that
  is a wider refactor, left for later.
- **The two style holes** now use Ints. The sidebar uses its Int user id
  directly. The room page parses `PageChrome.room-id` back to an Int, because
  `PageChrome` keeps its ids as Strings. Retyping the field is the real fix, and
  is also left for later.

**Output:** all 22 sampled responses match the base build byte for byte (room
page, avatars, user pages, sidebar, account, search, paging).

## Checks

- **New suite:** `tests/AgentLang.TemplateContext.Tests`, 415 assertions after the correction (405 before).
  - 33 rule cases. Each URL attribute rejects a String at the start, including
    single quotes, upper-case `HREF`, `j{s}` and a leading space. They accept a
    Url, a String after `/`, `?`, `#` or `mailto:`, or an Int.
    - `onclick`, `style` and `srcdoc` reject String and Url holes, and accept
      Ints.
    - Script holes are rejected in plain, module and JSON scripts, and when
      `type` is itself a hole. They are accepted in `text/template` and
      `text/x-tmpl` scripts.
    - `<style>` text takes only Ints.
  - `Url.new("javascript:…")` fails the build, and `Url` can't be redeclared.
  - 24 URLs through `string.url-safe?` and through `Url.new` inside an `href`.
    The interpreter must match a separate reference, and native code must
    match the interpreter at O0, O2 and trusted O2. Unsafe values fail with
    `REFINEMENT_FAILED` on both backends. The cases cover upper-case schemes,
    `javascript:`, `vbscript:`, `data:`, `file:`, a tab inside a scheme, a
    leading space, a trailing space, a newline, DEL, `a/b:c` (relative), `a:b`
    (scheme `a`) and `//host`.
  - Int holes, natively, from `Int64.MinValue` to `MaxValue`, in content, in a
    `data-` attribute and in `style`.
  - Refined String holes, escaped natively.
- **Suites:** the 16 suites of report 301, plus TemplateContext, all pass.
  - Two older assertions encoded the old rules and were updated.
    OwningTemplates rejected an Int hole; it now rejects a Float hole.
    Report 300 already updated Flow.Tests' String `<` check.

## Speed and build time

- **Build:** the verified Campfire build took 41.4 s (40.7 s in report 301).
  The context rules run during lowering and cost nothing measurable.
- **Room page.** It now runs a `Url.new` check on each avatar, and writes its
  Int holes directly. ABBA runs (protocol of report 285, 4 vCPUs):

  | | 1 A (base) | 2 B (this) | 3 B (this) | 4 A (base) |
  | --- | ---: | ---: | ---: | ---: |
  | Uncached req/s | 1,166 | 1,183 | 1,182 | 1,167 |
  | Uncached CPU ms per request | 3.42 | 3.36 | 3.37 | 3.40 |
  | Cached req/s | 5,689 | 5,666 | 5,674 | 5,573 |
  | Cached CPU ms per request | 0.683 | 0.685 | 0.684 | 0.694 |

  Uncached, this build is about 1% faster. Both B runs beat both A runs, but
  that is close to the run-to-run spread. Cached, there is no change.

## Not done

- **Retyping Campfire's fields** as `Url` and Int (above).
- **A JSON hole type** for `<script>`.
- **`data-*-url` attributes** that scripts follow.

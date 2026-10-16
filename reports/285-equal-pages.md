# 285 — Equal pages: Flow serves Rails' bytes, and the fair sweep

For the same database, the Flow server now returns the bytes Rails returns,
apart from Rails' CSRF elements, which Flow and Rust leave out by design
(report 283). That holds for the room page and for three more routes. The
interleaved Linux sweep was rerun with that build. Its uncached column now
compares like work.

## What was built

**Message bodies (rich text), in Flow.** Rails' message_presentation
pipeline is mirrored at render time, on store misses:
- the canonical-markup checker and auto_link (`src/stdlib/rich-html.flow`);
- the HTML5 start-tag tokenizer, plus fragment queries for an element's text
  and its first match by name or class;
- sounds (`/play`), and Lexxy table figure stripping;
- mentions, using the possibly-expired SGID's GlobalID, with the page's users
  in one query;
- Open Graph embeds, both forms. A link or image counts only for a named host
  other than the request's (`web_url`). The partial is rendered as the
  sanitizer leaves it;
- RemoveSoloUnfurledLinkText, with Action Text's plain-text conversion;
- file attachments (Messages::AttachmentPresentation). Image and video
  previews are sized with Ruby's Integer and Float arithmetic and printed with
  Float#to_s. Signed blob ids and variation keys use the ActiveStorage verifier
  key (PBKDF2-SHA256, then HMAC-SHA1 from libcrypto). Attachments load in one
  query per page.

**Around the bodies:**
- `message--emoji` for messages whose plain text is all emoji;
- Download and Share in place of Reply for attachment messages;
- boost avatar titles from User#title.

**Routes:**
- `/rooms/<id>/@<message>` (rooms#show page_around). The forty messages
  before it, the message, and the forty after. A message the room doesn't
  have gets the last page.
- `/rooms/<id>/messages?before=|after=` (messages#index). Bare partials with
  fresh_when's weak ETag (SHA-256 of `[messages, template digest]`, cut to 32
  hex digits) and Last-Modified, plus Rails' Cache-Control. Also 204 when the
  page is empty, 304 on matching validators, and 404 for a room that isn't the
  user's or an unknown message.
- `/users/me/sidebar` (users/sidebars#show). The layout's head assets and
  lightbox are now shared functions, and the room page's bytes are unchanged.

**Host:** the default arena is now 128 MiB. It was 32, and the 81-message
`@message` page overran it. The arena is a lazy reservation, resident only as
touched.

## Verification

- **Rails oracle (`rails runner`, the reference app on the snapshot):** all
  169 messages' presentations match. That is 164 text and sound messages plus
  5 attachments.
- **Byte-identical to Rails without CSRF, on Linux:**

  | Page | Bytes |
  | --- | ---: |
  | Benchmark room | 417,052 |
  | Room 654632876 (sounds, embeds, mentions, attachments, emoji) | 356,704 |
  | `@933434570` window (81 messages) | 812,594 |
  | `@1` (falls back to the last page) | 417,052 |
  | `messages?before=` | 384,885 |
  | `messages?after=` | 384,885 |
  | `messages?before=<first>` (204) | — |
  | `/users/me/sidebar` | 30,806 |

  The paging ETag, Last-Modified and Cache-Control also equal Rails'.
- **Sweep binary checked first:** before the sweep, its binary served the
  benchmark room byte-identical with the store on and with it off.
- **Quick suites:** CampfireViews, Campfire.Http (475 assertions) and
  Campfire.Integration (583) passed on each commit before it went to main.
  Attached Flow tests ran on every server build.
- **Gates:** see "Gates" below.

## Performance: interleaved Linux sweep (protocol as 282/284, plan amended before any run)

There were 3 reps × 6 configurations, 18 lifetimes, all exit 0, with 0 invalid
responses in every cell. The server ran on vCPUs 8-11 and the load generator
on 12-19. The numbers are medians over the reps
(`.agentlang/linux-sweep-285/aggregate-s285b.md`).

| Peak (saturated cell, median of 3) | Uncached | Cached |
| --- | --- | --- |
| Flow | 1,221 req/s, **3.34 ms** CPU/req (32 conn), PSS 15 MB | 5,916 req/s, **0.67 ms** (16 conn), PSS 48 MB |
| Rust | 1,554 req/s, **2.40 ms** (16 conn), PSS 88 MB | 9,868 req/s, **0.40 ms** (64 conn), PSS 52 MB |
| Rails | not saturated (wait-bound, 0.79 util): 21.1 req/s, 150 ms | not saturated (wait-bound): 174 req/s, 19.0 ms |

**Uncached latency at 16 connections (p50 / p99):** Flow 9.9 / 39.4 ms;
Rust 9.8 / 21.0 ms. At 128: Flow 104 / 222, Rust 83 / 135.

**Against 284:**
- Flow uncached went from 3,488 req/s at 1.21 ms CPU/req to 1,221 req/s at
  3.34 ms. That is the cost of doing what Rails does to every body: the
  checker, auto_link, attachment and mention scans, plain text for the emoji
  test, and the per-page attachment and mention queries.
- Flow cached is unchanged within noise (6,094 → 5,916). Store hits don't
  re-render.
- Rust and Rails are unchanged.

## Fairness

- **Uncached is now comparable work, in Rust's favour.** Flow's page is
  Rails' page byte for byte. Rust renders the same rich text with html5ever.
  Its page is 416,127 bytes, 925 off Rails, from pre-equal-pages chrome
  differences. Rust does 1.27× Flow's uncached throughput at 0.72× its CPU
  per request.
- **Cached:** both serve hit fragments without re-rendering. Rust does 1.67×
  Flow's throughput.
- **Rails is wait-bound in every cell** (3 puma workers, about 0.8 of 4
  vCPUs). No fairness claim is made against it.
- **Where Flow's uncached cost goes:** rendering uses about 650 KB of arena
  per message, for about 10 KB of HTML (docs/PENDING-REVIEW.md). Strings are
  built by repeated concatenation in the checker, the tokenizer and the
  templates. That is the obvious target once output equality is done.
- **Contamination:**
  - The sweep was held earlier: first a Codex test run, then another
    session's Boxpush `node --test`, hung at one core for about 43 minutes.
    That process was stopped with the user's approval.
  - Codex was idle, Windows load was 1% and WSL load was 0.00 before the
    start (`contamination-screen.txt`).
  - The per-lifetime top-3 process capture came out empty. WSL's `ps` printed
    a terminal-size warning, so that column is missing.
  - The start-line load averages (4–6) are the previous lifetime decaying.
    Rep ranges stay within a few percent, so nothing points to interference.
- **Rails cache mode:** Redis held about 178 fragment keys in the cached runs
  and 4–6 base keys in the uncached ones. `cache-env.txt` records only the
  Rust and Flow settings, as in 284.

## Not carried yet, or approximated

These are noted in the code and in docs/EQUAL-PAGES-PLAN.md:
- **Plain text** for lists, quotes and captions is approximated. It is used
  only to compare against a URL and for the emoji test.
- **Initials:** ASCII upper-casing only.
- **Previews** follow the reference environment: ffmpeg is present, so videos
  preview; poppler and mupdf are absent, so PDFs don't.
- **Metadata dimensions** in exponent form are treated as absent.
- **404 bodies** are the host's, not `public/404.html`.
- **Not yet compared with Rails:** a member's sidebar, and
  `restrict_room_creation_to_administrators`.

## Gates

All 25 regression gates pass on 7fa621c (`.agentlang/lang-285/Run-Gates.ps1`,
`gates-20261016T020303Z`, 5,698 s). The run started after the sweep, so it
could not perturb it.

## Evidence

`reports/evidence/285-equal-pages/` holds 508 entries. The zip's sha256 is
`f73ba3f1395e3c7352eaa4479f0ca16431a403fe775947eed742d0a86153ed4f`. It
contains:
- the gate logs;
- the sweep plan, scripts, logs, aggregate and every lifetime's files;
- the pages-285 tooling;
- the sources changed since 284.

The value-based secret scan found 0 hits. The Rails pages, headers and
oracle data under `pages-285/private` are left out.

## Next

- **Remaining routes**, in the EQUAL-PAGES-PLAN order: search; users and
  avatars; account; sign-in and sign-out (bcrypt needs a decision); message
  and boost writes, with sanitization moving inbound (the RichHtml type).
- **Typed schema decoders** and the validated migration.
- **Flow's per-message render cost.**

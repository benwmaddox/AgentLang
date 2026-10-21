# 304 — Item 6: strings without re-scans

Item 6 is about writing templates and strings straight to the response, with
better string concatenation. The profile taken for item 1 (report 301) showed
where string handling actually costs on the uncached room page:
- the runtime re-scans and re-validates values it built itself, about 39% of
  flat CPU (`scan_text`, `scan_value`, `validate_type_descriptor`,
  `scan_readable`);
- concatenation and copying take about 15%.

So the item runs in three steps. Each is measured on its own: one commit, an
ABBA run on both columns, and a profile.
- **(a)** Fast equality for String, Bytes and refined Strings.
- **(b)** Trusted measuring: a value's size, read without re-validating it.
- **(c)** Concatenation and output, designed from the profile taken after (a)
  and (b).

**Safety limit for all three:** the fast paths apply only to values the
generated code built, in trusted builds. Bytes from the host (database rows,
cache hits, the request) are still validated where they enter. The diagnostic
profile keeps every check.

## (a) Fast String equality

**Target:** `al_owning_equal_typed`, 14.4% of uncached CPU, inclusive.
`rich-html.allowed-tag?` and `rich-html.tag` compare tag names with `==`. Each
comparison validated the type graph, then scanned both values in full, then
compared their bytes.

**Change:**
- In trusted builds, `equals` on two Strings, two Bytes or two of a refined
  String type calls `al_owning_string_equal`. It compares the two byte counts,
  then the bytes, 8 at a time; the payloads are 8-byte aligned.
- Diagnostic builds keep `al_owning_equal_typed`. The generated modules show
  each profile using its own path.

**Checks:**
- NativeParity grows to 5,003 assertions. 225 pairs of Strings are compared
  as Strings, as Bytes and as a refined type, at O0, O2 and trusted O2. They
  include lengths 7, 8, 9 and 16 around the 8-byte word, equal prefixes,
  combining marks, emoji, and 99 or 100 bytes.
- All 17 suites pass, and all 22 sampled responses match the base build.

**Speed** (ABBA against the item-4 build; protocol of report 285, 4 vCPUs):

| | 1 A (item 4) | 2 B (6a) | 3 B (6a) | 4 A (item 4) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,208 | 1,407 | 1,418 | 1,202 |
| Uncached CPU ms per request | 3.32 | 2.85 | 2.83 | 3.32 |
| Cached req/s | 5,699 | 5,735 | 5,729 | 5,724 |
| Cached CPU ms per request | 0.675 | 0.679 | 0.680 | 0.682 |

- **Uncached: 17% more requests a second, and 15% less CPU per request.**
- Cached: no change, because the cached room page serves message bodies from
  the worker store.
- **Profile after (a):** `equal_typed` fell from 14.4% to 0.8% inclusive, and
  `string_equal` costs 1.1%. `rich-html.allowed-tag?` fell from 11.2% to
  6.6%; its 52-entry list is still rebuilt on every call.
- **Build:** 39.2 s.

## (b) Trusted measuring

Next. Report to follow.

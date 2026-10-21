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

**Target:** the scans under list iteration, `list.get` and Option/Result case
access in generated code:
- `al_owning_measure_value`, 14.6% inclusive;
- `al_owning_locate_sum_case`, 10.1%;
- `al_owning_locate_field`, 3.9%.

Their flat costs were mostly `scan_text` (11.2% self),
`validate_type_descriptor` (6.7% self) and `scan_readable`. Report 270 had
already skipped the type-graph check for values verified code built, but each
node was still re-checked.

**Change:** C only, in trusted builds, for internal views (values the generated
code built). It extends report 270's rule one level down:
- `validate_type_descriptor` returns the compiler-emitted descriptor without
  re-checking it.
- `scan_readable` checks bounds only, and skips the initialization bitmap:
  generated code initializes every byte it builds.
- `scan_text` takes a String's or Bytes value's size from its header, checked
  against its owner. It skips the reserved-byte and padding loops, which are
  zero by construction. UTF-8 was already checked only for external views.

External views keep the full walk: host input, cache hits and anything
`measure_external_value` reads. So do diagnostic builds.

**The differential test** (NativeParity, now 6,590 assertions). 24 seeded
random `List<Shelf>` values are generated:
- a `Shelf` holds a String, a `List<Note>` and an `Option<Note>`;
- a `Note` holds a String, a `List<String>`, an `Option<String>`, a
  `Result<Int, String>` and a Bool;
- Strings are 0 to 20 characters, mixing ASCII, `é`, `🚀`, `日`, space, `<`
  and `&`.

Three programs walk those values: nested loops, `list.get` twice, and matches
on every Option and Result. The interpreter must match an F# reference, and
diagnostic O2 (the full scanner) and trusted O2 (the fast path) must both
match the interpreter. The test passed on the runtime before this change, and
passes after it, with the trusted artifacts confirmed to carry the new C.

**Speed** (ABBA against the 6a build):

| | 1 A (6a) | 2 B (6b) | 3 B (6b) | 4 A (6a) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,411 | 1,660 | 1,651 | 1,416 |
| Uncached CPU ms per request | 2.84 | 2.42 | 2.42 | 2.84 |
| Cached req/s | 5,804 | 6,154 | 6,074 | 5,772 |
| Cached CPU ms per request | 0.676 | 0.631 | 0.634 | 0.679 |

- **Uncached: 17% more requests a second, and 15% less CPU per request.**
- **Cached: 5% more requests a second, and 7% less CPU.** The cached page
  still loops over its messages, users and Options outside the store. Hits from
  the store are external bytes, so they are still validated.
- Output: all 22 sampled responses match. Build: 39.0 s.
- **Together, (a) and (b)** take the uncached room page from 1,205 to 1,655
  req/s (+37%), at 2.42 ms of CPU per request instead of 3.32.

  Report 285 measured the Rust port at 1,554 req/s under the same protocol,
  but in another session, so that comparison is not made here.

/* Exhaustive check of al_owning_find_bytes (report 312) against the naive
 * search it replaced: needles of 0-4 bytes at every position of texts up to 40
 * bytes, near-misses, repeated first bytes, and random texts over a small
 * alphabet so matches are common. */
#define AL_OWNING_TRUSTED_GENERATED 1
#include "owning_stack_runtime.c"
#include <stdio.h>
#include <string.h>

static uint32_t naive(const uint8_t *t, uint32_t n, uint32_t from, const uint8_t *s, uint32_t m) {
  uint32_t i, k;
  if (m == 0u) return from <= n ? from : n;
  for (i = from; m <= n && i <= n - m; ++i) {
    for (k = 0u; k < m && t[i + k] == s[k]; ++k) {
    }
    if (k == m) return i;
  }
  return n;
}

static uint64_t next_random(uint64_t *state) {
  *state = *state * 6364136223846793005ull + 1442695040888963407ull;
  return *state >> 33u;
}

static long checks = 0, failures = 0;
static void check(const uint8_t *t, uint32_t n, uint32_t from, const uint8_t *s, uint32_t m) {
  const uint32_t a = naive(t, n, from, s, m);
  const uint32_t b = al_owning_find_bytes(t, n, from, s, m);
  ++checks;
  if (a != b && failures++ < 5)
    printf("mismatch n=%u from=%u m=%u: %u vs %u\n", n, from, m, a, b);
}

int main(void) {
  uint8_t text[64];
  uint8_t needle[8];
  uint64_t seed = 312u;
  uint32_t n, m, pos, from;
  int r;
  /* A needle planted at every position, with every start. */
  for (n = 0u; n <= 40u; ++n)
    for (m = 0u; m <= 4u && m <= n; ++m)
      for (pos = 0u; pos + m <= n; ++pos)
        for (from = 0u; from <= n + 1u; ++from) {
          memset(text, 'a', n);
          memcpy(needle, "xyzw", m);
          memcpy(text + pos, needle, m);
          check(text, n, from, needle, m);
          /* A near miss: the first byte without the rest. */
          if (m > 1u) {
            text[pos + m - 1u] = 'q';
            check(text, n, from, needle, m);
          }
        }
  /* Random texts over "ab", needles over "ab": many overlapping candidates. */
  for (r = 0; r < 300000; ++r) {
    n = (uint32_t)(next_random(&seed) % 48u);
    m = (uint32_t)(next_random(&seed) % 5u);
    from = (uint32_t)(next_random(&seed) % (n + 2u));
    for (pos = 0u; pos < n; ++pos) text[pos] = (uint8_t)('a' + next_random(&seed) % 2u);
    for (pos = 0u; pos < m; ++pos) needle[pos] = (uint8_t)('a' + next_random(&seed) % 2u);
    check(text, n, from, needle, m);
  }
  /* High bytes, so the word test's borrows cross byte boundaries. */
  for (r = 0; r < 300000; ++r) {
    n = (uint32_t)(next_random(&seed) % 48u);
    m = 1u + (uint32_t)(next_random(&seed) % 3u);
    from = (uint32_t)(next_random(&seed) % (n + 2u));
    for (pos = 0u; pos < n; ++pos) text[pos] = (uint8_t)(next_random(&seed) % 4u == 0u ? 0x00u : 0x80u + next_random(&seed) % 3u);
    for (pos = 0u; pos < m; ++pos) needle[pos] = (uint8_t)(next_random(&seed) % 2u == 0u ? 0x00u : 0x80u + next_random(&seed) % 3u);
    check(text, n, from, needle, m);
  }
  printf("%ld checks, %ld failures\n", checks, failures);
  return failures != 0;
}

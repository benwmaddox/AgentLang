/* Exhaustive check of the 8-byte HTML escape scan (report 310) against the
 * scalar loop it replaced: every byte value at every position of windows of
 * length 0-24, pairs of specials, and random strings. */
#define AL_OWNING_TRUSTED_GENERATED 1
#include "owning_stack_runtime.c"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t ref_extra(const uint8_t *d, uint32_t n) {
  uint64_t t = 0;
  for (uint32_t i = 0; i < n; ++i)
    if (al_owning_html_is_special(d[i])) t += al_owning_html_escape_extra(d[i]);
  return t;
}

static uint32_t ref_into(uint8_t *dst, const uint8_t *src, uint32_t n) {
  uint32_t w = 0, run = 0;
  for (uint32_t i = 0; i < n; ++i) {
    if (al_owning_html_is_special(src[i])) {
      const char *e = al_owning_html_escape_entity(src[i]);
      memcpy(dst + w, src + run, i - run); w += i - run;
      while (*e) dst[w++] = (uint8_t)*e++;
      run = i + 1;
    }
  }
  memcpy(dst + w, src + run, n - run);
  return w + (n - run);
}

static uint64_t next_random(uint64_t *state) {
  *state = *state * 6364136223846793005ull + 1442695040888963407ull;
  return *state >> 33u;
}

static long checks = 0, failures = 0;
static void check(const uint8_t *src, uint32_t n) {
  static uint8_t a[4096], b[4096];
  memset(a, 0xAA, sizeof a); memset(b, 0xAA, sizeof b);
  uint64_t e1 = ref_extra(src, n), e2 = al_owning_html_escape_extra_total(src, n);
  uint32_t w1 = ref_into(a, src, n), w2 = al_owning_html_escape_into(b, src, n);
  ++checks;
  if (e1 != e2 || w1 != w2 || memcmp(a, b, sizeof a) != 0 || w1 != n + e1) {
    if (failures++ < 5) printf("mismatch n=%u extra %llu/%llu written %u/%u\n", n,
                               (unsigned long long)e1, (unsigned long long)e2, w1, w2);
  }
}

int main(void) {
  uint8_t buf[600];
  /* One byte of every value at every position, over two backgrounds. */
  for (int bg = 0; bg < 2; ++bg)
    for (uint32_t n = 0; n <= 24; ++n)
      for (uint32_t pos = 0; pos < (n ? n : 1); ++pos)
        for (int v = 0; v < 256; ++v) {
          memset(buf, bg ? '&' - 1 : 'a', n);
          if (n) buf[pos] = (uint8_t)v;
          check(buf, n);
        }
  /* Two specials anywhere in 17 bytes, and all-special runs. */
  const uint8_t sp[] = {'&', '\'', '<', '>', '"'};
  for (uint32_t i = 0; i < 17; ++i)
    for (uint32_t j = 0; j < 17; ++j)
      for (int a = 0; a < 5; ++a)
        for (int b = 0; b < 5; ++b) {
          memset(buf, 'x', 17); buf[i] = sp[a]; buf[j] = sp[b]; check(buf, 17);
        }
  for (uint32_t n = 0; n <= 40; ++n) { for (uint32_t i = 0; i < n; ++i) buf[i] = sp[i % 5]; check(buf, n); }
  /* Random strings, biased toward specials and their neighbours. */
  uint64_t seed = 310u;
  for (int r = 0; r < 200000; ++r) {
    uint32_t n = (uint32_t)(next_random(&seed) % 600u);
    for (uint32_t i = 0; i < n; ++i) {
      uint32_t k = (uint32_t)(next_random(&seed) % 10u);
      buf[i] = k < 2u ? sp[next_random(&seed) % 5u] : k < 4u ? (uint8_t)(0x20u + next_random(&seed) % 0x20u) : (uint8_t)(next_random(&seed) % 256u);
    }
    check(buf, n);
  }
  printf("%ld checks, %ld failures\n", checks, failures);
  return failures != 0;
}

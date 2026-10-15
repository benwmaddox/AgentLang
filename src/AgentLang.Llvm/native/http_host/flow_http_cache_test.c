/* Concurrency stress test for flow_http_cache.c (run under ThreadSanitizer and
 * AddressSanitizer on Linux with clang; plain on Windows):
 *   1. single thread: miss, put, hit, first writer wins, disabled cache;
 *   2. racing puts of the same keys: exactly one insert per key, every reader
 *      sees one stable value;
 *   3. many threads racing get/put on overlapping keys with a tiny budget:
 *      every hit's bytes match its key, generations roll over under load and
 *      retired generations are freed by epoch reclamation.
 * Build (Linux): clang -std=gnu11 -O1 -g -fsanitize=thread flow_http_cache.c
 *   flow_http_cache_test.c -lpthread -o cache_test && ./cache_test
 * Prints "CACHE TEST PASS ..." and exits 0, else prints the failure, exits 1. */
#include "flow_http_cache.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
typedef HANDLE fh_thread;
#define THREAD_RETURN DWORD WINAPI
#define THREAD_VALUE 0
#else
#include <pthread.h>
typedef pthread_t fh_thread;
#define THREAD_RETURN void *
#define THREAD_VALUE NULL
#endif

#define THREADS 8u
#define KEYS 3000u
#define ITERATIONS 60000u
#define RACE_KEYS 500u

static int g_failed;

static void fail(const char *what) {
  if (!__atomic_exchange_n(&g_failed, 1, __ATOMIC_SEQ_CST)) fprintf(stderr, "CACHE TEST FAIL: %s\n", what);
}

static void start(fh_thread *t, THREAD_RETURN (*body)(void *), void *argument) {
#if defined(_WIN32)
  *t = CreateThread(0, 0, (LPTHREAD_START_ROUTINE)body, argument, 0, 0);
#else
  pthread_create(t, 0, body, argument);
#endif
}

static void join(fh_thread t) {
#if defined(_WIN32)
  WaitForSingleObject(t, INFINITE);
  CloseHandle(t);
#else
  pthread_join(t, 0);
#endif
}

/* Key k's value: a length of 64..2111 bytes derived from k, bytes from k. */
static uint32_t value_for(uint32_t k, uint8_t *out) {
  uint32_t length = 64u + (k * 2654435761u >> 21) % 2048u, i;
  for (i = 0u; i < length; ++i) out[i] = (uint8_t)(k * 31u + i * 7u);
  return length;
}

static uint32_t key_for(uint32_t k, char *out) {
  return (uint32_t)sprintf(out, "views/test/_item:digest/items/%u-20261014/presentation-v3", k);
}

static uint64_t next_random(uint64_t *state) {
  *state ^= *state << 13; *state ^= *state >> 7; *state ^= *state << 17;
  return *state;
}

static THREAD_RETURN stress(void *argument) {
  uint32_t worker = (uint32_t)(uintptr_t)argument, n;
  uint64_t state = 0x9e3779b97f4a7c15ull ^ ((uint64_t)worker << 32 | 1u);
  uint8_t expected[4096], copy[4096];
  char key[128];
  for (n = 0u; n < ITERATIONS && !g_failed; ++n) {
    uint32_t k = (uint32_t)(next_random(&state) % KEYS), key_length = key_for(k, key);
    const uint8_t *value = 0;
    uint32_t value_length = 0u, expected_length = value_for(k, expected);
    fh_cache_enter(worker);
    if (fh_cache_get(worker, (const uint8_t *)key, key_length, &value, &value_length)) {
      if (value_length != expected_length) fail("hit length differs from the key's value");
      else {
        memcpy(copy, value, value_length); /* the arena copy */
        if (memcmp(copy, expected, value_length) != 0) fail("hit bytes differ from the key's value");
      }
    } else {
      fh_cache_put(worker, (const uint8_t *)key, key_length, expected, expected_length);
    }
    fh_cache_leave(worker);
  }
  return THREAD_VALUE;
}

typedef struct race_arg { uint32_t worker; uint32_t inserted; } race_arg;

static THREAD_RETURN race(void *argument) {
  race_arg *a = (race_arg *)argument;
  uint32_t k;
  char key[64], value[32];
  for (k = 0u; k < RACE_KEYS; ++k) {
    uint32_t key_length = (uint32_t)sprintf(key, "race/%u", k), value_length = (uint32_t)sprintf(value, "writer-%u", a->worker);
    fh_cache_enter(a->worker);
    a->inserted += (uint32_t)fh_cache_put(a->worker, (const uint8_t *)key, key_length, (const uint8_t *)value, value_length);
    fh_cache_leave(a->worker);
  }
  return THREAD_VALUE;
}

static int get_text(uint32_t worker, const char *key, char *out) {
  const uint8_t *value;
  uint32_t length;
  int hit;
  fh_cache_enter(worker);
  hit = fh_cache_get(worker, (const uint8_t *)key, (uint32_t)strlen(key), &value, &length);
  if (hit) { memcpy(out, value, length); out[length] = 0; }
  fh_cache_leave(worker);
  return hit;
}

static int put_text(uint32_t worker, const char *key, const char *value) {
  int inserted;
  fh_cache_enter(worker);
  inserted = fh_cache_put(worker, (const uint8_t *)key, (uint32_t)strlen(key), (const uint8_t *)value, (uint32_t)strlen(value));
  fh_cache_leave(worker);
  return inserted;
}

int main(void) {
  fh_thread threads[THREADS];
  race_arg races[THREADS];
  fh_cache_stats stats;
  char text[64];
  uint32_t i, inserted = 0u;

  /* 1. Basics. */
  if (fh_cache_init(0u, 1u) != 0 || fh_cache_enabled()) fail("budget 0 disables");
  if (put_text(0u, "a", "1") || get_text(0u, "a", text)) fail("disabled cache stores nothing");
  if (fh_cache_init(4u << 20, THREADS) != 0 || !fh_cache_enabled()) fail("init");
  if (get_text(0u, "a", text)) fail("empty cache misses");
  if (!put_text(0u, "a", "first")) fail("first put inserts");
  if (put_text(1u, "a", "second")) fail("second put of the same key is dropped");
  if (!get_text(2u, "a", text) || strcmp(text, "first") != 0) fail("first writer wins");
  if (!put_text(0u, "empty", "") || !get_text(0u, "empty", text) || text[0] != 0) fail("empty value");

  /* 2. Racing puts of the same keys. */
  for (i = 0u; i < THREADS; ++i) { races[i].worker = i; races[i].inserted = 0u; start(&threads[i], race, &races[i]); }
  for (i = 0u; i < THREADS; ++i) { join(threads[i]); inserted += races[i].inserted; }
  if (inserted != RACE_KEYS) fail("exactly one racing put per key inserts");
  for (i = 0u; i < RACE_KEYS; ++i) {
    char key[32], first[64];
    sprintf(key, "race/%u", i);
    if (!get_text(0u, key, first) || strncmp(first, "writer-", 7) != 0) { fail("raced key readable"); break; }
    if (!get_text(3u, key, text) || strcmp(first, text) != 0) { fail("raced key stable"); break; }
  }
  fh_cache_shutdown();

  /* 3. Overlapping get/put under load with a 256 KiB budget (128 KiB per
   * generation, about 120 values of ~1 KiB): constant rollover. */
  /* Then 3 MiB (about 1,400 of the 3,000 values per generation): mostly hits. */
  {
    static const uint64_t budgets[2] = { 256u << 10, 3u << 20 };
    uint32_t b;
    for (b = 0u; b < 2u; ++b) {
      if (fh_cache_init(budgets[b], THREADS) != 0) fail("init small");
      for (i = 0u; i < THREADS; ++i) start(&threads[i], stress, (void *)(uintptr_t)i);
      for (i = 0u; i < THREADS; ++i) join(threads[i]);
      fh_cache_enter(0u); fh_cache_leave(0u); /* every worker idle: reclaim the rest */
      fh_cache_stats_read(&stats);
      if (stats.rollovers < 10u) fail("generations roll over under load");
      if (stats.generations_freed != stats.rollovers) fail("every retired generation is freed once idle");
      if (stats.hits == 0u || stats.puts == 0u) fail("hits and puts happen");
      fh_cache_shutdown();
      printf("budget %llu KiB: hits=%llu misses=%llu puts=%llu dropped=%llu rollovers=%llu freed=%llu\n",
             (unsigned long long)(budgets[b] >> 10), (unsigned long long)stats.hits, (unsigned long long)stats.misses,
             (unsigned long long)stats.puts, (unsigned long long)stats.dropped, (unsigned long long)stats.rollovers,
             (unsigned long long)stats.generations_freed);
    }
  }

  if (g_failed) return 1;
  printf("CACHE TEST PASS\n");
  return 0;
}

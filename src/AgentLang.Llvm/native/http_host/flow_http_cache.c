/* Process-wide, lock-free fragment cache for the portable Flow HTTP host.
 *
 * Shape. One cache shared by every worker thread. The current *generation* is
 * an insert-only open-addressing hash table (linear probing) of pointers to
 * entries. An entry (hash, key bytes, value bytes) is allocated with malloc,
 * outside every request arena, filled completely, and then published into an
 * empty slot with one compare-and-swap; it is never mutated afterwards. Slots
 * are never cleared, so a probe that reaches an empty slot is a miss. A put
 * whose key is already present (or wins a slot race against the same key)
 * frees its own unpublished entry: the first writer wins.
 *
 * Bounds. The byte budget (--cache-mb) is split in two: each generation
 * accepts up to budget/2 entry bytes and at most slot_count/2 entries, both
 * reserved with fetch-add *before* an entry is inserted, so a generation's
 * table is never more than half full and a probe always terminates. When a
 * put cannot reserve, the generation is full: the putter allocates a new empty
 * generation and swaps it in with a CAS on the current pointer (losers free
 * their unpublished table), then retries once. The old generation is retired
 * onto a push-only list. While a retired generation is still waiting to be
 * freed, a full generation does not roll over again (its puts are dropped), so
 * at most two generations, the current and one retired, are alive: peak memory
 * is about the budget plus the two slot arrays (a racing rollover can rarely
 * leave a second retired generation; it is freed the same way). Superseded
 * entries (an older updated_at of an edited record) stay until rollover.
 *
 * Reclamation (epochs). A global epoch counter starts at 1. Worker w
 * announces, for the whole request, the epoch it read at request start
 * (announced[w] = epoch), and announces 0 at request end. Retiring a
 * generation bumps the epoch: retire_epoch = ++epoch, taken after the CAS that
 * unpublished the generation. A retired generation is freed when every worker
 * shows announced == 0 or announced >= retire_epoch. Freeing takes the whole
 * retired list with one exchange (so exactly one thread owns each node), frees
 * what is safe and pushes the rest back with a CAS loop.
 *
 * Memory ordering. Every access to `current`, `epoch`, `announced[]` is
 * seq_cst, so they all lie in one total order S consistent with each thread's
 * program order; a seq_cst load returns the last store before it in S.
 *  - Request start is (a) load epoch -> e, (b) store announced[w] = e,
 *    (c) load current. (b) is a seq_cst store followed by a seq_cst load (c),
 *    which may not be reordered (this is the store-load pair that acquire/
 *    release alone would allow to reorder, even on x86).
 *  - Retire is (1) CAS current: G -> G', (2) fetch-add epoch -> r, then later
 *    (3) load announced[w] for every w, (4) free G.
 *  Take any request R of worker w that loaded G at its (c). (c) returned G,
 *  so (c) precedes (1) in S, hence (a) < (b) < (c) < (1) < (2) < (3).
 *  (3) returns the last store to announced[w] before it in S, which is R's
 *  (b) or a later store of w:
 *   - R's (b): its value e was read by (a), before (2), so e < r and (4) does
 *     not run (the generation stays on the retired list);
 *   - a later store X (R's end store d = 0, or a later request's announce):
 *     X is sequenced after d, and (3), a seq_cst (acquire) load, reads from X,
 *     a seq_cst (release) store, so every access R made to G happens-before
 *     (3) and therefore before (4).
 *  A request whose (c) follows (1) in S reads G' or later, never G. So when
 *  (4) runs, no request can still read G or its entries. (A request that
 *  read the epoch between (1) and (2) announces r - 1 and merely delays (4)
 *  until it ends: the check is conservative.)
 *  Entries: a slot CAS is a release and slot loads are acquire, so a reader
 *  that sees an entry pointer sees its fully written bytes. A generation is
 *  published by a seq_cst CAS (a release) after it is zero-filled.
 *
 * No mutexes, spin locks or reader-writer locks: every loop is a probe over a
 * finite table or a CAS retry that only fails when another thread made
 * progress. malloc/free are the C library's (their internal synchronization is
 * the allocator's). Builtins: GCC/clang __atomic_* (clang on Windows and
 * Linux). */
#include "flow_http_cache.h"

#include <stdlib.h>
#include <string.h>

#define LOAD(p) __atomic_load_n((p), __ATOMIC_SEQ_CST)
#define STORE(p, v) __atomic_store_n((p), (v), __ATOMIC_SEQ_CST)

typedef struct fh_cache_entry {
  uint64_t hash;
  uint32_t key_length;
  uint32_t value_length;
  uint8_t bytes[]; /* key then value */
} fh_cache_entry;

typedef struct fh_cache_generation {
  struct fh_cache_generation *next_retired;
  uint64_t retire_epoch;
  uint64_t byte_budget;
  uint64_t bytes;   /* reserved entry bytes (atomic) */
  uint64_t entries; /* reserved entries (atomic) */
  uint64_t max_entries;
  uint64_t mask;
  fh_cache_entry *slots[]; /* atomic pointers, mask + 1 of them */
} fh_cache_generation;

typedef struct fh_cache_worker {
  uint64_t announced; /* atomic: 0 idle, else the epoch read at request start */
  uint64_t hits, misses, puts, dropped; /* owner-written only */
  uint8_t pad[64 - 5 * sizeof(uint64_t) > 0 ? 64 - 5 * sizeof(uint64_t) : 1];
} fh_cache_worker;

static int g_enabled;
static uint32_t g_workers;
static uint64_t g_generation_bytes;
static uint64_t g_generation_slots;
static fh_cache_generation *g_current; /* atomic */
static fh_cache_generation *g_retired; /* atomic head of the push-only list */
static uint64_t g_epoch = 1u;          /* atomic */
static uint64_t g_rollovers;           /* atomic */
static uint64_t g_freed;               /* atomic */
static fh_cache_worker g_worker[FH_CACHE_MAX_WORKERS];

static uint64_t fh_cache_hash(const uint8_t *data, uint32_t length) {
  uint64_t hash = 1469598103934665603ull;
  uint32_t i;
  for (i = 0u; i < length; ++i) { hash ^= data[i]; hash *= 1099511628211ull; }
  hash ^= hash >> 29;
  return hash;
}

static fh_cache_generation *fh_generation_new(void) {
  size_t size = sizeof(fh_cache_generation) + (size_t)g_generation_slots * sizeof(fh_cache_entry *);
  fh_cache_generation *g = (fh_cache_generation *)calloc(1u, size);
  if (g == 0) return 0;
  g->byte_budget = g_generation_bytes;
  g->max_entries = g_generation_slots / 2u;
  g->mask = g_generation_slots - 1u;
  return g;
}

static void fh_generation_free(fh_cache_generation *g) {
  uint64_t i;
  for (i = 0u; i <= g->mask; ++i) free(g->slots[i]);
  free(g);
}

int fh_cache_init(uint64_t budget_bytes, uint32_t workers) {
  uint64_t entries, slots = 64u;
  if (workers == 0u || workers > FH_CACHE_MAX_WORKERS) return 1;
  g_workers = workers;
  memset(g_worker, 0, sizeof(g_worker));
  g_enabled = 0;
  g_current = 0;
  g_retired = 0;
  g_epoch = 1u;
  g_rollovers = 0u;
  g_freed = 0u;
  if (budget_bytes == 0u) return 0;
  g_generation_bytes = budget_bytes / 2u;
  /* One slot pair per 512 budget bytes (fragments are usually larger), at
   * least 64 and at most 2^22 slots. */
  entries = g_generation_bytes / 512u;
  if (entries < 32u) entries = 32u;
  if (entries > (1u << 21)) entries = 1u << 21;
  while (slots < entries * 2u) slots <<= 1;
  g_generation_slots = slots;
  g_current = fh_generation_new();
  if (g_current == 0) return 1;
  g_enabled = 1;
  return 0;
}

int fh_cache_enabled(void) { return g_enabled; }

static int fh_retired_is_safe(const fh_cache_generation *g) {
  uint32_t w;
  for (w = 0u; w < g_workers; ++w) {
    uint64_t a = LOAD(&g_worker[w].announced);
    if (a != 0u && a < g->retire_epoch) return 0;
  }
  return 1;
}

static void fh_retired_push(fh_cache_generation *g) {
  fh_cache_generation *head = __atomic_load_n(&g_retired, __ATOMIC_ACQUIRE);
  do { g->next_retired = head; }
  while (!__atomic_compare_exchange_n(&g_retired, &head, g, 1, __ATOMIC_SEQ_CST, __ATOMIC_ACQUIRE));
}

/* Frees every retired generation no worker can reference. */
static void fh_cache_reclaim(void) {
  fh_cache_generation *list, *next;
  if (__atomic_load_n(&g_retired, __ATOMIC_ACQUIRE) == 0) return;
  list = __atomic_exchange_n(&g_retired, (fh_cache_generation *)0, __ATOMIC_SEQ_CST);
  for (; list != 0; list = next) {
    next = list->next_retired;
    if (fh_retired_is_safe(list)) {
      fh_generation_free(list);
      __atomic_fetch_add(&g_freed, 1u, __ATOMIC_RELAXED);
    } else {
      fh_retired_push(list);
    }
  }
}

void fh_cache_shutdown(void) {
  fh_cache_generation *g, *next;
  if (!g_enabled) return;
  for (g = g_retired; g != 0; g = next) { next = g->next_retired; fh_generation_free(g); }
  g_retired = 0;
  if (g_current != 0) fh_generation_free(g_current);
  g_current = 0;
  g_enabled = 0;
}

void fh_cache_enter(uint32_t worker) {
  if (!g_enabled) return;
  STORE(&g_worker[worker].announced, LOAD(&g_epoch));
}

void fh_cache_leave(uint32_t worker) {
  if (!g_enabled) return;
  STORE(&g_worker[worker].announced, (uint64_t)0u);
  fh_cache_reclaim();
}

static const fh_cache_entry *fh_cache_find(fh_cache_generation *g, uint64_t hash,
                                           const uint8_t *key, uint32_t key_length) {
  uint64_t i = hash & g->mask, probes;
  for (probes = 0u; probes <= g->mask; ++probes, i = (i + 1u) & g->mask) {
    const fh_cache_entry *e = __atomic_load_n(&g->slots[i], __ATOMIC_ACQUIRE);
    if (e == 0) return 0;
    if (e->hash == hash && e->key_length == key_length && memcmp(e->bytes, key, key_length) == 0) return e;
  }
  return 0;
}

int fh_cache_get(uint32_t worker, const uint8_t *key, uint32_t key_length,
                 const uint8_t **value, uint32_t *value_length) {
  fh_cache_generation *g;
  const fh_cache_entry *e;
  if (!g_enabled) return 0;
  g = LOAD(&g_current);
  e = fh_cache_find(g, fh_cache_hash(key, key_length), key, key_length);
  if (e == 0) { ++g_worker[worker].misses; return 0; }
  ++g_worker[worker].hits;
  *value = e->bytes + e->key_length;
  *value_length = e->value_length;
  return 1;
}

/* Swaps in a new empty generation for the full one `full`. Returns 0 when
 * the put should be dropped (a retired generation is still pending). */
static int fh_cache_rollover(fh_cache_generation *full) {
  fh_cache_generation *fresh, *expected = full;
  fh_cache_reclaim();
  if (__atomic_load_n(&g_retired, __ATOMIC_ACQUIRE) != 0) return 0;
  if (LOAD(&g_current) != full) return 1; /* someone already rolled over */
  fresh = fh_generation_new();
  if (fresh == 0) return 0;
  if (!__atomic_compare_exchange_n(&g_current, &expected, fresh, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)) {
    free(fresh); /* never published */
    return 1;
  }
  full->retire_epoch = __atomic_add_fetch(&g_epoch, 1u, __ATOMIC_SEQ_CST);
  __atomic_fetch_add(&g_rollovers, 1u, __ATOMIC_RELAXED);
  fh_retired_push(full);
  return 1;
}

int fh_cache_put(uint32_t worker, const uint8_t *key, uint32_t key_length,
                 const uint8_t *value, uint32_t value_length) {
  uint64_t hash, size, i;
  int attempt;
  fh_cache_entry *mine;
  if (!g_enabled) return 0;
  hash = fh_cache_hash(key, key_length);
  size = sizeof(fh_cache_entry) + (uint64_t)key_length + value_length;
  if (size > g_generation_bytes) { ++g_worker[worker].dropped; return 0; }
  for (attempt = 0; attempt < 2; ++attempt) {
    fh_cache_generation *g = LOAD(&g_current);
    if (fh_cache_find(g, hash, key, key_length) != 0) { ++g_worker[worker].dropped; return 0; }
    if (__atomic_fetch_add(&g->entries, 1u, __ATOMIC_RELAXED) >= g->max_entries ||
        __atomic_fetch_add(&g->bytes, size, __ATOMIC_RELAXED) + size > g->byte_budget) {
      if (!fh_cache_rollover(g)) break;
      continue;
    }
    mine = (fh_cache_entry *)malloc((size_t)size);
    if (mine == 0) break;
    mine->hash = hash;
    mine->key_length = key_length;
    mine->value_length = value_length;
    memcpy(mine->bytes, key, key_length);
    if (value_length > 0u) memcpy(mine->bytes + key_length, value, value_length);
    for (i = hash & g->mask;; i = (i + 1u) & g->mask) {
      fh_cache_entry *seen = 0;
      if (__atomic_compare_exchange_n(&g->slots[i], &seen, mine, 0, __ATOMIC_RELEASE, __ATOMIC_ACQUIRE)) {
        ++g_worker[worker].puts;
        return 1;
      }
      if (seen->hash == hash && seen->key_length == key_length && memcmp(seen->bytes, key, key_length) == 0) {
        free(mine); /* a racing put of the same key won */
        ++g_worker[worker].dropped;
        return 0;
      }
    }
  }
  ++g_worker[worker].dropped;
  return 0;
}

void fh_cache_stats_read(fh_cache_stats *stats) {
  uint32_t w;
  memset(stats, 0, sizeof(*stats));
  for (w = 0u; w < g_workers; ++w) {
    stats->hits += g_worker[w].hits;
    stats->misses += g_worker[w].misses;
    stats->puts += g_worker[w].puts;
    stats->dropped += g_worker[w].dropped;
  }
  stats->rollovers = __atomic_load_n(&g_rollovers, __ATOMIC_RELAXED);
  stats->generations_freed = __atomic_load_n(&g_freed, __ATOMIC_RELAXED);
}

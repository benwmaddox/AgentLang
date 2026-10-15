/* Process-wide, lock-free fragment cache for the portable Flow HTTP host
 * (cache.get / cache.put). See flow_http_cache.c for the design and the
 * memory-ordering argument. */
#ifndef FLOW_HTTP_CACHE_H
#define FLOW_HTTP_CACHE_H

#include <stdint.h>

#define FH_CACHE_MAX_WORKERS 256u

typedef struct fh_cache_stats {
  uint64_t hits, misses, puts, dropped, rollovers, generations_freed;
} fh_cache_stats;

/* Called once before any worker starts. budget_bytes 0 disables the cache:
 * every get misses and every put is ignored. Returns 0 on success. */
int fh_cache_init(uint64_t budget_bytes, uint32_t workers);
/* Called once after every worker has stopped; frees every generation. */
void fh_cache_shutdown(void);
int fh_cache_enabled(void);

/* Bracket every request (and worker-init) of worker `worker`: the cache may
 * only be read between enter and leave. leave also frees retired generations
 * no worker can still reference. */
void fh_cache_enter(uint32_t worker);
void fh_cache_leave(uint32_t worker);

/* Looks up key; on a hit *value points at immutable bytes that stay valid
 * until this worker's fh_cache_leave. Returns 1 on a hit, 0 on a miss. */
int fh_cache_get(uint32_t worker, const uint8_t *key, uint32_t key_length,
                 const uint8_t **value, uint32_t *value_length);
/* Inserts key -> value unless the key is present (first writer wins; a
 * racing or later put of the same key is dropped). Returns 1 when inserted. */
int fh_cache_put(uint32_t worker, const uint8_t *key, uint32_t key_length,
                 const uint8_t *value, uint32_t value_length);

/* Sums the per-worker counters (call after the workers have stopped). */
void fh_cache_stats_read(fh_cache_stats *stats);

#endif

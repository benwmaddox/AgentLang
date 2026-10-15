/* Portable Flow HTTP host (docs/LINUX-SERVER-PLAN.md pieces A and C).
 *
 * Owning layout value encoding (ABI 5) used at the host boundary, written and
 * read sequentially: Int/Bool/enum ordinal = 8 bytes; String/Bytes = u32
 * length, u32 zero, data, zero pad to 8; List = u32 count, u32 zero, items;
 * Option = i64 tag (0 some, 1 none) [+ payload]; Result = i64 tag (0 ok,
 * 1 error) + payload; record = fields in declaration order. */
#ifndef FLOW_HTTP_H
#define FLOW_HTTP_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "owning_stack_runtime.h"

typedef struct fh_writer {
  uint8_t *data;
  uint32_t capacity;
  uint32_t used;
  int failed;
} fh_writer;

static inline uint32_t fh_pad8(uint32_t value) { return (value + 7u) & ~7u; }

static inline void fh_put_i64(fh_writer *w, int64_t value) {
  if (w->failed || w->capacity - w->used < 8u) { w->failed = 1; return; }
  memcpy(w->data + w->used, &value, 8);
  w->used += 8u;
}

/* A String or Bytes value. */
static inline void fh_put_bytes(fh_writer *w, const void *data, uint32_t length) {
  uint32_t extent = fh_pad8(8u + length);
  if (w->failed || length > 0x7fffffffu || w->capacity - w->used < extent) { w->failed = 1; return; }
  memset(w->data + w->used, 0, extent);
  memcpy(w->data + w->used, &length, 4);
  if (length > 0u) memcpy(w->data + w->used + 8u, data, length);
  w->used += extent;
}

/* List header; returns the offset of the count for later patching. */
static inline uint32_t fh_put_list_header(fh_writer *w, uint32_t count) {
  uint32_t at = w->used;
  fh_put_i64(w, (int64_t)count);
  return at;
}

static inline void fh_patch_count(fh_writer *w, uint32_t at, uint32_t count) {
  if (!w->failed) memcpy(w->data + at, &count, 4);
}

typedef struct fh_reader {
  const uint8_t *data;
  uint32_t extent;
  uint32_t at;
  int failed;
} fh_reader;

static inline int64_t fh_get_i64(fh_reader *r) {
  int64_t value = 0;
  if (r->failed || r->extent - r->at < 8u) { r->failed = 1; return 0; }
  memcpy(&value, r->data + r->at, 8);
  r->at += 8u;
  return value;
}

static inline const uint8_t *fh_get_bytes(fh_reader *r, uint32_t *length) {
  uint32_t n = 0u, extent;
  const uint8_t *start;
  if (r->failed || r->extent - r->at < 8u) { r->failed = 1; *length = 0u; return 0; }
  memcpy(&n, r->data + r->at, 4);
  extent = fh_pad8(8u + n);
  if (n > 0x7fffffffu || r->extent - r->at < extent) { r->failed = 1; *length = 0u; return 0; }
  start = r->data + r->at + 8u;
  r->at += extent;
  *length = n;
  return start;
}

static inline uint32_t fh_get_count(fh_reader *r) {
  uint32_t n = 0u;
  if (r->failed || r->extent - r->at < 8u) { r->failed = 1; return 0u; }
  memcpy(&n, r->data + r->at, 4);
  r->at += 8u;
  return n;
}

/* Per-worker database context (the host provider context). */
typedef struct fh_db fh_db;

/* Opens a connection with WAL and the Campfire host PRAGMAs. Returns NULL on
 * failure and writes a short reason (never config values) to reason. */
fh_db *fh_db_open(const char *path, char *reason, uint32_t reason_capacity);
void fh_db_close(fh_db *db);
int fh_db_provision(const char *path, char *reason, uint32_t reason_capacity);
uint32_t fh_db_prepared_count(const fh_db *db);

/* The provider context the generated module passes to the adapter: the
 * worker's database connection and its index (the cache epoch slot). */
typedef struct fh_provider {
  fh_db *db;
  uint32_t worker;
} fh_provider;

/* The host adapter the generated module calls for db.query / db.execute /
 * cache.get / cache.put (provider_context is an fh_provider). */
typedef struct fh_host_input {
  const uint8_t *data;
  uint32_t extent_bytes;
  uint32_t type_index;
} fh_host_input;

int32_t al_host_flow_http_v1(void *provider_context, uint32_t operation,
                             const al_owning_layout *layout,
                             const fh_host_input *inputs, uint32_t input_count,
                             uint32_t output_type_index, uint8_t *output,
                             uint32_t output_capacity_bytes,
                             uint32_t *output_extent_bytes);

#endif

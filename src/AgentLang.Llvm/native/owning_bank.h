#ifndef AGENTLANG_OWNING_BYTE_BANK_H
#define AGENTLANG_OWNING_BYTE_BANK_H

#include <stddef.h>
#include <stdint.h>

#include "owning_stack_runtime.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum al_owning_bank_result {
  AL_OWNING_BANK_OK = 0,
  AL_OWNING_BANK_INVALID_ARGUMENT = 1,
  AL_OWNING_BANK_INVALID_STORAGE = 2,
  AL_OWNING_BANK_BUSY = 3,
  AL_OWNING_BANK_NO_TRANSACTION = 4,
  AL_OWNING_BANK_INVALID_VALUE = 5,
  AL_OWNING_BANK_ROOT_CAPACITY = 6,
  AL_OWNING_BANK_BYTE_CAPACITY = 7,
  AL_OWNING_BANK_OVERLAP = 8,
  AL_OWNING_BANK_OVERFLOW = 9
} al_owning_bank_result;

/* Describes one independent, serialized value owned by a bank. Offsets and
 * owner_end_bytes are relative to that bank's byte buffer. The half-open
 * owner range is [offset_bytes, owner_end_bytes). No descriptor points into
 * the source working arena. */
typedef struct al_owning_bank_root {
  uint32_t type_id;
  uint32_t offset_bytes;
  uint32_t extent_bytes;
  uint32_t payload_bytes;
  uint32_t owner_end_bytes;
} al_owning_bank_root;

/* Read-only view. Its pointers are borrowed until the next store mutation. */
typedef struct al_owning_byte_bank {
  const uint8_t *bytes;
  uint32_t byte_capacity;
  uint32_t used_bytes;
  const al_owning_bank_root *roots;
  uint32_t root_capacity;
  uint32_t root_count;
} al_owning_byte_bank;

/* Caller-allocated, single-writer pair. This is storage/publication only; it
 * deliberately contains no mailbox, token, scheduling, or async policy. */
typedef struct al_owning_byte_store {
  uint8_t *storage_bytes[2];
  al_owning_bank_root *storage_roots[2];
  al_owning_byte_bank banks[2];
  uint32_t active_index;
  uint32_t transaction_open;
  uint32_t transaction_ready;
  uint32_t reserved;
  uint64_t last_committed_copy_bytes;
} al_owning_byte_store;

/* Each requested output is an owning-stack value. The scanner validates the
 * complete value through owner_end_bytes before any payload is written. */
typedef struct al_owning_bank_stack_slice {
  uint32_t type_index;
  uint32_t source_offset_bytes;
  uint32_t source_owner_end_bytes;
  uint32_t reserved;
} al_owning_bank_stack_slice;

/* All four backing regions must be caller-owned and pairwise disjoint. Byte
 * buffers are aligned to 8 bytes. Root capacity may exceed the maximum
 * permitted roots for one handler output list. Layout descriptors and their
 * type/field arrays must remain immutable and disjoint from the store, both
 * banks, the source context, and the input slice array for the entire
 * transaction. The staging call checks these ranges before writing metadata. */
al_owning_bank_result al_owning_byte_store_init(
    al_owning_byte_store *store, uint8_t *bank0_bytes,
    uint32_t bank0_byte_capacity, al_owning_bank_root *bank0_roots,
    uint32_t bank0_root_capacity, uint8_t *bank1_bytes,
    uint32_t bank1_byte_capacity, al_owning_bank_root *bank1_roots,
    uint32_t bank1_root_capacity);

/* Begins a transaction in the inactive bank. The active bank stays readable
 * and immutable until commit. Begin/abort reset metadata, not payload bytes. */
al_owning_bank_result al_owning_byte_store_begin(
    al_owning_byte_store *store);

/* Prevalidates the entire output list, capacity totals, and source/destination
 * disjointness before copying any payload. Inputs must be standalone owning
 * values accepted by al_owning_measure_value. A zero-width nested field has no
 * independent extent and cannot be published directly; its caller must use
 * the backend's canonical standalone representation where one exists. A
 * successful call stages exactly one complete root list. Any later failure
 * leaves the active bank unchanged; abort discards staging metadata. */
al_owning_bank_result al_owning_byte_store_stage_stack_values(
    al_owning_byte_store *store, al_owning_stack_context *context,
    const al_owning_layout *layout,
    const al_owning_bank_stack_slice *slices, uint32_t slice_count,
    uint32_t error_id);

/* Atomic metadata publication: changes the active index only after a complete
 * staged root list exists. */
al_owning_bank_result al_owning_byte_store_commit(
    al_owning_byte_store *store);

/* Discards the inactive transaction without changing the active bytes/roots.
 * It is safe to call after a failed preflight or copy. */
al_owning_bank_result al_owning_byte_store_abort(
    al_owning_byte_store *store);

const al_owning_byte_bank *al_owning_byte_store_active(
    const al_owning_byte_store *store);

/* Bytes copied by the last successful commit, including headers and padding.
 * This is publication traffic, not unique live payload. */
uint64_t al_owning_byte_store_last_committed_copy_bytes(
    const al_owning_byte_store *store);

#ifdef __cplusplus
}
#define AL_OWNING_BANK_ASSERT(condition, message) static_assert((condition), message)
#else
#define AL_OWNING_BANK_ASSERT(condition, message) _Static_assert((condition), message)
#endif

AL_OWNING_BANK_ASSERT(sizeof(al_owning_bank_stack_slice) == 16,
                      "bank stack slice size");
AL_OWNING_BANK_ASSERT(offsetof(al_owning_bank_stack_slice, type_index) == 0,
                      "bank slice type index offset");
AL_OWNING_BANK_ASSERT(
    offsetof(al_owning_bank_stack_slice, source_offset_bytes) == 4,
    "bank slice source offset");
AL_OWNING_BANK_ASSERT(
    offsetof(al_owning_bank_stack_slice, source_owner_end_bytes) == 8,
    "bank slice owner end");
AL_OWNING_BANK_ASSERT(offsetof(al_owning_bank_stack_slice, reserved) == 12,
                      "bank slice reserved offset");
AL_OWNING_BANK_ASSERT(sizeof(al_owning_bank_root) == 20,
                      "bank root descriptor size");

#undef AL_OWNING_BANK_ASSERT

#endif

#ifndef AGENTLANG_OWNING_MAILBOX_ABI_H
#define AGENTLANG_OWNING_MAILBOX_ABI_H

#include <stddef.h>
#include <stdint.h>

#include "owning_stack_runtime.h"

#ifdef __cplusplus
extern "C" {
#endif

#if defined(AL_OWNING_MAILBOX_BUILD) && defined(_WIN32)
#define AL_OWNING_MAILBOX_EXPORT __declspec(dllexport)
#else
#define AL_OWNING_MAILBOX_EXPORT
#endif

enum {
  AL_OWNING_MAILBOX_ABI_VERSION = 1u,
  AL_OWNING_MAILBOX_ENTRY_COUNT = 3u,
  AL_OWNING_MAILBOX_MAX_INPUTS = 3u,
  AL_OWNING_MAILBOX_MAX_OUTPUTS = 2u,
  /* The controller owns the complete output-slice definition. Keep its
   * descriptor span available to a generated module without importing the
   * bank's storage implementation. */
  AL_OWNING_MAILBOX_OUTPUT_SLICE_BYTES = 16u
};

/* Defined by the owning bank contract. A module returns only source offsets
 * and an owner boundary into its live working arena; it never returns a byte
 * pointer or publishes the retained bank itself. */
typedef struct al_owning_bank_stack_slice al_owning_bank_stack_slice;

/* One complete serialized input value. bytes and extent_bytes remain borrowed
 * for the duration of execute; type_index indexes the module's shared layout. */
typedef struct al_owning_external_slice {
  const uint8_t *bytes;
  uint32_t extent_bytes;
  uint32_t type_index;
} al_owning_external_slice;

/* Entry order is fixed: initialize, begin, resume. The controller validates
 * exact counts and type indexes before dispatching; generated callbacks repeat
 * these checks before importing caller-owned slices. Output descriptors are
 * valid only on success: preflight failures leave them untouched, while the
 * callback initializes requested descriptors invalid after preflight and keeps
 * them invalid if later execution fails. */
typedef int32_t (*al_owning_mailbox_execute_fn)(
    al_owning_stack_context *context,
    const al_owning_external_slice *inputs, uint32_t input_count,
    al_owning_bank_stack_slice *outputs, uint32_t output_capacity);

typedef struct al_owning_mailbox_entry {
  uint32_t input_count;
  uint32_t output_count;
  uint32_t input_type_indexes[AL_OWNING_MAILBOX_MAX_INPUTS];
  uint32_t output_type_indexes[AL_OWNING_MAILBOX_MAX_OUTPUTS];
  al_owning_mailbox_execute_fn execute;
} al_owning_mailbox_entry;

/* Immutable module-owned metadata. All three callbacks use the same layout and
 * deterministic type-index universe. The module lifetime must cover every
 * controller that calls an entry. */
typedef struct al_owning_mailbox_module {
  uint32_t abi_version;
  uint32_t struct_size;
  const al_owning_layout *layout;
  al_owning_mailbox_entry entries[AL_OWNING_MAILBOX_ENTRY_COUNT];
} al_owning_mailbox_module;

typedef const al_owning_mailbox_module *(*al_owning_mailbox_module_fn)(void);

/* Required generated-module export. */
AL_OWNING_MAILBOX_EXPORT const al_owning_mailbox_module *
agentlang_owning_mailbox_module(void);

#if defined(__cplusplus)
#define AL_OWNING_MAILBOX_ASSERT(condition, message)                           \
  static_assert((condition), message)
#else
#define AL_OWNING_MAILBOX_ASSERT(condition, message)                           \
  _Static_assert((condition), message)
#endif

AL_OWNING_MAILBOX_ASSERT(sizeof(void *) == 8,
                         "owning mailbox ABI v1 requires x64 pointers");
AL_OWNING_MAILBOX_ASSERT(sizeof(al_owning_external_slice) == 16,
                         "external slice size");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_external_slice, bytes) == 0,
                         "external slice pointer offset");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_external_slice, extent_bytes) == 8,
                         "external slice extent offset");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_external_slice, type_index) == 12,
                         "external slice type-index offset");
AL_OWNING_MAILBOX_ASSERT(sizeof(al_owning_mailbox_entry) == 40,
                         "mailbox entry size");
AL_OWNING_MAILBOX_ASSERT(
    offsetof(al_owning_mailbox_entry, input_type_indexes) == 8,
    "mailbox input indexes offset");
AL_OWNING_MAILBOX_ASSERT(
    offsetof(al_owning_mailbox_entry, output_type_indexes) == 20,
    "mailbox output indexes offset");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_mailbox_entry, execute) == 32,
                         "mailbox callback offset");
AL_OWNING_MAILBOX_ASSERT(sizeof(al_owning_mailbox_module) == 136,
                         "mailbox module descriptor size");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_mailbox_module, layout) == 8,
                         "mailbox layout pointer offset");
AL_OWNING_MAILBOX_ASSERT(offsetof(al_owning_mailbox_module, entries) == 16,
                         "mailbox entries offset");

#undef AL_OWNING_MAILBOX_ASSERT

#ifdef __cplusplus
}
#endif

#endif

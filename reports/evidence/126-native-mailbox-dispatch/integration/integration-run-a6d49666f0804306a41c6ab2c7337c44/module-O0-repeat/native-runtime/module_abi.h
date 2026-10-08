#ifndef AGENTLANG_MODULE_ABI_H
#define AGENTLANG_MODULE_ABI_H

#include "arena_runtime.h"

#ifdef __cplusplus
extern "C" {
#endif

enum { AL_MODULE_ABI_VERSION = 1u, AL_MODULE_FINGERPRINT_BYTES = 32u };

typedef void (*al_entry_execute_fn)(al_runtime_context *context,
                                    int64_t *outputs, uint32_t output_capacity,
                                    int32_t *status);

/* IDs match the generated entry's diagnostic metadata IDs. Empty strings mean
 * absent optional word/source information. All memory is immutable and belongs
 * to the loaded module, whose lifetime must cover every controller using it. */
typedef struct al_module_diagnostic {
  int32_t id;
  uint32_t line;
  uint32_t column;
  uint32_t length;
  const char *code;
  const char *word;
  const char *file;
} al_module_diagnostic;

typedef struct al_module_entry {
  uint32_t struct_size;
  uint32_t entry_id;
  const char *name;
  al_entry_execute_fn execute;
  uint32_t input_count;
  uint32_t output_count;
  uint32_t workspace_capacity;
  uint32_t diagnostic_count;
  const uint32_t *input_type_ids;
  const uint32_t *output_type_ids;
  const al_module_diagnostic *diagnostics;
} al_module_entry;

/* Entry IDs are deterministic table indices. Every entry uses the same verified
 * program/type-ID universe. The fingerprint covers deterministic entry IR and
 * metadata, excluding optimization and output-directory paths. Type names have
 * program->type_count elements. They preserve nominal names for inspection. */
typedef struct al_module_desc {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t runtime_abi_version;
  uint32_t entry_count;
  uint8_t fingerprint[AL_MODULE_FINGERPRINT_BYTES];
  const al_program_desc *program;
  const al_module_entry *entries;
  const char *const *type_names;
} al_module_desc;

typedef const al_module_desc *(*al_module_descriptor_fn)(void);

/* The required module export. Native callers obtain indexed entry function
 * pointers and their typed descriptors from this table. */
const al_module_desc *agentlang_module_descriptor(void);

#if defined(__cplusplus)
#define AL_MODULE_ASSERT(condition, message) static_assert((condition), message)
#else
#define AL_MODULE_ASSERT(condition, message)                                   \
  _Static_assert((condition), message)
#endif

AL_MODULE_ASSERT(sizeof(void *) == 8, "module ABI v1 requires x64 pointers");
AL_MODULE_ASSERT(sizeof(al_module_diagnostic) == 40, "diagnostic size");
AL_MODULE_ASSERT(offsetof(al_module_diagnostic, code) == 16,
                 "diagnostic code offset");
AL_MODULE_ASSERT(offsetof(al_module_diagnostic, word) == 24,
                 "diagnostic word offset");
AL_MODULE_ASSERT(offsetof(al_module_diagnostic, file) == 32,
                 "diagnostic file offset");
AL_MODULE_ASSERT(sizeof(al_module_entry) == 64, "entry size");
AL_MODULE_ASSERT(offsetof(al_module_entry, name) == 8, "entry name offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, execute) == 16,
                 "entry function offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, input_count) == 24,
                 "entry inputs offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, output_count) == 28,
                 "entry outputs offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, workspace_capacity) == 32,
                 "entry workspace offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, diagnostic_count) == 36,
                 "entry diagnostics count offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, input_type_ids) == 40,
                 "entry input types offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, output_type_ids) == 48,
                 "entry output types offset");
AL_MODULE_ASSERT(offsetof(al_module_entry, diagnostics) == 56,
                 "entry diagnostics offset");
AL_MODULE_ASSERT(sizeof(al_module_desc) == 72, "module descriptor size");
AL_MODULE_ASSERT(offsetof(al_module_desc, fingerprint) == 16,
                 "fingerprint offset");
AL_MODULE_ASSERT(offsetof(al_module_desc, program) == 48, "program offset");
AL_MODULE_ASSERT(offsetof(al_module_desc, entries) == 56, "entries offset");
AL_MODULE_ASSERT(offsetof(al_module_desc, type_names) == 64,
                 "type names offset");

#undef AL_MODULE_ASSERT
#ifdef __cplusplus
}
#endif
#endif

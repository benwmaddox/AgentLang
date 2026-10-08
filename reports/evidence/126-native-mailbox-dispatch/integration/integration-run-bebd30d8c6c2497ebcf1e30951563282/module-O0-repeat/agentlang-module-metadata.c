#include <stddef.h>
#include <stdint.h>
#include "module_abi.h"

extern void agentlang_entry_000_execute(al_runtime_context *ctx, int64_t *outputs, uint32_t capacity, int32_t *status);
extern void agentlang_entry_001_execute(al_runtime_context *ctx, int64_t *outputs, uint32_t capacity, int32_t *status);
extern void agentlang_entry_002_execute(al_runtime_context *ctx, int64_t *outputs, uint32_t capacity, int32_t *status);

static const uint32_t module_type_fields_3[] = { 0u };
static const uint32_t module_type_fields_4[] = { 0u };
static const al_type_desc module_types[5] = {
  { 1u, 0u, NULL },
  { 2u, 0u, NULL },
  { 3u, 0u, NULL },
  { 4u, 1u, module_type_fields_3 },
  { 4u, 1u, module_type_fields_4 },
};
static const al_program_desc module_program = { module_types, 5u, 0u };

static const char *const module_type_names[5] = {
  "Int",
  "Bool",
  "Unit",
  "Continuation",
  "State",
};

static const uint32_t module_entry_000_input_type_ids[2] = { 4u, 0u };
static const uint32_t module_entry_000_output_type_ids[2] = { 4u, 3u };
static const al_module_diagnostic module_entry_000_diagnostics[13] = {
  { 0, 1u, 1u, 1u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "<mailbox.begin>" },
  { 1, 16u, 1u, 259u, "RUNTIME_CALL_DEPTH", "mailbox.begin", "mailbox.flow" },
  { 2, 16u, 32u, 10u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 3, 16u, 18u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 4, 20u, 24u, 32u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 5, 20u, 50u, 5u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 6, 20u, 42u, 13u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 7, 20u, 24u, 32u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 8, 20u, 24u, 32u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 9, 1u, 1u, 1u, "RUNTIME_CALL_DEPTH", "continuation.new", "<generated-native-dispatch-record>" },
  { 10, 20u, 5u, 51u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 11, 21u, 13u, 5u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
  { 12, 21u, 20u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.begin", "mailbox.flow" },
};

static const uint32_t module_entry_001_input_type_ids[1] = { 0u };
static const uint32_t module_entry_001_output_type_ids[1] = { 4u };
static const al_module_diagnostic module_entry_001_diagnostics[9] = {
  { 0, 1u, 1u, 1u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "<mailbox.initialize>" },
  { 1, 9u, 1u, 153u, "RUNTIME_CALL_DEPTH", "mailbox.initialize", "mailbox.flow" },
  { 2, 9u, 23u, 9u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 3, 13u, 5u, 24u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 4, 13u, 24u, 4u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 5, 13u, 16u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 6, 13u, 5u, 24u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 7, 13u, 5u, 24u, "RUNTIME_STEP_LIMIT", "mailbox.initialize", "mailbox.flow" },
  { 8, 1u, 1u, 1u, "RUNTIME_CALL_DEPTH", "state.new", "<generated-native-dispatch-record>" },
};

static const uint32_t module_entry_002_input_type_ids[3] = { 4u, 3u, 0u };
static const uint32_t module_entry_002_output_type_ids[1] = { 4u };
static const al_module_diagnostic module_entry_002_diagnostics[28] = {
  { 0, 1u, 1u, 1u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "<mailbox.resume>" },
  { 1, 24u, 1u, 298u, "RUNTIME_CALL_DEPTH", "mailbox.resume", "mailbox.flow" },
  { 2, 24u, 61u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 3, 24u, 33u, 26u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 4, 24u, 19u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 5, 28u, 25u, 12u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 6, 28u, 25u, 18u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 7, 1u, 1u, 1u, "RUNTIME_CALL_DEPTH", "continuation.delta", "<generated-native-dispatch-record>" },
  { 8, 28u, 45u, 7u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 9, 28u, 18u, 35u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 10, 1u, 1u, 6u, "RUNTIME_CALL_DEPTH", "divide", "<standard>" },
  { 11, 0u, 0u, 0u, "RUNTIME_DIVIDE_BY_ZERO", "divide", "" },
  { 12, 0u, 0u, 0u, "RUNTIME_OVERFLOW", "divide", "" },
  { 13, 28u, 5u, 48u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 14, 29u, 21u, 5u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 15, 29u, 21u, 11u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 16, 1u, 1u, 1u, "RUNTIME_CALL_DEPTH", "state.total", "<generated-native-dispatch-record>" },
  { 17, 29u, 34u, 6u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 18, 29u, 17u, 24u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 19, 1u, 1u, 3u, "RUNTIME_CALL_DEPTH", "add", "<standard>" },
  { 20, 1u, 1u, 3u, "RUNTIME_OVERFLOW", "add", "<standard>" },
  { 21, 29u, 5u, 36u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 22, 30u, 5u, 25u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 23, 30u, 24u, 5u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 24, 30u, 16u, 13u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 25, 30u, 5u, 25u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 26, 30u, 5u, 25u, "RUNTIME_STEP_LIMIT", "mailbox.resume", "mailbox.flow" },
  { 27, 1u, 1u, 1u, "RUNTIME_CALL_DEPTH", "state.new", "<generated-native-dispatch-record>" },
};

static const al_module_entry module_entries[3] = {
  { sizeof(al_module_entry), 0u, "mailbox.begin", agentlang_entry_000_execute, 2u, 2u, 2u, 13u, module_entry_000_input_type_ids, module_entry_000_output_type_ids, module_entry_000_diagnostics },
  { sizeof(al_module_entry), 1u, "mailbox.initialize", agentlang_entry_001_execute, 1u, 1u, 2u, 9u, module_entry_001_input_type_ids, module_entry_001_output_type_ids, module_entry_001_diagnostics },
  { sizeof(al_module_entry), 2u, "mailbox.resume", agentlang_entry_002_execute, 3u, 1u, 3u, 28u, module_entry_002_input_type_ids, module_entry_002_output_type_ids, module_entry_002_diagnostics },
};

static const al_module_desc module_descriptor = {
  AL_MODULE_ABI_VERSION,
  sizeof(al_module_desc),
  AL_RUNTIME_ABI_VERSION,
  3u,
  { 0x42u, 0xCBu, 0x81u, 0x20u, 0x13u, 0x1Du, 0x08u, 0x01u, 0x98u, 0x58u, 0x2Bu, 0x79u, 0xBCu, 0x1Fu, 0x7Eu, 0x48u, 0xD1u, 0x33u, 0x6Au, 0x4Fu, 0x99u, 0xD7u, 0x28u, 0x77u, 0x77u, 0xDDu, 0x8Eu, 0xF6u, 0x90u, 0xDCu, 0x4Fu, 0x05u },
  &module_program,
  module_entries,
  module_type_names
};

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdll-attribute-on-redeclaration"
__declspec(dllexport) const al_module_desc *agentlang_module_descriptor(void) {
  return &module_descriptor;
}
#pragma clang diagnostic pop

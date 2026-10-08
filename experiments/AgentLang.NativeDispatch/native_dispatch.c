#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "mailbox_runtime.h"

typedef const al_module_desc *(*module_descriptor_fn)(void);

typedef struct check_record {
  const char *name;
  int passed;
} check_record;

typedef struct foreign_call {
  al_mailbox_runtime *runtime;
  al_mailbox_token token;
  al_mailbox_result result;
  al_mailbox_call_info info;
} foreign_call;

static check_record checks[96];
static size_t check_count;

static void record_check(const char *name, int passed) {
  if (check_count < sizeof(checks) / sizeof(checks[0])) {
    checks[check_count].name = name;
    checks[check_count].passed = passed != 0;
    ++check_count;
  }
}

#define CHECK(name, condition) record_check((name), (condition))

static uint32_t find_entry_id(const al_module_desc *module, const char *name) {
  uint32_t index;
  if (module == NULL || module->entries == NULL || name == NULL) {
    return UINT32_MAX;
  }
  for (index = 0u; index < module->entry_count; ++index) {
    const al_module_entry *entry = &module->entries[index];
    if (entry->name != NULL && strcmp(entry->name, name) == 0 &&
        entry->entry_id == index) {
      return index;
    }
  }
  return UINT32_MAX;
}

static uint32_t find_type_id(const al_module_desc *module, const char *name) {
  uint32_t index;
  if (module == NULL || module->program == NULL || module->type_names == NULL) {
    return UINT32_MAX;
  }
  for (index = 0u; index < module->program->type_count; ++index) {
    const char *candidate = module->type_names[index];
    if (candidate != NULL && strcmp(candidate, name) == 0) {
      return index;
    }
  }
  return UINT32_MAX;
}

static int signature_matches(const al_module_desc *module, uint32_t entry_id,
                             const char *const *inputs, uint32_t input_count,
                             const char *const *outputs,
                             uint32_t output_count) {
  uint32_t index;
  const al_module_entry *entry;
  if (module == NULL || entry_id >= module->entry_count) {
    return 0;
  }
  entry = &module->entries[entry_id];
  if (entry->input_count != input_count ||
      entry->output_count != output_count ||
      (input_count != 0u && entry->input_type_ids == NULL) ||
      (output_count != 0u && entry->output_type_ids == NULL)) {
    return 0;
  }
  for (index = 0u; index < input_count; ++index) {
    uint32_t expected = find_type_id(module, inputs[index]);
    if (expected == UINT32_MAX || entry->input_type_ids[index] != expected) {
      return 0;
    }
  }
  for (index = 0u; index < output_count; ++index) {
    uint32_t expected = find_type_id(module, outputs[index]);
    if (expected == UINT32_MAX || entry->output_type_ids[index] != expected) {
      return 0;
    }
  }
  return 1;
}

static const char *find_diagnostic_code(const al_module_desc *module,
                                        uint32_t entry_id, int32_t id) {
  uint32_t index;
  const al_module_entry *entry;
  if (module == NULL || entry_id >= module->entry_count || id < 0) {
    return "";
  }
  entry = &module->entries[entry_id];
  for (index = 0u; index < entry->diagnostic_count; ++index) {
    if (entry->diagnostics[index].id == id) {
      return entry->diagnostics[index].code;
    }
  }
  return "";
}

static void init_call_info(al_mailbox_call_info *info) {
  memset(info, 0, sizeof(*info));
  info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  info->struct_size = (uint32_t)sizeof(*info);
  info->error_metadata_id = -1;
}

static void init_stats(al_mailbox_runtime_stats *stats) {
  memset(stats, 0, sizeof(*stats));
  stats->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  stats->struct_size = (uint32_t)sizeof(*stats);
}

static int get_stats(al_mailbox_runtime *runtime,
                     al_mailbox_runtime_stats *stats) {
  init_stats(stats);
  return al_mailbox_get_stats(runtime, stats) == AL_MAILBOX_OK;
}

static int scratch_leases_balanced(al_mailbox_runtime *runtime) {
  al_mailbox_runtime_stats stats;
  return get_stats(runtime, &stats) && stats.outstanding_scratch_leases == 0u &&
         stats.scratch_lease_acquisitions == stats.scratch_lease_returns;
}

static int read_record_field(const al_mailbox_state_view *view,
                             const al_program_desc *program,
                             uint32_t root_index, uint32_t record_type_id,
                             uint32_t field_index, int64_t *value) {
  al_arena scratch_view;
  al_arena empty_retained;
  al_runtime_context context;
  int64_t workspace[1] = {0};
  _Alignas(8) uint8_t retained_data[8] = {0};
  al_node retained_nodes[1];
  uint32_t empty_generation;

  if (view == NULL || program == NULL || value == NULL || view->owner == NULL ||
      view->roots == NULL || view->root_type_ids == NULL ||
      root_index >= view->root_count ||
      view->root_type_ids[root_index] != record_type_id) {
    return 0;
  }

  scratch_view = *view->owner;
  memset(&empty_retained, 0, sizeof(empty_retained));
  memset(retained_nodes, 0, sizeof(retained_nodes));
  empty_generation = scratch_view.generation == 1u ? 2u : 1u;
  empty_retained.data = retained_data;
  empty_retained.byte_capacity = (uint32_t)sizeof(retained_data);
  empty_retained.nodes = retained_nodes;
  empty_retained.node_capacity = (uint32_t)(sizeof(retained_nodes) /
                                            sizeof(retained_nodes[0]));
  empty_retained.generation = empty_generation;

  memset(&context, 0, sizeof(context));
  context.abi_version = AL_RUNTIME_ABI_VERSION;
  context.error_metadata_id = -1;
  context.scratch = &scratch_view;
  context.retained = &empty_retained;
  context.workspace = workspace;
  context.workspace_capacity = (uint32_t)(sizeof(workspace) /
                                           sizeof(workspace[0]));
  if (al_runtime_get_field(&context, program, record_type_id,
                           view->roots[root_index], field_index,
                           &workspace[0]) != AL_RUNTIME_OK) {
    return 0;
  }
  *value = workspace[0];
  return 1;
}

static int get_state_view(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                          al_mailbox_state_view *view) {
  memset(view, 0, sizeof(*view));
  view->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  view->struct_size = (uint32_t)sizeof(*view);
  return al_mailbox_get_state_view(runtime, mailbox_id, view) == AL_MAILBOX_OK;
}

static int read_state_total(al_mailbox_runtime *runtime,
                            const al_module_desc *module, uint32_t mailbox_id,
                            int expected_pending, int64_t *state_total,
                            int64_t *continuation_delta) {
  al_mailbox_state_view view;
  uint32_t state_type_id = find_type_id(module, "State");
  uint32_t continuation_type_id = find_type_id(module, "Continuation");
  if (state_type_id == UINT32_MAX || continuation_type_id == UINT32_MAX ||
      !get_state_view(runtime, mailbox_id, &view) ||
      view.pending != (uint32_t)(expected_pending != 0) ||
      view.root_count != (expected_pending ? 2u : 1u) || view.owner == NULL ||
      view.state_type_id != state_type_id ||
      view.owner->used != (expected_pending ? 16u : 8u) ||
      view.owner->node_count != (expected_pending ? 2u : 1u) ||
      !read_record_field(&view, module->program, 0u, state_type_id, 0u,
                         state_total)) {
    return 0;
  }
  if (expected_pending &&
      (continuation_delta == NULL ||
       !read_record_field(&view, module->program, 1u, continuation_type_id, 0u,
                          continuation_delta))) {
    return 0;
  }
  return 1;
}

static void sample_live_maximum(al_mailbox_runtime *runtime,
                                uint64_t *maximum_bytes,
                                uint64_t *maximum_nodes) {
  al_mailbox_runtime_stats stats;
  if (get_stats(runtime, &stats)) {
    if (stats.live_retained_bytes > *maximum_bytes) {
      *maximum_bytes = stats.live_retained_bytes;
    }
    if (stats.live_retained_nodes > *maximum_nodes) {
      *maximum_nodes = stats.live_retained_nodes;
    }
  }
}

static DWORD WINAPI foreign_thread_entry(void *opaque) {
  foreign_call *call = (foreign_call *)opaque;
  init_call_info(&call->info);
  call->result = al_mailbox_resume(call->runtime, 0u, &call->token, 1,
                                   NULL, &call->info);
  return 0u;
}

static void print_abi(void) {
  printf("\"abi\":{");
  printf("\"controlAbiVersion\":%u,", (unsigned)AL_MAILBOX_CONTROL_ABI_VERSION);
  printf("\"moduleAbiVersion\":%u,", (unsigned)AL_MODULE_ABI_VERSION);
  printf("\"executionAbiVersion\":%u,",
         (unsigned)AL_RUNTIME_ABI_VERSION);
  printf("\"target\":\"Windows x64\",");
  printf("\"pointerBytes\":%zu,", sizeof(void *));
  printf("\"diagnostic\":{\"size\":%zu,\"code\":%zu,\"word\":%zu,\"file\":%zu},",
         sizeof(al_module_diagnostic), offsetof(al_module_diagnostic, code),
         offsetof(al_module_diagnostic, word),
         offsetof(al_module_diagnostic, file));
  printf("\"entry\":{\"size\":%zu,\"name\":%zu,\"execute\":%zu,\"inputCount\":%zu,\"outputCount\":%zu,\"workspaceCapacity\":%zu,\"diagnosticCount\":%zu,\"inputTypeIds\":%zu,\"outputTypeIds\":%zu,\"diagnostics\":%zu},",
         sizeof(al_module_entry), offsetof(al_module_entry, name),
         offsetof(al_module_entry, execute),
         offsetof(al_module_entry, input_count),
         offsetof(al_module_entry, output_count),
         offsetof(al_module_entry, workspace_capacity),
         offsetof(al_module_entry, diagnostic_count),
         offsetof(al_module_entry, input_type_ids),
         offsetof(al_module_entry, output_type_ids),
         offsetof(al_module_entry, diagnostics));
  printf("\"module\":{\"size\":%zu,\"fingerprint\":%zu,\"program\":%zu,\"entries\":%zu,\"typeNames\":%zu},",
         sizeof(al_module_desc), offsetof(al_module_desc, fingerprint),
         offsetof(al_module_desc, program), offsetof(al_module_desc, entries),
         offsetof(al_module_desc, type_names));
  printf("\"config\":{\"size\":%zu,\"controlAbiVersion\":%zu,\"structSize\":%zu,\"mailboxCapacity\":%zu,\"scratchByteCapacity\":%zu,\"scratchNodeCapacity\":%zu,\"retainedByteCapacity\":%zu,\"retainedNodeCapacity\":%zu,\"initEntryId\":%zu,\"beginEntryId\":%zu,\"resumeEntryId\":%zu,\"reserved\":%zu},",
         sizeof(al_mailbox_config),
         offsetof(al_mailbox_config, control_abi_version),
         offsetof(al_mailbox_config, struct_size),
         offsetof(al_mailbox_config, mailbox_capacity),
         offsetof(al_mailbox_config, scratch_byte_capacity),
         offsetof(al_mailbox_config, scratch_node_capacity),
         offsetof(al_mailbox_config, retained_byte_capacity),
         offsetof(al_mailbox_config, retained_node_capacity),
         offsetof(al_mailbox_config, init_entry_id),
         offsetof(al_mailbox_config, begin_entry_id),
         offsetof(al_mailbox_config, resume_entry_id),
         offsetof(al_mailbox_config, reserved));
  printf("\"callLimits\":{\"size\":%zu,\"controlAbiVersion\":%zu,\"structSize\":%zu,\"retainedByteLimit\":%zu,\"retainedNodeLimit\":%zu},",
         sizeof(al_mailbox_call_limits),
         offsetof(al_mailbox_call_limits, control_abi_version),
         offsetof(al_mailbox_call_limits, struct_size),
         offsetof(al_mailbox_call_limits, retained_byte_limit),
         offsetof(al_mailbox_call_limits, retained_node_limit));
  printf("\"storageRequirements\":{\"size\":%zu,\"storageAlignment\":%zu,\"workspaceCapacity\":%zu,\"storageBytes\":%zu,\"retainedReservedBytes\":%zu,\"scratchReservedBytes\":%zu,\"workspaceReservedBytes\":%zu,\"controllerReservedBytes\":%zu},",
         sizeof(al_mailbox_storage_requirements),
         offsetof(al_mailbox_storage_requirements, storage_alignment),
         offsetof(al_mailbox_storage_requirements, workspace_capacity),
         offsetof(al_mailbox_storage_requirements, storage_bytes),
         offsetof(al_mailbox_storage_requirements, retained_reserved_bytes),
         offsetof(al_mailbox_storage_requirements, scratch_reserved_bytes),
         offsetof(al_mailbox_storage_requirements, workspace_reserved_bytes),
         offsetof(al_mailbox_storage_requirements, controller_reserved_bytes));
  printf("\"callInfo\":{\"size\":%zu,\"controlAbiVersion\":%zu,\"structSize\":%zu,\"handlerStatus\":%zu,\"errorMetadataId\":%zu,\"errorArgument0\":%zu,\"errorArgument1\":%zu,\"stepsConsumed\":%zu,\"reserved\":%zu},",
         sizeof(al_mailbox_call_info),
         offsetof(al_mailbox_call_info, control_abi_version),
         offsetof(al_mailbox_call_info, struct_size),
         offsetof(al_mailbox_call_info, handler_status),
         offsetof(al_mailbox_call_info, error_metadata_id),
         offsetof(al_mailbox_call_info, error_argument0),
         offsetof(al_mailbox_call_info, error_argument1),
         offsetof(al_mailbox_call_info, steps_consumed),
         offsetof(al_mailbox_call_info, reserved));
  printf("\"token\":{\"size\":%zu,\"alignment\":%zu,\"opaque\":%zu},",
         sizeof(al_mailbox_token), _Alignof(al_mailbox_token),
         offsetof(al_mailbox_token, opaque));
  printf("\"stats\":{\"size\":%zu,\"mailboxCapacity\":%zu,\"initializedMailboxes\":%zu,\"pendingMailboxes\":%zu,\"workspaceCapacity\":%zu,\"storageReservedBytes\":%zu,\"retainedReservedBytes\":%zu,\"scratchReservedBytes\":%zu,\"workspaceReservedBytes\":%zu,\"liveRetainedBytes\":%zu,\"liveRetainedNodes\":%zu,\"scratchHighWaterBytes\":%zu,\"scratchHighWaterNodes\":%zu,\"handlerInvocations\":%zu,\"handlerFailures\":%zu,\"scratchLeaseAcquisitions\":%zu,\"scratchLeaseReturns\":%zu,\"outstandingScratchLeases\":%zu,\"reserved\":%zu},",
         sizeof(al_mailbox_runtime_stats),
         offsetof(al_mailbox_runtime_stats, mailbox_capacity),
         offsetof(al_mailbox_runtime_stats, initialized_mailboxes),
         offsetof(al_mailbox_runtime_stats, pending_mailboxes),
         offsetof(al_mailbox_runtime_stats, workspace_capacity),
         offsetof(al_mailbox_runtime_stats, storage_reserved_bytes),
         offsetof(al_mailbox_runtime_stats, retained_reserved_bytes),
         offsetof(al_mailbox_runtime_stats, scratch_reserved_bytes),
         offsetof(al_mailbox_runtime_stats, workspace_reserved_bytes),
         offsetof(al_mailbox_runtime_stats, live_retained_bytes),
         offsetof(al_mailbox_runtime_stats, live_retained_nodes),
         offsetof(al_mailbox_runtime_stats, scratch_high_water_bytes),
         offsetof(al_mailbox_runtime_stats, scratch_high_water_nodes),
         offsetof(al_mailbox_runtime_stats, handler_invocations),
         offsetof(al_mailbox_runtime_stats, handler_failures),
         offsetof(al_mailbox_runtime_stats, scratch_lease_acquisitions),
         offsetof(al_mailbox_runtime_stats, scratch_lease_returns),
         offsetof(al_mailbox_runtime_stats, outstanding_scratch_leases),
         offsetof(al_mailbox_runtime_stats, reserved));
  printf("\"stateView\":{\"size\":%zu,\"owner\":%zu,\"roots\":%zu,\"rootTypeIds\":%zu,\"rootCount\":%zu,\"stateTypeId\":%zu,\"pending\":%zu,\"reserved\":%zu},",
         sizeof(al_mailbox_state_view), offsetof(al_mailbox_state_view, owner),
         offsetof(al_mailbox_state_view, roots),
         offsetof(al_mailbox_state_view, root_type_ids),
         offsetof(al_mailbox_state_view, root_count),
         offsetof(al_mailbox_state_view, state_type_id),
         offsetof(al_mailbox_state_view, pending),
         offsetof(al_mailbox_state_view, reserved));
  printf("\"results\":{\"ok\":%u,\"invalidArgument\":%u,\"invalidModule\":%u,\"invalidStorage\":%u,\"wrongThread\":%u,\"disposed\":%u,\"invalidMailbox\":%u,\"notInitialized\":%u,\"alreadyInitialized\":%u,\"busy\":%u,\"notPending\":%u,\"crossRuntimeToken\":%u,\"wrongOwnerToken\":%u,\"staleToken\":%u,\"duplicateToken\":%u,\"tokenExhausted\":%u,\"generationExhausted\":%u,\"handlerFailure\":%u,\"scratchCapacity\":%u,\"retainedCapacity\":%u,\"invalidReference\":%u,\"sizeOverflow\":%u,\"instanceExhausted\":%u}}",
         (unsigned)AL_MAILBOX_OK,
         (unsigned)AL_MAILBOX_INVALID_ARGUMENT,
         (unsigned)AL_MAILBOX_INVALID_MODULE,
         (unsigned)AL_MAILBOX_INVALID_STORAGE,
         (unsigned)AL_MAILBOX_WRONG_THREAD,
         (unsigned)AL_MAILBOX_DISPOSED,
         (unsigned)AL_MAILBOX_INVALID_MAILBOX,
         (unsigned)AL_MAILBOX_NOT_INITIALIZED,
         (unsigned)AL_MAILBOX_ALREADY_INITIALIZED,
         (unsigned)AL_MAILBOX_BUSY,
         (unsigned)AL_MAILBOX_NOT_PENDING,
         (unsigned)AL_MAILBOX_CROSS_RUNTIME_TOKEN,
         (unsigned)AL_MAILBOX_WRONG_OWNER_TOKEN,
         (unsigned)AL_MAILBOX_STALE_TOKEN,
         (unsigned)AL_MAILBOX_DUPLICATE_TOKEN,
         (unsigned)AL_MAILBOX_TOKEN_EXHAUSTED,
         (unsigned)AL_MAILBOX_GENERATION_EXHAUSTED,
         (unsigned)AL_MAILBOX_HANDLER_FAILURE,
         (unsigned)AL_MAILBOX_SCRATCH_CAPACITY,
         (unsigned)AL_MAILBOX_RETAINED_CAPACITY,
         (unsigned)AL_MAILBOX_INVALID_REFERENCE,
         (unsigned)AL_MAILBOX_SIZE_OVERFLOW,
         (unsigned)AL_MAILBOX_INSTANCE_EXHAUSTED);
}

static void print_checks(void) {
  size_t index;
  printf("\"checks\":[");
  for (index = 0u; index < check_count; ++index) {
    if (index != 0u) {
      putchar(',');
    }
    printf("{\"name\":\"");
    printf("%s", checks[index].name);
    printf("\",\"passed\":%s}", checks[index].passed ? "true" : "false");
  }
  printf("]");
}

int main(int argc, char **argv) {
  HMODULE module_handle = NULL;
  FARPROC descriptor_symbol = NULL;
  module_descriptor_fn descriptor = NULL;
  const al_module_desc *module = NULL;
  al_mailbox_config config;
  al_mailbox_storage_requirements requirements;
  al_mailbox_storage_requirements malformed_requirements;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_runtime *second_runtime = NULL;
  void *storage = NULL;
  void *second_storage = NULL;
  uint32_t init_entry_id = UINT32_MAX;
  uint32_t begin_entry_id = UINT32_MAX;
  uint32_t resume_entry_id = UINT32_MAX;
  uint32_t state_type_id = UINT32_MAX;
  uint32_t continuation_type_id = UINT32_MAX;
  uint32_t int_type_id = UINT32_MAX;
  al_mailbox_token token_a;
  al_mailbox_token original_token_a;
  al_mailbox_token token_b;
  al_mailbox_token token_b_zero;
  al_mailbox_token token_second;
  al_mailbox_token token_a_second;
  al_mailbox_call_info call_info;
  al_mailbox_call_limits short_limit;
  al_mailbox_runtime_stats stats_before;
  al_mailbox_runtime_stats stats_after;
  al_mailbox_runtime_stats post_dispose_stats;
  al_mailbox_state_view a_state_view;
  const al_arena *a_owner_before_failure = NULL;
  al_module_desc malformed_module;
  al_mailbox_result result;
  int64_t a_after_failed_division = INT64_MIN;
  int64_t a_after_failed_capacity = INT64_MIN;
  int64_t a_after_overlap = INT64_MIN;
  int64_t a_final = INT64_MIN;
  int64_t b_after_overlap = INT64_MIN;
  int64_t b_final = INT64_MIN;
  int64_t continuation_delta = INT64_MIN;
  int64_t required_capacity_bytes = -1;
  int64_t required_capacity_nodes = -1;
  uint64_t maximum_live_bytes = 0u;
  uint64_t maximum_live_nodes = 0u;
  char language_failure_code[128] = "";
  foreign_call foreign;
  HANDLE foreign_thread = NULL;
  int fatal = 0;
  int have_post_dispose_stats = 0;
  size_t index;
  int all_passed;
  char fingerprint[AL_MODULE_FINGERPRINT_BYTES * 2u + 1u];

  memset(&token_a, 0, sizeof(token_a));
  memset(&original_token_a, 0, sizeof(original_token_a));
  memset(&token_b, 0, sizeof(token_b));
  memset(&token_b_zero, 0, sizeof(token_b_zero));
  memset(&token_second, 0, sizeof(token_second));
  memset(&token_a_second, 0, sizeof(token_a_second));
  memset(&requirements, 0, sizeof(requirements));
  memset(&config, 0, sizeof(config));
  memset(&foreign, 0, sizeof(foreign));
  memset(fingerprint, 0, sizeof(fingerprint));

  if (argc != 2) {
    fprintf(stderr, "Usage: native_dispatch.exe <module.dll>\n");
    return 2;
  }
  module_handle = LoadLibraryA(argv[1]);
  CHECK("native module DLL loaded", module_handle != NULL);
  if (module_handle == NULL) {
    fatal = 1;
    goto cleanup;
  }
  descriptor_symbol = GetProcAddress(module_handle, "agentlang_module_descriptor");
  CHECK("module descriptor export resolved", descriptor_symbol != NULL);
  if (descriptor_symbol == NULL || sizeof(descriptor_symbol) != sizeof(descriptor)) {
    fatal = 1;
    goto cleanup;
  }
  memcpy(&descriptor, &descriptor_symbol, sizeof(descriptor));
  module = descriptor();
  CHECK("module descriptor returned", module != NULL);
  if (module == NULL) {
    fatal = 1;
    goto cleanup;
  }

  init_entry_id = find_entry_id(module, "mailbox.initialize");
  begin_entry_id = find_entry_id(module, "mailbox.begin");
  resume_entry_id = find_entry_id(module, "mailbox.resume");
  state_type_id = find_type_id(module, "State");
  continuation_type_id = find_type_id(module, "Continuation");
  int_type_id = find_type_id(module, "Int");
  CHECK("one immutable module descriptor has ABI v1 and execution ABI v3",
        module->abi_version == AL_MODULE_ABI_VERSION &&
            module->struct_size == sizeof(*module) &&
            module->runtime_abi_version == AL_RUNTIME_ABI_VERSION &&
            module->entry_count == 3u && module->entries != NULL &&
            module->program != NULL);
  CHECK("entry IDs are found by exact metadata names",
        init_entry_id != UINT32_MAX && begin_entry_id != UINT32_MAX &&
            resume_entry_id != UINT32_MAX &&
            module->entries[init_entry_id].entry_id == init_entry_id &&
            module->entries[begin_entry_id].entry_id == begin_entry_id &&
            module->entries[resume_entry_id].entry_id == resume_entry_id);
  CHECK("module metadata carries Int, State, and Continuation types",
        state_type_id != UINT32_MAX && continuation_type_id != UINT32_MAX &&
            int_type_id != UINT32_MAX);
  {
    const char *init_inputs[] = {"Int"};
    const char *init_outputs[] = {"State"};
    const char *begin_inputs[] = {"State", "Int"};
    const char *begin_outputs[] = {"State", "Continuation"};
    const char *resume_inputs[] = {"State", "Continuation", "Int"};
    const char *resume_outputs[] = {"State"};
    CHECK("initialize metadata signature is Int to State",
          signature_matches(module, init_entry_id, init_inputs, 1u,
                            init_outputs, 1u));
    CHECK("begin metadata signature is State and Int to State and Continuation",
          signature_matches(module, begin_entry_id, begin_inputs, 2u,
                            begin_outputs, 2u));
    CHECK("resume metadata signature is State and Continuation and Int to State",
          signature_matches(module, resume_entry_id, resume_inputs, 3u,
                            resume_outputs, 1u));
  }
  for (index = 0u; index < AL_MODULE_FINGERPRINT_BYTES; ++index) {
    (void)snprintf(&fingerprint[index * 2u], 3u, "%02x",
                   module->fingerprint[index]);
  }

  config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  config.struct_size = (uint32_t)sizeof(config);
  config.mailbox_capacity = 2u;
  config.scratch_byte_capacity = 24u;
  config.scratch_node_capacity = 3u;
  config.retained_byte_capacity = 16u;
  config.retained_node_capacity = 2u;
  config.init_entry_id = init_entry_id;
  config.begin_entry_id = begin_entry_id;
  config.resume_entry_id = resume_entry_id;

  result = al_mailbox_get_storage_requirements(module, &config, &requirements);
  CHECK("bounded caller storage requirements computed", result == AL_MAILBOX_OK &&
            requirements.control_abi_version == AL_MAILBOX_CONTROL_ABI_VERSION &&
            requirements.struct_size == sizeof(requirements) &&
            requirements.storage_bytes > 0u && requirements.storage_alignment > 0u &&
            (requirements.storage_alignment & (requirements.storage_alignment - 1u)) == 0u);
  if (result != AL_MAILBOX_OK || requirements.storage_bytes == 0u ||
      requirements.storage_bytes > (uint64_t)SIZE_MAX) {
    fatal = 1;
    goto cleanup;
  }

  malformed_module = *module;
  malformed_module.runtime_abi_version = AL_RUNTIME_ABI_VERSION + 1u;
  memset(&malformed_requirements, 0, sizeof(malformed_requirements));
  result = al_mailbox_get_storage_requirements(&malformed_module, &config,
                                               &malformed_requirements);
  CHECK("unsupported runtime ABI metadata rejected before dispatch",
        result == AL_MAILBOX_INVALID_MODULE);

  storage = VirtualAlloc(NULL, (SIZE_T)requirements.storage_bytes,
                         MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  CHECK("caller-owned bounded storage allocated with required alignment",
        storage != NULL &&
            ((uintptr_t)storage % requirements.storage_alignment) == 0u);
  if (storage == NULL) {
    fatal = 1;
    goto cleanup;
  }
  {
    al_mailbox_runtime *invalid_runtime = NULL;
    result = al_mailbox_runtime_init(module, &config, storage,
                                     requirements.storage_bytes - 1u,
                                     &invalid_runtime);
    CHECK("one-byte-short storage rejected before runtime creation",
          result == AL_MAILBOX_INVALID_STORAGE && invalid_runtime == NULL);
    invalid_runtime = NULL;
    result = al_mailbox_runtime_init(module, &config, (uint8_t *)storage + 1u,
                                     requirements.storage_bytes - 1u,
                                     &invalid_runtime);
    CHECK("misaligned caller storage rejected before runtime creation",
          result == AL_MAILBOX_INVALID_STORAGE && invalid_runtime == NULL);
  }

  result = al_mailbox_runtime_init(module, &config, storage,
                                   requirements.storage_bytes, &runtime);
  CHECK("native mailbox runtime initialized over supplied storage",
        result == AL_MAILBOX_OK && runtime != NULL);
  if (result != AL_MAILBOX_OK || runtime == NULL) {
    fatal = 1;
    goto cleanup;
  }

  init_call_info(&call_info);
  result = al_mailbox_init_mailbox(runtime, 0u, 10, NULL, &call_info);
  CHECK("mailbox A initializes through native module entry",
        result == AL_MAILBOX_OK && call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS);
  init_call_info(&call_info);
  result = al_mailbox_init_mailbox(runtime, 1u, 100, NULL, &call_info);
  CHECK("mailbox B initializes through native module entry",
        result == AL_MAILBOX_OK && call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS);
  CHECK("initialization returns every shared scratch lease",
        scratch_leases_balanced(runtime));

  init_call_info(&call_info);
  result = al_mailbox_begin(runtime, 0u, 7, NULL, &token_a, &call_info);
  CHECK("mailbox A begins and suspends with native continuation",
        result == AL_MAILBOX_OK && call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS);
  sample_live_maximum(runtime, &maximum_live_bytes, &maximum_live_nodes);
  original_token_a = token_a;
  if (get_state_view(runtime, 0u, &a_state_view)) {
    a_owner_before_failure = a_state_view.owner;
  }
  {
    int64_t state_value = INT64_MIN;
    int64_t delta_value = INT64_MIN;
    al_mailbox_runtime_stats stats;
    CHECK("A suspends with retained state 10 and continuation 7",
          read_state_total(runtime, module, 0u, 1, &state_value, &delta_value) &&
              state_value == 10 && delta_value == 7);
    CHECK("A suspension returns all shared scratch leases",
          get_stats(runtime, &stats) && stats.outstanding_scratch_leases == 0u &&
              stats.scratch_lease_acquisitions == stats.scratch_lease_returns);
  }

  if (get_stats(runtime, &stats_before)) {
    al_mailbox_token unused_token;
    memset(&unused_token, 0x5a, sizeof(unused_token));
    init_call_info(&call_info);
    result = al_mailbox_begin(runtime, 0u, 999, NULL, &unused_token, &call_info);
    (void)get_stats(runtime, &stats_after);
    CHECK("busy mailbox rejects before handler and keeps its token pending",
          result == AL_MAILBOX_BUSY &&
              stats_after.handler_invocations == stats_before.handler_invocations &&
              read_state_total(runtime, module, 0u, 1, &a_after_failed_division,
                               &continuation_delta) &&
              a_after_failed_division == 10 && continuation_delta == 7);
  } else {
    CHECK("pre-busy invocation statistics available", 0);
  }

  memset(&foreign, 0, sizeof(foreign));
  foreign.runtime = runtime;
  foreign.token = token_a;
  if (!get_stats(runtime, &stats_before)) {
    CHECK("pre-foreign-thread invocation statistics available", 0);
  } else {
    foreign_thread = CreateThread(NULL, 0u, foreign_thread_entry, &foreign, 0u, NULL);
    CHECK("foreign Windows thread created", foreign_thread != NULL);
    if (foreign_thread != NULL) {
      DWORD wait_result = WaitForSingleObject(foreign_thread, 5000u);
      (void)get_stats(runtime, &stats_after);
      CHECK("foreign-thread operation is rejected before dispatch",
            wait_result == WAIT_OBJECT_0 &&
                foreign.result == AL_MAILBOX_WRONG_THREAD &&
                stats_after.handler_invocations == stats_before.handler_invocations);
      CloseHandle(foreign_thread);
      foreign_thread = NULL;
    }
  }

  init_call_info(&call_info);
  result = al_mailbox_begin(runtime, 1u, 3, NULL, &token_b, &call_info);
  CHECK("mailbox B begins with an independent native token",
        result == AL_MAILBOX_OK && call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS);
  sample_live_maximum(runtime, &maximum_live_bytes, &maximum_live_nodes);
  {
    int64_t state_value = INT64_MIN;
    int64_t delta_value = INT64_MIN;
    al_mailbox_runtime_stats stats;
    CHECK("B suspends with retained state 100 and continuation 3 within its 16-byte graph",
          read_state_total(runtime, module, 1u, 1, &state_value, &delta_value) &&
              state_value == 100 && delta_value == 3);
    CHECK("B suspension returns all shared scratch leases",
          get_stats(runtime, &stats) && stats.outstanding_scratch_leases == 0u &&
              stats.scratch_lease_acquisitions == stats.scratch_lease_returns);
  }
  if (get_stats(runtime, &stats_before)) {
    init_call_info(&call_info);
    result = al_mailbox_resume(runtime, 1u, &token_a, 1, NULL, &call_info);
    (void)get_stats(runtime, &stats_after);
    CHECK("wrong-mailbox token is rejected before handler execution",
          result == AL_MAILBOX_WRONG_OWNER_TOKEN &&
              stats_after.handler_invocations == stats_before.handler_invocations &&
              read_state_total(runtime, module, 1u, 1, &b_after_overlap,
                               &continuation_delta) &&
              b_after_overlap == 100 && continuation_delta == 3);
  } else {
    CHECK("pre-wrong-owner invocation statistics available", 0);
  }

  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 1u, &token_b, 1, NULL, &call_info);
  CHECK("B completes with report108 value 103",
        result == AL_MAILBOX_OK && call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS &&
            read_state_total(runtime, module, 1u, 0, &b_after_overlap, NULL) &&
            b_after_overlap == 103);
  if (get_stats(runtime, &stats_before)) {
    init_call_info(&call_info);
    result = al_mailbox_resume(runtime, 1u, &token_b, 1, NULL, &call_info);
    (void)get_stats(runtime, &stats_after);
    CHECK("completed B token is rejected as duplicate before handler execution",
          result == AL_MAILBOX_DUPLICATE_TOKEN &&
              stats_after.handler_invocations == stats_before.handler_invocations &&
              read_state_total(runtime, module, 1u, 0, &b_final, NULL) &&
              b_final == 103);
  } else {
    CHECK("pre-duplicate invocation statistics available", 0);
  }

  init_call_info(&call_info);
  result = al_mailbox_begin(runtime, 1u, 0, NULL, &token_b_zero, &call_info);
  sample_live_maximum(runtime, &maximum_live_bytes, &maximum_live_nodes);
  CHECK("B starts a zero-delta second completion",
        result == AL_MAILBOX_OK && scratch_leases_balanced(runtime));
  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 1u, &token_b_zero, 1, NULL, &call_info);
  CHECK("B zero-delta completion retains report108 final value 103",
        result == AL_MAILBOX_OK &&
            read_state_total(runtime, module, 1u, 0, &b_final, NULL) &&
            b_final == 103);
  if (get_stats(runtime, &stats_before)) {
    init_call_info(&call_info);
    result = al_mailbox_resume(runtime, 1u, &token_b, 1, NULL, &call_info);
    (void)get_stats(runtime, &stats_after);
    CHECK("superseded B token is rejected as stale before handler execution",
          result == AL_MAILBOX_STALE_TOKEN &&
              stats_after.handler_invocations == stats_before.handler_invocations &&
              read_state_total(runtime, module, 1u, 0, &b_final, NULL) &&
              b_final == 103);
  } else {
    CHECK("pre-stale invocation statistics available", 0);
  }

  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 0u, &token_a, 0, NULL, &call_info);
  {
    const char *diagnostic_code =
        find_diagnostic_code(module, resume_entry_id, call_info.error_metadata_id);
    if (diagnostic_code != NULL) {
      (void)snprintf(language_failure_code, sizeof(language_failure_code), "%s",
                     diagnostic_code);
    }
  }
  CHECK("divide-by-zero reports the source Flow diagnostic",
        result == AL_MAILBOX_HANDLER_FAILURE &&
            strcmp(language_failure_code, "RUNTIME_DIVIDE_BY_ZERO") == 0 &&
            call_info.handler_status == AL_RUNTIME_STATUS_DIAGNOSTIC);
  CHECK("divide-by-zero failure preserves A owner and pending token for retry",
        read_state_total(runtime, module, 0u, 1, &a_after_failed_division,
                         &continuation_delta) &&
            a_after_failed_division == 10 && continuation_delta == 7 &&
            get_state_view(runtime, 0u, &a_state_view) &&
            a_state_view.owner == a_owner_before_failure &&
            memcmp(&token_a, &original_token_a, sizeof(token_a)) == 0);

  memset(&short_limit, 0, sizeof(short_limit));
  short_limit.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  short_limit.struct_size = (uint32_t)sizeof(short_limit);
  short_limit.retained_byte_limit = 7u;
  short_limit.retained_node_limit = config.retained_node_capacity;
  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 0u, &token_a, 1, &short_limit, &call_info);
  required_capacity_bytes = call_info.error_argument0;
  required_capacity_nodes = call_info.error_argument1;
  CHECK("seven-byte retained limit fails with exact required capacity",
        result == AL_MAILBOX_RETAINED_CAPACITY &&
            required_capacity_bytes == 8 && required_capacity_nodes == 1 &&
            call_info.error_argument0 > short_limit.retained_byte_limit);
  CHECK("capacity failure preserves A owner and the same pending token",
        read_state_total(runtime, module, 0u, 1, &a_after_failed_capacity,
                         &continuation_delta) &&
            a_after_failed_capacity == 10 && continuation_delta == 7 &&
            get_state_view(runtime, 0u, &a_state_view) &&
            a_state_view.owner == a_owner_before_failure &&
            memcmp(&token_a, &original_token_a, sizeof(token_a)) == 0);

  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 0u, &token_a, 1, NULL, &call_info);
  CHECK("same A token retries successfully to report108 value 17",
        result == AL_MAILBOX_OK &&
            read_state_total(runtime, module, 0u, 0, &a_after_overlap, NULL) &&
            a_after_overlap == 17);

  init_call_info(&call_info);
  result = al_mailbox_begin(runtime, 0u, 5, NULL, &token_a_second, &call_info);
  CHECK("A begins the second report108 completion",
        result == AL_MAILBOX_OK && scratch_leases_balanced(runtime));
  init_call_info(&call_info);
  result = al_mailbox_resume(runtime, 0u, &token_a_second, 2, NULL, &call_info);
  CHECK("A finishes at report108 value 19", result == AL_MAILBOX_OK &&
            read_state_total(runtime, module, 0u, 0, &a_final, NULL) &&
            a_final == 19);
  CHECK("B remains at report108 value 103 after A completes",
        read_state_total(runtime, module, 1u, 0, &b_final, NULL) &&
            b_final == 103);
  CHECK("two simultaneous pending mailboxes use 32 live bytes and four nodes",
        maximum_live_bytes == 32u && maximum_live_nodes == 4u);

  second_storage = VirtualAlloc(NULL, (SIZE_T)requirements.storage_bytes,
                                MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  result = al_mailbox_runtime_init(module, &config, second_storage,
                                   requirements.storage_bytes, &second_runtime);
  CHECK("second native runtime initialized with isolated storage",
        second_storage != NULL && result == AL_MAILBOX_OK && second_runtime != NULL);
  if (second_runtime != NULL) {
    init_call_info(&call_info);
    result = al_mailbox_init_mailbox(second_runtime, 0u, 0, NULL, &call_info);
    CHECK("second runtime mailbox initializes", result == AL_MAILBOX_OK);
    init_call_info(&call_info);
    result = al_mailbox_begin(second_runtime, 0u, 0, NULL, &token_second,
                              &call_info);
    CHECK("second runtime obtains its own completion capability",
          result == AL_MAILBOX_OK);
    if (get_stats(second_runtime, &stats_before)) {
      init_call_info(&call_info);
      result = al_mailbox_resume(second_runtime, 0u, &token_a, 1, NULL,
                                 &call_info);
      (void)get_stats(second_runtime, &stats_after);
      CHECK("cross-runtime token rejected before handler execution",
            result == AL_MAILBOX_CROSS_RUNTIME_TOKEN &&
                stats_after.handler_invocations == stats_before.handler_invocations &&
                read_state_total(second_runtime, module, 0u, 1,
                                 &a_after_failed_capacity, &continuation_delta) &&
                continuation_delta == 0);
    } else {
      CHECK("second runtime invocation statistics available", 0);
    }
  }

  CHECK("native controller reports balanced scratch leases at the end",
        get_stats(runtime, &stats_after) &&
            stats_after.outstanding_scratch_leases == 0u &&
            stats_after.scratch_lease_acquisitions ==
                stats_after.scratch_lease_returns &&
            stats_after.scratch_high_water_bytes <= config.scratch_byte_capacity &&
            stats_after.scratch_high_water_nodes <= config.scratch_node_capacity);
  CHECK("bounded retained backing includes two 128-byte banks per mailbox",
        get_stats(runtime, &stats_after) &&
            requirements.retained_reserved_bytes == 512u &&
            stats_after.retained_reserved_bytes == 512u &&
            requirements.scratch_reserved_bytes == 168u &&
            stats_after.scratch_reserved_bytes == 168u);

cleanup:
  if (foreign_thread != NULL) {
    (void)WaitForSingleObject(foreign_thread, 5000u);
    CloseHandle(foreign_thread);
  }
  if (second_runtime != NULL) {
    (void)al_mailbox_dispose(second_runtime);
    (void)al_mailbox_dispose(second_runtime);
    init_stats(&stats_after);
    result = al_mailbox_get_stats(second_runtime, &stats_after);
    CHECK("second-runtime disposal is idempotent and clears retained owners",
          result == AL_MAILBOX_OK && stats_after.live_retained_bytes == 0u &&
              stats_after.live_retained_nodes == 0u &&
              stats_after.outstanding_scratch_leases == 0u &&
              stats_after.scratch_lease_acquisitions ==
                  stats_after.scratch_lease_returns);
    if (second_storage != NULL) {
      VirtualFree(second_storage, 0u, MEM_RELEASE);
      second_storage = NULL;
    }
  }
  if (runtime != NULL) {
    (void)al_mailbox_dispose(runtime);
    result = al_mailbox_dispose(runtime);
    init_stats(&stats_after);
    {
      al_mailbox_result stats_result = al_mailbox_get_stats(runtime, &stats_after);
      post_dispose_stats = stats_after;
      have_post_dispose_stats = stats_result == AL_MAILBOX_OK;
      CHECK("disposal is idempotent and releases every native owner",
            result == AL_MAILBOX_OK && stats_result == AL_MAILBOX_OK &&
                stats_after.initialized_mailboxes == 0u &&
                stats_after.pending_mailboxes == 0u &&
                stats_after.live_retained_bytes == 0u &&
                stats_after.live_retained_nodes == 0u &&
                stats_after.outstanding_scratch_leases == 0u &&
                stats_after.scratch_lease_acquisitions ==
                    stats_after.scratch_lease_returns);
      init_call_info(&call_info);
      result = al_mailbox_begin(runtime, 0u, 0, NULL, &token_a_second,
                                &call_info);
      CHECK("post-disposal work is rejected without invoking a handler",
            result == AL_MAILBOX_DISPOSED &&
                al_mailbox_get_stats(runtime, &stats_after) == AL_MAILBOX_OK &&
                stats_after.initialized_mailboxes == 0u &&
                stats_after.handler_invocations ==
                    post_dispose_stats.handler_invocations);
    }
    (void)al_mailbox_dispose(runtime);
    if (storage != NULL) {
      VirtualFree(storage, 0u, MEM_RELEASE);
      storage = NULL;
    }
  }
  if (module_handle != NULL) {
    FreeLibrary(module_handle);
    module_handle = NULL;
  }

  all_passed = !fatal;
  for (index = 0u; index < check_count; ++index) {
    if (!checks[index].passed) {
      all_passed = 0;
    }
  }
  printf("{\"schemaVersion\":1,\"kind\":\"native-dispatch-runner\",\"passed\":%s,",
         all_passed ? "true" : "false");
  printf("\"fingerprint\":\"%s\",", fingerprint);
  printf("\"moduleEntryIds\":{\"initialize\":%u,\"begin\":%u,\"resume\":%u},",
         init_entry_id, begin_entry_id, resume_entry_id);
  printf("\"expectedOracle\":{\"aAfterOverlap\":17,\"bAfterOverlap\":103,\"aFinal\":19},");
  printf("\"observed\":{\"aAfterOverlap\":\"%" PRId64 "\",\"bAfterOverlap\":\"%" PRId64 "\",\"aFinal\":\"%" PRId64 "\",\"bFinal\":\"%" PRId64 "\",\"languageFailureCode\":\"%s\",\"capacityRequiredBytes\":%" PRId64 ",\"capacityRequiredNodes\":%" PRId64 "},",
         a_after_overlap, b_after_overlap, a_final, b_final,
         language_failure_code, required_capacity_bytes,
         required_capacity_nodes);
  printf("\"storageRequirements\":{\"storageAlignment\":%u,\"workspaceCapacity\":%u,\"storageBytes\":%" PRIu64 ",\"retainedReservedBytes\":%" PRIu64 ",\"scratchReservedBytes\":%" PRIu64 ",\"workspaceReservedBytes\":%" PRIu64 ",\"controllerReservedBytes\":%" PRIu64 "},",
         requirements.storage_alignment, requirements.workspace_capacity,
         requirements.storage_bytes, requirements.retained_reserved_bytes,
         requirements.scratch_reserved_bytes,
         requirements.workspace_reserved_bytes,
         requirements.controller_reserved_bytes);
  printf("\"peakLiveState\":{\"retainedBytes\":%" PRIu64 ",\"retainedNodes\":%" PRIu64 "},",
         maximum_live_bytes, maximum_live_nodes);
  if (have_post_dispose_stats) {
    printf("\"finalStats\":{\"storageReservedBytes\":%" PRIu64 ",\"retainedReservedBytes\":%" PRIu64 ",\"scratchReservedBytes\":%" PRIu64 ",\"workspaceReservedBytes\":%" PRIu64 ",\"liveRetainedBytes\":%" PRIu64 ",\"liveRetainedNodes\":%" PRIu64 ",\"scratchHighWaterBytes\":%" PRIu64 ",\"scratchHighWaterNodes\":%" PRIu64 ",\"handlerInvocations\":%" PRIu64 ",\"handlerFailures\":%" PRIu64 ",\"scratchLeaseAcquisitions\":%" PRIu64 ",\"scratchLeaseReturns\":%" PRIu64 ",\"outstandingScratchLeases\":%u},",
           post_dispose_stats.storage_reserved_bytes,
           post_dispose_stats.retained_reserved_bytes,
           post_dispose_stats.scratch_reserved_bytes,
           post_dispose_stats.workspace_reserved_bytes,
           post_dispose_stats.live_retained_bytes,
           post_dispose_stats.live_retained_nodes,
           post_dispose_stats.scratch_high_water_bytes,
           post_dispose_stats.scratch_high_water_nodes,
           post_dispose_stats.handler_invocations,
           post_dispose_stats.handler_failures,
           post_dispose_stats.scratch_lease_acquisitions,
           post_dispose_stats.scratch_lease_returns,
           post_dispose_stats.outstanding_scratch_leases);
  } else {
    printf("\"finalStats\":null,");
  }
  print_abi();
  putchar(',');
  print_checks();
  printf("}\n");
  return all_passed ? 0 : 1;
}

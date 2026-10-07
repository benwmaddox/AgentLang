#include "arena_runtime.h"

#include <stdio.h>
#include <string.h>

enum {
  TYPE_INT = 0u,
  TYPE_BOOL = 1u,
  TYPE_UNIT = 2u,
  TYPE_PAIR = 3u,
  TYPE_EMPTY = 4u,
  TYPE_WRAPPER = 5u,
  TYPE_SELF = 6u,
  TYPE_INT_ALIAS = 7u,
  TYPE_PAIR_ALIAS = 8u,
  TYPE_COUNT = 9u
};

static const uint32_t pair_field_types[] = {TYPE_INT, TYPE_BOOL};
static const uint32_t wrapper_field_types[] = {TYPE_PAIR, TYPE_INT};
static const uint32_t self_field_types[] = {TYPE_SELF};
static const uint32_t pair_alias_field_types[] = {TYPE_INT, TYPE_BOOL};
static const al_type_desc test_types[TYPE_COUNT] = {
    {AL_RUNTIME_TYPE_INT, 0u, NULL},
    {AL_RUNTIME_TYPE_BOOL, 0u, NULL},
    {AL_RUNTIME_TYPE_UNIT, 0u, NULL},
    {AL_RUNTIME_TYPE_RECORD, 2u, pair_field_types},
    {AL_RUNTIME_TYPE_RECORD, 0u, NULL},
    {AL_RUNTIME_TYPE_RECORD, 2u, wrapper_field_types},
    {AL_RUNTIME_TYPE_RECORD, 1u, self_field_types},
    {AL_RUNTIME_TYPE_INT, 0u, NULL},
    {AL_RUNTIME_TYPE_RECORD, 2u, pair_alias_field_types}};
static const al_program_desc test_program = {test_types, TYPE_COUNT, 0u};
static uint32_t failures;
static uint32_t assertions;

typedef struct test_fixture {
  al_runtime_context context;
  al_arena scratch;
  al_arena retained;
  al_arena input_owner;
  _Alignas(8) uint8_t scratch_data[256];
  _Alignas(8) uint8_t retained_data[256];
  _Alignas(8) uint8_t input_data[256];
  al_node scratch_nodes[16];
  al_node retained_nodes[16];
  al_node input_nodes[16];
  int64_t workspace[32];
  int64_t input_roots[8];
  uint32_t input_root_type_ids[8];
} test_fixture;

typedef struct input_snapshot {
  al_arena owner;
  uint8_t data[256];
  al_node nodes[16];
  int64_t roots[8];
  uint32_t root_type_ids[8];
} input_snapshot;

static void check_result(uint32_t condition, const char *name) {
  ++assertions;
  if (condition == 0u) {
    ++failures;
    (void)printf("FAIL %s\n", name);
  }
}

static void fill_bytes(uint8_t *data, uint32_t length, uint8_t value) {
  uint32_t index;
  for (index = 0u; index < length; ++index) {
    data[index] = value;
  }
}

static uint32_t bytes_equal(const uint8_t *left, const uint8_t *right,
                            uint32_t length) {
  uint32_t index;
  for (index = 0u; index < length; ++index) {
    if (left[index] != right[index]) {
      return 0u;
    }
  }
  return 1u;
}

static void fixture_init(test_fixture *fixture, uint32_t scratch_bytes,
                         uint32_t scratch_nodes, uint32_t retained_bytes,
                         uint32_t retained_nodes, uint32_t workspace_capacity) {
  uint32_t index;

  fill_bytes(fixture->scratch_data, (uint32_t)sizeof(fixture->scratch_data),
             0xA5u);
  fill_bytes(fixture->retained_data, (uint32_t)sizeof(fixture->retained_data),
             0x5Au);
  fill_bytes(fixture->input_data, (uint32_t)sizeof(fixture->input_data), 0x3Cu);
  for (index = 0u; index < (uint32_t)(sizeof(fixture->scratch_nodes) /
                                      sizeof(fixture->scratch_nodes[0]));
       ++index) {
    fill_bytes((uint8_t *)&fixture->scratch_nodes[index],
               (uint32_t)sizeof(al_node), 0u);
    fill_bytes((uint8_t *)&fixture->retained_nodes[index],
               (uint32_t)sizeof(al_node), 0xCCu);
    fill_bytes((uint8_t *)&fixture->input_nodes[index],
               (uint32_t)sizeof(al_node), 0xD3u);
  }
  for (index = 0u; index < (uint32_t)(sizeof(fixture->workspace) /
                                      sizeof(fixture->workspace[0]));
       ++index) {
    fixture->workspace[index] = 0;
  }
  for (index = 0u; index < (uint32_t)(sizeof(fixture->input_roots) /
                                      sizeof(fixture->input_roots[0]));
       ++index) {
    fixture->input_roots[index] = 0;
  }
  for (index = 0u; index < (uint32_t)(sizeof(fixture->input_root_type_ids) /
                                      sizeof(fixture->input_root_type_ids[0]));
       ++index) {
    fixture->input_root_type_ids[index] = 0u;
  }

  fixture->scratch.data = fixture->scratch_data;
  fixture->scratch.byte_capacity = scratch_bytes;
  fixture->scratch.used = 0u;
  fixture->scratch.nodes = fixture->scratch_nodes;
  fixture->scratch.node_capacity = scratch_nodes;
  fixture->scratch.node_count = 0u;
  fixture->scratch.generation = 0x10203040u;
  fixture->scratch.flags = 0u;
  fixture->scratch.reserved = 0u;

  fixture->retained.data = fixture->retained_data;
  fixture->retained.byte_capacity = retained_bytes;
  fixture->retained.used = 0u;
  fixture->retained.nodes = fixture->retained_nodes;
  fixture->retained.node_capacity = retained_nodes;
  fixture->retained.node_count = 0u;
  fixture->retained.generation = 0x50607080u;
  fixture->retained.flags = 0u;
  fixture->retained.reserved = 0u;

  fixture->input_owner.data = fixture->input_data;
  fixture->input_owner.byte_capacity = (uint32_t)sizeof(fixture->input_data);
  fixture->input_owner.used = 0u;
  fixture->input_owner.nodes = fixture->input_nodes;
  fixture->input_owner.node_capacity =
      (uint32_t)(sizeof(fixture->input_nodes) / sizeof(al_node));
  fixture->input_owner.node_count = 0u;
  fixture->input_owner.generation = 0x90ABCDEFu;
  fixture->input_owner.flags = 0u;
  fixture->input_owner.reserved = 0u;

  fixture->context.abi_version = AL_RUNTIME_ABI_VERSION;
  fixture->context.steps_consumed = 0u;
  fixture->context.error_metadata_id = -1;
  fixture->context.reserved_prefix = 0;
  fixture->context.error_argument0 = 0;
  fixture->context.error_argument1 = 0;
  fixture->context.scratch = &fixture->scratch;
  fixture->context.retained = &fixture->retained;
  fixture->context.workspace = fixture->workspace;
  fixture->context.workspace_capacity = workspace_capacity;
  fixture->context.reserved_tail = 0u;
  fixture->context.input_owner = NULL;
  fixture->context.input_roots = NULL;
  fixture->context.input_root_type_ids = NULL;
  fixture->context.input_root_count = 0u;
  fixture->context.reserved_v3 = 0u;
}

static uint64_t make_handle(uint32_t generation, uint32_t node_index) {
  return ((uint64_t)generation << 32u) | (uint64_t)node_index;
}

static uint32_t write_runtime_u64(uint8_t *data, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index) {
    data[index] = (uint8_t)(value >> (index * 8u));
  }
  return 1u;
}

static uint64_t read_runtime_u64(const uint8_t *data) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index) {
    value |= (uint64_t)data[index] << (index * 8u);
  }
  return value;
}

static void build_alias_graph(test_fixture *fixture, uint32_t retained_bytes,
                              uint32_t retained_nodes) {
  fixture_init(fixture, 256u, 16u, retained_bytes, retained_nodes, 32u);
  fixture->workspace[0] = 42;
  fixture->workspace[1] = 1;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_PAIR, &fixture->workspace[0], 2u,
                                      &fixture->workspace[2]) == AL_RUNTIME_OK,
               "make pair");

  fixture->workspace[3] = 42;
  fixture->workspace[4] = 1;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_PAIR, &fixture->workspace[3], 2u,
                                      &fixture->workspace[5]) == AL_RUNTIME_OK,
               "make equal pair");

  fixture->workspace[10] = fixture->workspace[2];
  fixture->workspace[11] = 77;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_WRAPPER, &fixture->workspace[10], 2u,
                                      &fixture->workspace[12]) == AL_RUNTIME_OK,
               "make wrapper");
  fixture->workspace[13] = 0;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_EMPTY, NULL, 0u,
                                      &fixture->workspace[13]) == AL_RUNTIME_OK,
               "make empty record");
}

static void build_input_owner_graph(test_fixture *fixture) {
  al_arena *entry_scratch = fixture->context.scratch;

  fixture->context.scratch = &fixture->input_owner;
  fixture->workspace[0] = 42;
  fixture->workspace[1] = 1;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_PAIR, &fixture->workspace[0], 2u,
                                      &fixture->workspace[2]) == AL_RUNTIME_OK,
               "build input pair");

  fixture->workspace[3] = fixture->workspace[2];
  fixture->workspace[4] = 77;
  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_WRAPPER, &fixture->workspace[3], 2u,
                                      &fixture->workspace[5]) == AL_RUNTIME_OK,
               "build input wrapper");

  check_result(al_runtime_make_record(&fixture->context, &test_program,
                                      TYPE_EMPTY, NULL, 0u,
                                      &fixture->workspace[6]) == AL_RUNTIME_OK,
               "build unselected input node");
  fixture->context.scratch = entry_scratch;
  fixture->context.input_owner = &fixture->input_owner;

  fixture->input_roots[0] = fixture->workspace[5];
  fixture->input_roots[1] = fixture->workspace[2];
  fixture->input_roots[2] = fixture->workspace[2];
  fixture->input_roots[3] = 123;
  fixture->input_root_type_ids[0] = TYPE_WRAPPER;
  fixture->input_root_type_ids[1] = TYPE_PAIR;
  fixture->input_root_type_ids[2] = TYPE_PAIR;
  fixture->input_root_type_ids[3] = TYPE_INT;
  fixture->context.input_roots = fixture->input_roots;
  fixture->context.input_root_type_ids = fixture->input_root_type_ids;
  fixture->context.input_root_count = 4u;
}

static void snapshot_input(const test_fixture *fixture,
                           input_snapshot *snapshot) {
  (void)memcpy(&snapshot->owner, &fixture->input_owner,
               sizeof(snapshot->owner));
  (void)memcpy(snapshot->data, fixture->input_data, sizeof(snapshot->data));
  (void)memcpy(snapshot->nodes, fixture->input_nodes, sizeof(snapshot->nodes));
  (void)memcpy(snapshot->roots, fixture->input_roots, sizeof(snapshot->roots));
  (void)memcpy(snapshot->root_type_ids, fixture->input_root_type_ids,
               sizeof(snapshot->root_type_ids));
}

static uint32_t input_unchanged(const test_fixture *fixture,
                                const input_snapshot *snapshot) {
  return bytes_equal((const uint8_t *)&fixture->input_owner,
                     (const uint8_t *)&snapshot->owner,
                     (uint32_t)sizeof(snapshot->owner)) &&
         bytes_equal(fixture->input_data, snapshot->data,
                     (uint32_t)sizeof(snapshot->data)) &&
         bytes_equal((const uint8_t *)fixture->input_nodes,
                     (const uint8_t *)snapshot->nodes,
                     (uint32_t)sizeof(snapshot->nodes)) &&
         bytes_equal((const uint8_t *)fixture->input_roots,
                     (const uint8_t *)snapshot->roots,
                     (uint32_t)sizeof(snapshot->roots)) &&
         bytes_equal((const uint8_t *)fixture->input_root_type_ids,
                     (const uint8_t *)snapshot->root_type_ids,
                     (uint32_t)sizeof(snapshot->root_type_ids));
}

static void snapshot_workspace(const test_fixture *fixture,
                               int64_t snapshot[32]) {
  (void)memcpy(snapshot, fixture->workspace, sizeof(fixture->workspace));
}

static uint32_t workspace_unchanged(const test_fixture *fixture,
                                    const int64_t snapshot[32]) {
  return bytes_equal((const uint8_t *)fixture->workspace,
                     (const uint8_t *)snapshot,
                     (uint32_t)sizeof(fixture->workspace));
}

static void test_layout(void) {
  (void)printf("context.size=%zu\n", sizeof(al_runtime_context));
  (void)printf("context.align=%zu\n", _Alignof(al_runtime_context));
  (void)printf("context.abi_version=%zu\n",
               offsetof(al_runtime_context, abi_version));
  (void)printf("context.steps_consumed=%zu\n",
               offsetof(al_runtime_context, steps_consumed));
  (void)printf("context.error_metadata_id=%zu\n",
               offsetof(al_runtime_context, error_metadata_id));
  (void)printf("context.reserved_prefix=%zu\n",
               offsetof(al_runtime_context, reserved_prefix));
  (void)printf("context.error_argument0=%zu\n",
               offsetof(al_runtime_context, error_argument0));
  (void)printf("context.error_argument1=%zu\n",
               offsetof(al_runtime_context, error_argument1));
  (void)printf("context.scratch=%zu\n", offsetof(al_runtime_context, scratch));
  (void)printf("context.retained=%zu\n",
               offsetof(al_runtime_context, retained));
  (void)printf("context.workspace=%zu\n",
               offsetof(al_runtime_context, workspace));
  (void)printf("context.workspace_capacity=%zu\n",
               offsetof(al_runtime_context, workspace_capacity));
  (void)printf("context.reserved_tail=%zu\n",
               offsetof(al_runtime_context, reserved_tail));
  (void)printf("context.input_owner=%zu\n",
               offsetof(al_runtime_context, input_owner));
  (void)printf("context.input_roots=%zu\n",
               offsetof(al_runtime_context, input_roots));
  (void)printf("context.input_root_type_ids=%zu\n",
               offsetof(al_runtime_context, input_root_type_ids));
  (void)printf("context.input_root_count=%zu\n",
               offsetof(al_runtime_context, input_root_count));
  (void)printf("context.reserved_v3=%zu\n",
               offsetof(al_runtime_context, reserved_v3));
  (void)printf("arena.size=%zu\n", sizeof(al_arena));
  (void)printf("arena.align=%zu\n", _Alignof(al_arena));
  (void)printf("arena.data=%zu\n", offsetof(al_arena, data));
  (void)printf("arena.byte_capacity=%zu\n", offsetof(al_arena, byte_capacity));
  (void)printf("arena.used=%zu\n", offsetof(al_arena, used));
  (void)printf("arena.nodes=%zu\n", offsetof(al_arena, nodes));
  (void)printf("arena.node_capacity=%zu\n", offsetof(al_arena, node_capacity));
  (void)printf("arena.node_count=%zu\n", offsetof(al_arena, node_count));
  (void)printf("arena.generation=%zu\n", offsetof(al_arena, generation));
  (void)printf("arena.flags=%zu\n", offsetof(al_arena, flags));
  (void)printf("arena.reserved=%zu\n", offsetof(al_arena, reserved));
  (void)printf("node.size=%zu\n", sizeof(al_node));
  (void)printf("node.align=%zu\n", _Alignof(al_node));
  (void)printf("node.type_id=%zu\n", offsetof(al_node, type_id));
  (void)printf("node.field_count=%zu\n", offsetof(al_node, field_count));
  (void)printf("node.payload_offset=%zu\n", offsetof(al_node, payload_offset));
  (void)printf("node.payload_bytes=%zu\n", offsetof(al_node, payload_bytes));
  (void)printf("node.mark=%zu\n", offsetof(al_node, mark));
  (void)printf("node.reserved=%zu\n", offsetof(al_node, reserved));
  (void)printf("node.forward_handle=%zu\n", offsetof(al_node, forward_handle));
  (void)printf("type_desc.size=%zu\n", sizeof(al_type_desc));
  (void)printf("type_desc.kind=%zu\n", offsetof(al_type_desc, kind));
  (void)printf("type_desc.field_count=%zu\n",
               offsetof(al_type_desc, field_count));
  (void)printf("type_desc.field_types=%zu\n",
               offsetof(al_type_desc, field_types));
  (void)printf("program_desc.size=%zu\n", sizeof(al_program_desc));
  (void)printf("program_desc.types=%zu\n", offsetof(al_program_desc, types));
  (void)printf("program_desc.type_count=%zu\n",
               offsetof(al_program_desc, type_count));
  (void)printf("program_desc.reserved=%zu\n",
               offsetof(al_program_desc, reserved));
}

static void test_layout_json(void) {
  (void)printf(
      "{\"context\":{\"size\":%zu,\"align\":%zu,\"offsets\":{\"abi_version\":%"
      "zu,\"steps_consumed\":%zu,\"error_metadata_id\":%zu,\"reserved_prefix\":"
      "%zu,\"error_argument0\":%zu,\"error_argument1\":%zu,\"scratch\":%zu,"
      "\"retained\":%zu,\"workspace\":%zu,\"workspace_capacity\":%zu,"
      "\"reserved_tail\":%zu,\"input_owner\":%zu,\"input_roots\":%zu,"
      "\"input_root_type_ids\":%zu,\"input_root_count\":%zu,"
      "\"reserved_v3\":%zu}},"
      "\"arena\":{\"size\":%zu,\"align\":%zu,\"offsets\":{\"data\":%zu,\"byte_"
      "capacity\":%zu,\"used\":%zu,\"nodes\":%zu,\"node_capacity\":%zu,\"node_"
      "count\":%zu,\"generation\":%zu,\"flags\":%zu,\"reserved\":%zu}},"
      "\"node\":{\"size\":%zu,\"align\":%zu,\"offsets\":{\"type_id\":%zu,"
      "\"field_count\":%zu,\"payload_offset\":%zu,\"payload_bytes\":%zu,"
      "\"mark\":%zu,\"reserved\":%zu,\"forward_handle\":%zu}},"
      "\"type_desc\":{\"size\":%zu,\"align\":%zu,\"offsets\":{\"kind\":%zu,"
      "\"field_count\":%zu,\"field_types\":%zu}},"
      "\"program_desc\":{\"size\":%zu,\"align\":%zu,\"offsets\":{\"types\":%zu,"
      "\"type_count\":%zu,\"reserved\":%zu}}}\n",
      sizeof(al_runtime_context), _Alignof(al_runtime_context),
      offsetof(al_runtime_context, abi_version),
      offsetof(al_runtime_context, steps_consumed),
      offsetof(al_runtime_context, error_metadata_id),
      offsetof(al_runtime_context, reserved_prefix),
      offsetof(al_runtime_context, error_argument0),
      offsetof(al_runtime_context, error_argument1),
      offsetof(al_runtime_context, scratch),
      offsetof(al_runtime_context, retained),
      offsetof(al_runtime_context, workspace),
      offsetof(al_runtime_context, workspace_capacity),
      offsetof(al_runtime_context, reserved_tail),
      offsetof(al_runtime_context, input_owner),
      offsetof(al_runtime_context, input_roots),
      offsetof(al_runtime_context, input_root_type_ids),
      offsetof(al_runtime_context, input_root_count),
      offsetof(al_runtime_context, reserved_v3), sizeof(al_arena),
      _Alignof(al_arena), offsetof(al_arena, data),
      offsetof(al_arena, byte_capacity), offsetof(al_arena, used),
      offsetof(al_arena, nodes), offsetof(al_arena, node_capacity),
      offsetof(al_arena, node_count), offsetof(al_arena, generation),
      offsetof(al_arena, flags), offsetof(al_arena, reserved), sizeof(al_node),
      _Alignof(al_node), offsetof(al_node, type_id),
      offsetof(al_node, field_count), offsetof(al_node, payload_offset),
      offsetof(al_node, payload_bytes), offsetof(al_node, mark),
      offsetof(al_node, reserved), offsetof(al_node, forward_handle),
      sizeof(al_type_desc), _Alignof(al_type_desc),
      offsetof(al_type_desc, kind), offsetof(al_type_desc, field_count),
      offsetof(al_type_desc, field_types), sizeof(al_program_desc),
      _Alignof(al_program_desc), offsetof(al_program_desc, types),
      offsetof(al_program_desc, type_count),
      offsetof(al_program_desc, reserved));
}

static void test_request_validation(void) {
  test_fixture fixture;
  int64_t public_outputs[2] = {11, 22};
  int32_t status = -7;
  al_runtime_result result;

  fixture_init(&fixture, 64u, 4u, 64u, 4u, 8u);
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_OK && status == AL_RUNTIME_STATUS_SUCCESS,
               "valid request");

  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 3u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   status == AL_RUNTIME_STATUS_INVALID_REQUEST,
               "output count exceeds capacity");

  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       (int32_t *)&fixture.workspace[0], 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.workspace[0] == 0,
               "status workspace alias rejected without write");

  status = -7;
  result =
      al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                  (int32_t *)&fixture.scratch.generation, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.generation == 0x10203040u,
               "status arena alias rejected without write");

  status = -7;
  result = al_runtime_validate_request(&fixture.context, fixture.workspace, 1u,
                                       8u, &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7,
               "public output workspace alias rejected without write");

  fixture.context.input_root_count = 1u;
  fixture.context.input_roots = fixture.workspace;
  fixture.context.input_root_type_ids = fixture.input_root_type_ids;
  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7,
               "input roots workspace alias rejected before status write");

  fixture.context.input_roots = public_outputs;
  fixture.context.input_root_type_ids = fixture.input_root_type_ids;
  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7 &&
                   public_outputs[0] == 11 && public_outputs[1] == 22,
               "input roots public output alias rejected without writes");

  fixture.context.input_roots = fixture.input_roots;
  fixture.context.input_root_type_ids = (const uint32_t *)&status;
  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7,
               "input type IDs status alias rejected without write");

  fixture.context.input_root_count = 0u;
  fixture.context.input_roots = NULL;
  fixture.context.input_root_type_ids = NULL;
  fixture.context.input_owner = (const al_arena *)&status;
  status = 1234;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == 1234,
               "input owner status alias rejected before descriptor read");

  fixture.context.input_owner = (const al_arena *)public_outputs;
  status = 4321;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(
      result == AL_RUNTIME_INVALID_REQUEST && status == 4321 &&
          public_outputs[0] == 11 && public_outputs[1] == 22,
      "input owner public-output alias rejected before descriptor read");

  fixture.context.input_owner =
      (const al_arena *)&fixture.context.error_argument0;
  status = 5432;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == 5432,
               "input owner context alias rejected before descriptor read");

  fixture.context.input_owner = &fixture.input_owner;
  fixture.input_owner.data = (uint8_t *)public_outputs;
  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7 &&
                   public_outputs[0] == 11 && public_outputs[1] == 22,
               "input data public-output alias rejected without writes");
  fixture.input_owner.data = fixture.input_data;
  fixture.context.input_owner = NULL;

  fixture.context.workspace = (int64_t *)fixture.scratch_nodes;
  status = -7;
  result = al_runtime_validate_request(&fixture.context, public_outputs, 2u, 2u,
                                       &status, 8u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST && status == -7,
               "workspace directory alias rejected without status write");
}

static void test_record_helpers(void) {
  test_fixture fixture;
  int32_t status = -1;
  al_runtime_result result;
  uint32_t bool_before[2] = {0u, 0xAABBCCDDu};

  build_alias_graph(&fixture, 256u, 16u);
  result =
      al_runtime_validate_request(&fixture.context, NULL, 0u, 0u, &status, 32u);
  check_result(result == AL_RUNTIME_OK,
               "entry validates empty public output list");

  fixture.workspace[14] = 0;
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_OK &&
                   fixture.workspace[14] == fixture.workspace[2],
               "get record child");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 1u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_OK && fixture.workspace[14] == 77,
               "get scalar field");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_PAIR,
                           fixture.workspace[12], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "wrong nominal record type");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 2u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REQUEST, "field index range");
  result = al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                                (int64_t)make_handle(0xDEADBEEFu, 1u), 0u,
                                &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE, "wrong handle owner");
  result = al_runtime_get_field(
      &fixture.context, &test_program, TYPE_WRAPPER,
      (int64_t)make_handle(fixture.scratch.generation + 1u, 1u), 0u,
      &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "stale handle generation");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           (int64_t)make_handle(fixture.scratch.generation, 0u),
                           0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE, "zero handle index");
  result = al_runtime_get_field(
      &fixture.context, &test_program, TYPE_WRAPPER,
      (int64_t)make_handle(fixture.scratch.generation,
                           fixture.scratch.node_count + 1u),
      0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "out of range handle index");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_PAIR_ALIAS,
                           fixture.workspace[2], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "nominal type id mismatch");

  fixture.workspace[1] = 2;
  result =
      al_runtime_make_record(&fixture.context, &test_program, TYPE_PAIR,
                             &fixture.workspace[0], 2u, &fixture.workspace[16]);
  check_result(result == AL_RUNTIME_INVALID_REQUEST,
               "noncanonical Bool rejected");
  check_result(fixture.scratch.node_count == 4u && fixture.scratch.used == 48u,
               "invalid constructor did not allocate");
  fixture.workspace[1] = 1;

  fixture.workspace[16] = 0;
  result = al_runtime_make_record(&fixture.context, &test_program, TYPE_WRAPPER,
                                  &fixture.workspace[12], 2u,
                                  &fixture.workspace[16]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "constructor rejects wrong child type");
  check_result(fixture.scratch.node_count == 4u && fixture.scratch.used == 48u,
               "bad child did not allocate");

  fixture.workspace[0] = 42;
  fixture.workspace[1] = 1;
  bool_before[0] = 0u;
  bool_before[1] = 0xAABBCCDDu;
  fixture.workspace[17] =
      (int64_t)(((uint64_t)bool_before[1] << 32u) | bool_before[0]);
  result = al_runtime_equal(&fixture.context, &test_program, TYPE_PAIR,
                            fixture.workspace[2], fixture.workspace[5],
                            (uint32_t *)&fixture.workspace[17]);
  check_result(result == AL_RUNTIME_OK, "structural equality success");
  check_result(((uint8_t *)&fixture.workspace[17])[0] == 1u &&
                   ((uint8_t *)&fixture.workspace[17])[4] == 0xDDu,
               "structural equality compares fields and writes only Bool");
  result = al_runtime_equal(&fixture.context, &test_program, TYPE_PAIR,
                            fixture.workspace[2],
                            (int64_t)make_handle(0xDEADBEEFu, 1u),
                            (uint32_t *)&fixture.workspace[18]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "equality rejects wrong owner");

  fixture.scratch.nodes[0].forward_handle =
      make_handle(fixture.scratch.generation, 1u);
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "forward handle corruption rejected");
  fixture.scratch.nodes[0].forward_handle = 0u;

  fixture.scratch.nodes[2].payload_offset = 1000u;
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE,
               "payload range corruption rejected");
  fixture.scratch.nodes[2].payload_offset = 32u;

  fixture.scratch_nodes[4].type_id = TYPE_SELF;
  fixture.scratch_nodes[4].field_count = 1u;
  fixture.scratch_nodes[4].payload_offset = 48u;
  fixture.scratch_nodes[4].payload_bytes = 8u;
  fixture.scratch_nodes[4].mark = 0u;
  fixture.scratch_nodes[4].reserved = 0u;
  fixture.scratch_nodes[4].forward_handle = 0u;
  write_runtime_u64(fixture.scratch_data + 48u,
                    make_handle(fixture.scratch.generation, 5u));
  fixture.scratch.used = 56u;
  fixture.scratch.node_count = 5u;
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[12], 0u, &fixture.workspace[14]);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE, "self edge rejected");
  fixture.scratch.used = 48u;
  fixture.scratch.node_count = 4u;

  fixture.workspace[19] = 0;
  result = al_runtime_make_record(&fixture.context, &test_program, TYPE_EMPTY,
                                  NULL, 0u, &fixture.workspace[19]);
  check_result(result == AL_RUNTIME_OK && fixture.scratch.used == 48u &&
                   fixture.scratch.node_count == 5u,
               "empty record consumes directory only");

  {
    al_type_desc alias_types[TYPE_COUNT];
    al_program_desc alias_program;
    (void)memcpy(alias_types, test_types, sizeof(alias_types));
    alias_types[TYPE_PAIR].field_types = (const uint32_t *)fixture.scratch_data;
    alias_program.types = alias_types;
    alias_program.type_count = TYPE_COUNT;
    alias_program.reserved = 0u;
    result =
        al_runtime_make_record(&fixture.context, &alias_program, TYPE_EMPTY,
                               NULL, 0u, &fixture.workspace[20]);
    check_result(result == AL_RUNTIME_INVALID_REQUEST,
                 "field descriptor arena alias rejected");
  }

  {
    union program_workspace {
      al_program_desc program;
      int64_t slots[32];
    } alias_storage;
    al_runtime_context alias_context = fixture.context;
    alias_storage.program = test_program;
    alias_context.workspace = alias_storage.slots;
    alias_context.workspace_capacity = 32u;
    result =
        al_runtime_make_record(&alias_context, &alias_storage.program,
                               TYPE_EMPTY, NULL, 0u, &alias_storage.slots[4]);
    check_result(result == AL_RUNTIME_INVALID_REQUEST,
                 "program descriptor workspace alias rejected");
  }
}

static uint32_t promote_alias_graph(test_fixture *fixture,
                                    uint32_t retained_bytes,
                                    uint32_t retained_nodes,
                                    int64_t *public_roots) {
  static const uint32_t roots_types[] = {TYPE_WRAPPER, TYPE_WRAPPER, TYPE_INT};
  fixture->workspace[0] = fixture->workspace[12];
  fixture->workspace[1] = fixture->workspace[12];
  fixture->workspace[2] = 5;
  fixture->retained.byte_capacity = retained_bytes;
  fixture->retained.node_capacity = retained_nodes;
  return (uint32_t)al_runtime_promote(&fixture->context, &test_program,
                                      fixture->workspace, roots_types, 3u,
                                      public_roots, 3u);
}

static void test_promotion(void) {
  test_fixture fixture;
  int64_t public_roots[5];
  int64_t public_before[5];
  uint8_t retained_before[256];
  al_node directory_before[16];
  uint32_t result;
  uint32_t index;

  build_alias_graph(&fixture, 32u, 2u);
  check_result(al_runtime_get_field(&fixture.context, &test_program,
                                    TYPE_WRAPPER, fixture.workspace[12], 0u,
                                    &fixture.workspace[14]) == AL_RUNTIME_OK,
               "promotion source graph validates");
  public_roots[0] = 0x1122334455667788ll;
  public_roots[1] = -1;
  public_roots[2] = -2;
  public_roots[3] = -3;
  public_roots[4] = 0x7766554433221100ll;
  for (index = 0u; index < 5u; ++index) {
    public_before[index] = public_roots[index];
  }
  result = promote_alias_graph(&fixture, 32u, 2u, &public_roots[1]);
  check_result(result == AL_RUNTIME_OK, "exact fit promotion");
  check_result(fixture.retained.used == 32u &&
                   fixture.retained.node_count == 2u,
               "promotion copies only reachable records");
  check_result(public_roots[0] == public_before[0] &&
                   public_roots[4] == public_before[4],
               "promotion preserves public canaries");
  check_result((uint64_t)public_roots[1] ==
                   make_handle(fixture.retained.generation, 2u),
               "promotes root handle");
  check_result(public_roots[1] == public_roots[2],
               "promotion preserves multi-root aliasing");
  check_result(public_roots[3] == 5, "promotion preserves scalar root");
  check_result((uint64_t)fixture.retained_nodes[1].forward_handle == 0u,
               "retained forward handle cleared");
  check_result((uint64_t)fixture.workspace[12] ==
                   make_handle(fixture.scratch.generation, 3u),
               "scratch handle unchanged");

  build_alias_graph(&fixture, 31u, 2u);
  for (index = 0u; index < 5u; ++index) {
    public_roots[index] =
        (int64_t)(0x0101010101010101ull * (uint64_t)(index + 1u));
    public_before[index] = public_roots[index];
  }
  fill_bytes(retained_before, (uint32_t)sizeof(retained_before), 0u);
  for (index = 0u; index < (uint32_t)sizeof(fixture.retained_data); ++index) {
    retained_before[index] = fixture.retained_data[index];
  }
  for (index = 0u; index < 16u; ++index) {
    directory_before[index] = fixture.retained_nodes[index];
  }
  result = promote_alias_graph(&fixture, 31u, 2u, &public_roots[1]);
  check_result(result == AL_RUNTIME_RETAINED_CAPACITY,
               "one-short retained bytes rejected");
  check_result(fixture.context.error_argument0 == 32 &&
                   fixture.context.error_argument1 == 2,
               "retained required capacity args");
  check_result(fixture.retained.used == 0u && fixture.retained.node_count == 0u,
               "failed byte promotion preserves retained metadata");
  check_result(bytes_equal(fixture.retained_data, retained_before,
                           (uint32_t)sizeof(retained_before)),
               "failed byte promotion preserves retained data");
  check_result(bytes_equal((const uint8_t *)fixture.retained_nodes,
                           (const uint8_t *)directory_before,
                           (uint32_t)sizeof(directory_before)),
               "failed byte promotion preserves retained directory");
  check_result(bytes_equal((const uint8_t *)&public_roots[1],
                           (const uint8_t *)&public_before[1],
                           3u * (uint32_t)sizeof(int64_t)),
               "failed byte promotion preserves public outputs");
  check_result(public_roots[0] == public_before[0] &&
                   public_roots[4] == public_before[4],
               "failed byte promotion preserves public canaries");

  build_alias_graph(&fixture, 32u, 1u);
  for (index = 0u; index < 5u; ++index) {
    public_roots[index] = (int64_t)(0xF0F0F0F0F0F0F0F0ull ^ (uint64_t)index);
    public_before[index] = public_roots[index];
  }
  result = promote_alias_graph(&fixture, 32u, 1u, &public_roots[1]);
  check_result(result == AL_RUNTIME_RETAINED_CAPACITY,
               "one-short retained nodes rejected");
  check_result(fixture.context.error_argument0 == 32 &&
                   fixture.context.error_argument1 == 2,
               "node shortage reports required totals");
  check_result(fixture.retained.used == 0u && fixture.retained.node_count == 0u,
               "failed node promotion preserves retained metadata");
  check_result(bytes_equal((const uint8_t *)&public_roots[1],
                           (const uint8_t *)&public_before[1],
                           3u * (uint32_t)sizeof(int64_t)),
               "failed node promotion preserves public outputs");

  fixture_init(&fixture, 0u, 2u, 0u, 1u, 8u);
  fixture.workspace[0] = 0;
  check_result(al_runtime_make_record(&fixture.context, &test_program,
                                      TYPE_EMPTY, NULL, 0u,
                                      &fixture.workspace[1]) == AL_RUNTIME_OK,
               "empty root construction");
  fixture.workspace[0] = fixture.workspace[1];
  {
    static const uint32_t empty_root_types[] = {TYPE_EMPTY};
    result = (uint32_t)al_runtime_promote(&fixture.context, &test_program,
                                          fixture.workspace, empty_root_types,
                                          1u, &public_roots[1], 1u);
  }
  check_result(result == AL_RUNTIME_OK && fixture.retained.node_count == 1u &&
                   fixture.retained.used == 0u,
               "empty record promotion");
}

static void test_state_import(void) {
  static const uint32_t expected_types[4] = {TYPE_WRAPPER, TYPE_PAIR, TYPE_PAIR,
                                             TYPE_INT};
  static const uint32_t primitive_types[3] = {TYPE_INT, TYPE_BOOL, TYPE_UNIT};
  static const uint32_t wrong_nominal_types[4] = {TYPE_WRAPPER, TYPE_PAIR_ALIAS,
                                                  TYPE_PAIR, TYPE_INT};
  static const uint32_t wrong_handle_types[4] = {TYPE_PAIR, TYPE_PAIR,
                                                 TYPE_PAIR, TYPE_INT};
  test_fixture fixture;
  input_snapshot input_before;
  int64_t workspace_before[32];
  int64_t public_outputs[2] = {0x1234, 0x5678};
  uint8_t scratch_data_before[sizeof(fixture.scratch_data)];
  al_node scratch_nodes_before[sizeof(fixture.scratch_nodes) / sizeof(al_node)];
  al_runtime_result result;
  uint32_t index;
  int32_t entry_status = -7;

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  for (index = 0u; index < 32u; ++index) {
    fixture.workspace[index] = -777;
  }
  result = al_runtime_validate_request(&fixture.context, public_outputs, 0u, 2u,
                                       &entry_status, 8u);
  check_result(result == AL_RUNTIME_OK &&
                   entry_status == AL_RUNTIME_STATUS_SUCCESS,
               "valid ABI3 request accepts immutable input spans");
  snapshot_input(&fixture, &input_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_OK, "imports nested immutable owner");
  check_result(fixture.scratch.used == 32u && fixture.scratch.node_count == 3u,
               "imports entire graph including unselected node");
  check_result((uint64_t)fixture.workspace[0] ==
                       make_handle(fixture.scratch.generation, 2u) &&
                   (uint64_t)fixture.workspace[1] ==
                       make_handle(fixture.scratch.generation, 1u) &&
                   fixture.workspace[1] == fixture.workspace[2] &&
                   fixture.workspace[3] == 123,
               "rewrites ordered roots and preserves repeated sharing");
  check_result(fixture.scratch.nodes[2].type_id == TYPE_EMPTY &&
                   fixture.scratch.nodes[2].payload_bytes == 0u,
               "preserves unselected node index");
  check_result(read_runtime_u64(fixture.scratch_data + 16u) ==
                   make_handle(fixture.scratch.generation, 1u),
               "rewrites nested payload handle generation");
  result =
      al_runtime_get_field(&fixture.context, &test_program, TYPE_WRAPPER,
                           fixture.workspace[0], 0u, &fixture.workspace[4]);
  check_result(result == AL_RUNTIME_OK &&
                   fixture.workspace[4] == fixture.workspace[1],
               "imported graph supports existing helpers");
  check_result(input_unchanged(&fixture, &input_before),
               "input descriptor, graph and arguments remain immutable");
  check_result(public_outputs[0] == 0x1234 && public_outputs[1] == 0x5678 &&
                   fixture.retained.used == 0u &&
                   fixture.retained.node_count == 0u,
               "import leaves public outputs and output owner untouched");

  (void)memcpy(scratch_data_before, fixture.scratch_data,
               sizeof(scratch_data_before));
  (void)memcpy(scratch_nodes_before, fixture.scratch_nodes,
               sizeof(scratch_nodes_before));
  fixture.workspace[7] = -555;
  result = al_runtime_make_record(&fixture.context, &test_program, TYPE_EMPTY,
                                  NULL, 0u, &fixture.workspace[7]);
  check_result(result == AL_RUNTIME_SCRATCH_CAPACITY &&
                   fixture.context.error_argument0 == 32 &&
                   fixture.context.error_argument1 == 4,
               "later body allocation reports required scratch totals");
  check_result(fixture.workspace[7] == -555 && fixture.scratch.used == 32u &&
                   fixture.scratch.node_count == 3u &&
                   bytes_equal(fixture.scratch_data, scratch_data_before,
                               (uint32_t)sizeof(scratch_data_before)) &&
                   bytes_equal((const uint8_t *)fixture.scratch_nodes,
                               (const uint8_t *)scratch_nodes_before,
                               (uint32_t)sizeof(scratch_nodes_before)),
               "later body capacity failure leaves imported scratch atomic");
  check_result(input_unchanged(&fixture, &input_before),
               "input remains unchanged after later body capacity failure");

  fixture_init(&fixture, 31u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  for (index = 0u; index < 32u; ++index) {
    fixture.workspace[index] = -999;
  }
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  (void)memcpy(scratch_data_before, fixture.scratch_data,
               sizeof(scratch_data_before));
  (void)memcpy(scratch_nodes_before, fixture.scratch_nodes,
               sizeof(scratch_nodes_before));
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_SCRATCH_CAPACITY &&
                   fixture.context.error_argument0 == 32 &&
                   fixture.context.error_argument1 == 3,
               "one-short import bytes report exact required totals");
  check_result(fixture.scratch.used == 0u && fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   bytes_equal(fixture.scratch_data, scratch_data_before,
                               (uint32_t)sizeof(scratch_data_before)) &&
                   bytes_equal((const uint8_t *)fixture.scratch_nodes,
                               (const uint8_t *)scratch_nodes_before,
                               (uint32_t)sizeof(scratch_nodes_before)) &&
                   input_unchanged(&fixture, &input_before),
               "one-short import bytes preserve mutable and input data");

  fixture_init(&fixture, 32u, 2u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  for (index = 0u; index < 32u; ++index) {
    fixture.workspace[index] = -1001;
  }
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  (void)memcpy(scratch_data_before, fixture.scratch_data,
               sizeof(scratch_data_before));
  (void)memcpy(scratch_nodes_before, fixture.scratch_nodes,
               sizeof(scratch_nodes_before));
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_SCRATCH_CAPACITY &&
                   fixture.context.error_argument0 == 32 &&
                   fixture.context.error_argument1 == 3,
               "one-short import nodes report exact required totals");
  check_result(fixture.scratch.used == 0u && fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   bytes_equal(fixture.scratch_data, scratch_data_before,
                               (uint32_t)sizeof(scratch_data_before)) &&
                   bytes_equal((const uint8_t *)fixture.scratch_nodes,
                               (const uint8_t *)scratch_nodes_before,
                               (uint32_t)sizeof(scratch_nodes_before)) &&
                   input_unchanged(&fixture, &input_before),
               "one-short import nodes preserve mutable and input data");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  fixture.input_roots[0] = -44;
  fixture.input_roots[1] = 1;
  fixture.input_roots[2] = 0;
  fixture.input_root_type_ids[0] = TYPE_INT;
  fixture.input_root_type_ids[1] = TYPE_BOOL;
  fixture.input_root_type_ids[2] = TYPE_UNIT;
  fixture.context.input_roots = fixture.input_roots;
  fixture.context.input_root_type_ids = fixture.input_root_type_ids;
  fixture.context.input_root_count = 3u;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   primitive_types, 3u);
  check_result(result == AL_RUNTIME_OK && fixture.workspace[0] == -44 &&
                   fixture.workspace[1] == 1 && fixture.workspace[2] == 0 &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u,
               "primitive-only entry imports without an owner");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  result = al_runtime_import_state(&fixture.context, &test_program, NULL, 0u);
  check_result(result == AL_RUNTIME_OK && fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u,
               "no-input entry imports with null arrays and owner");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 3u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "input count mismatch rejected without mutation");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   wrong_nominal_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "nominal input type mismatch rejected without mutation");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  fixture.input_root_type_ids[0] = TYPE_PAIR;
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   wrong_handle_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "input handle nominal type mismatch rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  fixture.input_roots[0] = (int64_t)make_handle(0xDEADBEEFu, 2u);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "wrong-owner input handle rejected without mutation");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  fixture.input_roots[0] =
      (int64_t)make_handle(fixture.input_owner.generation, 4u);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "out-of-range input handle rejected without mutation");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  (void)write_runtime_u64(fixture.input_data + 16u,
                          make_handle(fixture.input_owner.generation, 2u));
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "self-edge in immutable input graph rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  (void)write_runtime_u64(fixture.input_data + 16u,
                          make_handle(0xDEADBEEFu, 1u));
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "wrong-owner nested handle rejected without mutation");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  (void)write_runtime_u64(fixture.input_data + 8u, 2u);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "noncanonical input Bool field rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  fixture.context.input_owner = NULL;
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REFERENCE &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "record input without its owner rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  (void)memcpy(scratch_data_before, fixture.scratch_data,
               sizeof(scratch_data_before));
  fixture.input_owner.data = fixture.scratch_data;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   bytes_equal(fixture.scratch_data, scratch_data_before,
                               (uint32_t)sizeof(scratch_data_before)),
               "source data overlapping mutable scratch is rejected");
  fixture.input_owner.data = fixture.input_data;
  check_result(input_unchanged(&fixture, &input_before),
               "rejected source data alias writes no input bytes");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  (void)memcpy(scratch_nodes_before, fixture.scratch_nodes,
               sizeof(scratch_nodes_before));
  fixture.input_owner.nodes = fixture.scratch_nodes;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   bytes_equal((const uint8_t *)fixture.scratch_nodes,
                               (const uint8_t *)scratch_nodes_before,
                               (uint32_t)sizeof(scratch_nodes_before)),
               "source directory overlapping mutable scratch is rejected");
  fixture.input_owner.nodes = fixture.input_nodes;
  check_result(input_unchanged(&fixture, &input_before),
               "rejected source directory alias writes no input metadata");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  fixture.context.input_roots = (const int64_t *)fixture.workspace;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   input_unchanged(&fixture, &input_before),
               "input roots overlapping workspace are rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  fixture.context.input_root_type_ids = (const uint32_t *)fixture.workspace;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   input_unchanged(&fixture, &input_before),
               "input type IDs overlapping workspace are rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   (const uint32_t *)fixture.workspace, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   input_unchanged(&fixture, &input_before),
               "expected type IDs overlapping workspace are rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  fixture.context.input_owner = (const al_arena *)fixture.workspace;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   fixture.scratch.used == 0u &&
                   fixture.scratch.node_count == 0u &&
                   input_unchanged(&fixture, &input_before),
               "input descriptor overlapping workspace is rejected");

  fixture_init(&fixture, 32u, 3u, 64u, 4u, 8u);
  build_input_owner_graph(&fixture);
  snapshot_input(&fixture, &input_before);
  snapshot_workspace(&fixture, workspace_before);
  fixture.scratch.used = 8u;
  result = al_runtime_import_state(&fixture.context, &test_program,
                                   expected_types, 4u);
  check_result(result == AL_RUNTIME_INVALID_REQUEST &&
                   fixture.scratch.used == 8u &&
                   fixture.scratch.node_count == 0u &&
                   workspace_unchanged(&fixture, workspace_before) &&
                   input_unchanged(&fixture, &input_before),
               "import rejects nonempty scratch without mutation");
}

static void test_capacities_and_malformed_requests(void) {
  test_fixture fixture;
  int64_t public_roots[3] = {1, 2, 3};
  int64_t before[3] = {1, 2, 3};
  al_runtime_result result;

  fixture_init(&fixture, 15u, 4u, 32u, 2u, 8u);
  fixture.workspace[0] = 42;
  fixture.workspace[1] = 1;
  fixture.workspace[2] = -99;
  result =
      al_runtime_make_record(&fixture.context, &test_program, TYPE_PAIR,
                             &fixture.workspace[0], 2u, &fixture.workspace[2]);
  check_result(result == AL_RUNTIME_SCRATCH_CAPACITY,
               "one-short scratch bytes rejected");
  check_result(fixture.context.error_argument0 == 16 &&
                   fixture.context.error_argument1 == 1,
               "scratch required capacity args");
  check_result(fixture.scratch.used == 0u && fixture.scratch.node_count == 0u &&
                   fixture.workspace[2] == -99,
               "failed scratch allocation is atomic");

  fixture_init(&fixture, 32u, 0u, 0u, 0u, 8u);
  fixture.workspace[0] = 42;
  fixture.workspace[1] = 1;
  fixture.workspace[2] = -99;
  result =
      al_runtime_make_record(&fixture.context, &test_program, TYPE_PAIR,
                             &fixture.workspace[0], 2u, &fixture.workspace[2]);
  check_result(result == AL_RUNTIME_SCRATCH_CAPACITY,
               "one-short scratch nodes rejected");
  check_result(fixture.context.error_argument0 == 16 &&
                   fixture.context.error_argument1 == 1,
               "scratch node shortage args");

  fixture_init(&fixture, 64u, 4u, 64u, 4u, 8u);
  fixture.workspace[0] = 0;
  result = al_runtime_make_record(&fixture.context, &test_program, TYPE_EMPTY,
                                  NULL, 0u, &fixture.workspace[0]);
  check_result(result == AL_RUNTIME_OK, "build stale-reference graph");
  fixture.context.scratch->generation = 0u;
  check_result(al_runtime_validate_request(&fixture.context, public_roots, 3u,
                                           3u, (int32_t *)&fixture.workspace[4],
                                           8u) == AL_RUNTIME_INVALID_REQUEST,
               "zero owner generation rejected");

  fixture_init(&fixture, 64u, 4u, 64u, 4u, 8u);
  fixture.context.abi_version = 1u;
  fixture.context.scratch = (al_arena *)(uintptr_t)1u;
  fixture.context.retained = (al_arena *)(uintptr_t)2u;
  fixture.context.workspace = (int64_t *)(uintptr_t)3u;
  fixture.context.workspace_capacity = 7u;
  fixture.context.input_owner = (const al_arena *)(uintptr_t)4u;
  fixture.context.input_roots = (const int64_t *)(uintptr_t)5u;
  fixture.context.input_root_type_ids = (const uint32_t *)(uintptr_t)6u;
  fixture.context.input_root_count = 99u;
  {
    int32_t status = 12345;
    result = al_runtime_validate_request(&fixture.context, public_roots, 0u, 3u,
                                         &status, 7u);
    check_result(result == AL_RUNTIME_INVALID_REQUEST && status == 12345,
                 "ABI1 prefix rejected before tail access or writes");
  }

  fixture_init(&fixture, 64u, 4u, 64u, 4u, 8u);
  fixture.context.abi_version = 2u;
  fixture.context.scratch = (al_arena *)(uintptr_t)1u;
  fixture.context.retained = (al_arena *)(uintptr_t)2u;
  fixture.context.workspace = (int64_t *)(uintptr_t)3u;
  fixture.context.workspace_capacity = 7u;
  fixture.context.input_owner = (const al_arena *)(uintptr_t)4u;
  fixture.context.input_roots = (const int64_t *)(uintptr_t)5u;
  fixture.context.input_root_type_ids = (const uint32_t *)(uintptr_t)6u;
  fixture.context.input_root_count = 99u;
  {
    int32_t status = 23456;
    result = al_runtime_validate_request(&fixture.context, public_roots, 0u, 3u,
                                         &status, 7u);
    check_result(result == AL_RUNTIME_INVALID_REQUEST && status == 23456,
                 "ABI2 prefix rejected before v3 tail access or writes");
  }

  fixture_init(&fixture, 64u, 4u, 64u, 4u, 8u);
  result = al_runtime_promote(&fixture.context, &test_program,
                              fixture.workspace, NULL, 0u, public_roots, 3u);
  check_result(result == AL_RUNTIME_OK, "zero-root promotion");
  check_result(bytes_equal((const uint8_t *)public_roots,
                           (const uint8_t *)before,
                           (uint32_t)sizeof(public_roots)),
               "zero-root promotion preserves public outputs");
}

int main(int argc, char **argv) {
  if (argc > 1 && strcmp(argv[1], "--layout") == 0) {
    test_layout();
    return 0;
  }
  if (argc > 1 && strcmp(argv[1], "--layout-json") == 0) {
    test_layout_json();
    return 0;
  }

  test_request_validation();
  test_record_helpers();
  test_promotion();
  test_state_import();
  test_capacities_and_malformed_requests();

  if (failures != 0u) {
    (void)printf("native runtime failures: %u\n", failures);
    return 1;
  }
  (void)printf("native runtime safety checks passed (%u assertions)\n",
               assertions);
  return 0;
}

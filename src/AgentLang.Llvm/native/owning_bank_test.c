#include "owning_bank.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

enum {
  TEST_LAYOUT_INT = 0u,
  TEST_LAYOUT_STRING = 1u,
  TEST_LAYOUT_RECORD = 2u,
  TEST_LAYOUT_EMPTY = 3u,
  TEST_TYPE_INT = 100u,
  TEST_TYPE_STRING = 101u,
  TEST_TYPE_RECORD = 102u,
  TEST_TYPE_EMPTY = 103u,
  TEST_STACK_CAPACITY = 128u,
  TEST_BITMAP_BYTES = TEST_STACK_CAPACITY / 8u,
  TEST_BANK_CAPACITY = 64u
};

static const al_owning_type_descriptor test_types[] = {
    {AL_OWNING_TYPE_I64, TEST_TYPE_INT, 0u, 0u, 8u, 8u, 8u, 8u},
    {AL_OWNING_TYPE_STRING, TEST_TYPE_STRING, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u},
    {AL_OWNING_TYPE_RECORD, TEST_TYPE_RECORD, 0u, 3u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u},
    {AL_OWNING_TYPE_RECORD, TEST_TYPE_EMPTY, 3u, 0u, 0u, 8u, 0u, 8u}};

static const al_owning_field_descriptor test_fields[] = {
    {TEST_LAYOUT_INT, 0u, 0u, 0u},
    {TEST_LAYOUT_STRING, 8u, 0u, 0u},
    {TEST_LAYOUT_EMPTY, AL_OWNING_LAYOUT_DYNAMIC_U32,
     AL_OWNING_FIELD_ZERO_WIDTH, 0u}};

static const al_owning_layout test_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION,
    test_types,
    (uint32_t)(sizeof(test_types) / sizeof(test_types[0])),
    test_fields,
    (uint32_t)(sizeof(test_fields) / sizeof(test_fields[0]))};

typedef struct test_context_storage {
  _Alignas(8) uint8_t stack[TEST_STACK_CAPACITY];
  uint8_t init_bitmap[TEST_BITMAP_BYTES];
  uint8_t poison_bitmap[TEST_BITMAP_BYTES];
  al_owning_stack_context context;
} test_context_storage;

typedef struct test_bank_storage {
  _Alignas(8) uint8_t bank0_bytes[TEST_BANK_CAPACITY];
  al_owning_bank_root bank0_roots[2];
  _Alignas(8) uint8_t bank1_bytes[TEST_BANK_CAPACITY];
  al_owning_bank_root bank1_roots[2];
  al_owning_byte_store store;
} test_bank_storage;

typedef union test_layout_overlap_storage {
  _Alignas(8) uint8_t bytes[TEST_BANK_CAPACITY];
  al_owning_layout layout;
} test_layout_overlap_storage;

static uint32_t checks;
static uint32_t failures;

#define CHECK(name, condition)                                                 \
  do {                                                                         \
    ++checks;                                                                  \
    if (!(condition)) {                                                        \
      ++failures;                                                              \
      printf("FAIL: %s\n", name);                                            \
    }                                                                          \
  } while (0)

static void write_u32_le(uint8_t *bytes, uint32_t offset, uint32_t value) {
  bytes[offset] = (uint8_t)value;
  bytes[offset + 1u] = (uint8_t)(value >> 8u);
  bytes[offset + 2u] = (uint8_t)(value >> 16u);
  bytes[offset + 3u] = (uint8_t)(value >> 24u);
}

static void write_u64_le(uint8_t *bytes, uint32_t offset, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bytes[offset + index] = (uint8_t)(value >> (index * 8u));
}

static void make_record(uint8_t bytes[24]) {
  memset(bytes, 0, 24u);
  write_u64_le(bytes, 0u, UINT64_C(0x123456789abcdef0));
  write_u32_le(bytes, 8u, 2u);
  bytes[16] = 0x41u; /* UTF-16 'A'. */
  bytes[17] = 0x00u;
  bytes[18] = 0xa9u; /* UTF-16 U+03A9. */
  bytes[19] = 0x03u;
}

static int context_init(test_context_storage *storage) {
  memset(storage, 0, sizeof(*storage));
  storage->context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  storage->context.stack_capacity_bytes = TEST_STACK_CAPACITY;
  storage->context.init_bitmap_bytes = TEST_BITMAP_BYTES;
  storage->context.stack_data = storage->stack;
  storage->context.init_bitmap = storage->init_bitmap;
  storage->context.poison_bitmap = storage->poison_bitmap;
  al_owning_begin(&storage->context);
  return storage->context.status == AL_OWNING_STATUS_OK;
}

static int context_load(test_context_storage *storage, const uint8_t *bytes,
                        uint32_t payload_bytes, uint32_t extent_bytes,
                        uint32_t type_id) {
  if (al_owning_reserve_to(&storage->context, extent_bytes, 0u) != 0)
    return 0;
  al_owning_copy_external(&storage->context, 0u, bytes, payload_bytes,
                          extent_bytes, type_id);
  return storage->context.status == AL_OWNING_STATUS_OK;
}

static int store_init(test_bank_storage *storage, uint32_t byte_capacity,
                      uint32_t root_capacity) {
  memset(storage, 0, sizeof(*storage));
  return al_owning_byte_store_init(
             &storage->store, storage->bank0_bytes, byte_capacity,
             storage->bank0_roots, root_capacity, storage->bank1_bytes,
             byte_capacity, storage->bank1_roots, root_capacity) ==
         AL_OWNING_BANK_OK;
}

static int seed_int_bank(al_owning_byte_store *store,
                         test_context_storage *storage, int64_t value) {
  al_owning_bank_stack_slice slice;
  if (!context_init(storage) ||
      al_owning_reserve_to(&storage->context, 8u, 0u) != 0)
    return 0;
  al_owning_store_i64(&storage->context, 0u, value, TEST_TYPE_INT);
  if (storage->context.status != AL_OWNING_STATUS_OK ||
      al_owning_byte_store_begin(store) != AL_OWNING_BANK_OK)
    return 0;
  slice.type_index = TEST_LAYOUT_INT;
  slice.source_offset_bytes = 0u;
  slice.source_owner_end_bytes = 8u;
  slice.reserved = 0u;
  if (al_owning_byte_store_stage_stack_values(store, &storage->context,
                                             &test_layout, &slice, 1u, 0u) !=
          AL_OWNING_BANK_OK ||
      al_owning_byte_store_commit(store) != AL_OWNING_BANK_OK)
    return 0;
  return 1;
}

static void test_storage_validation(void) {
  test_bank_storage storage;
  al_owning_byte_store rejected;
  memset(&storage, 0, sizeof(storage));
  memset(&rejected, 0, sizeof(rejected));
  CHECK("disjoint caller storage initializes",
        al_owning_byte_store_init(
            &storage.store, storage.bank0_bytes, TEST_BANK_CAPACITY,
            storage.bank0_roots, 2u, storage.bank1_bytes,
            TEST_BANK_CAPACITY, storage.bank1_roots, 2u) ==
            AL_OWNING_BANK_OK);
  CHECK("overlapping byte banks are rejected before store mutation",
        al_owning_byte_store_init(
            &rejected, storage.bank0_bytes, TEST_BANK_CAPACITY,
            storage.bank0_roots, 2u, storage.bank0_bytes,
            TEST_BANK_CAPACITY, storage.bank1_roots, 2u) ==
            AL_OWNING_BANK_INVALID_STORAGE &&
            rejected.storage_bytes[0] == NULL);
  CHECK("overlapping byte and descriptor storage is rejected",
        al_owning_byte_store_init(
            &rejected, storage.bank0_bytes, TEST_BANK_CAPACITY,
            (al_owning_bank_root *)(void *)storage.bank0_bytes, 2u,
            storage.bank1_bytes, TEST_BANK_CAPACITY, storage.bank1_roots,
            2u) == AL_OWNING_BANK_INVALID_STORAGE);
}

static void test_layout_overlap_rejected(void) {
  test_context_storage source_storage;
  test_layout_overlap_storage aliased_bank;
  _Alignas(8) uint8_t other_bank[TEST_BANK_CAPACITY];
  uint8_t aliased_before[TEST_BANK_CAPACITY];
  al_owning_bank_root other_roots[2];
  al_owning_bank_root aliased_roots[2];
  al_owning_byte_store store;
  uint8_t record[24];
  al_owning_bank_stack_slice slice;
  const al_owning_byte_bank *active;

  memset(&aliased_bank, 0, sizeof(aliased_bank));
  memset(&store, 0, sizeof(store));
  aliased_bank.layout = test_layout;
  memcpy(aliased_before, aliased_bank.bytes, sizeof(aliased_before));
  CHECK("store accepts its disjoint banks with a descriptor fixture",
        al_owning_byte_store_init(
            &store, other_bank, TEST_BANK_CAPACITY, other_roots, 2u,
            aliased_bank.bytes, TEST_BANK_CAPACITY, aliased_roots, 2u) ==
            AL_OWNING_BANK_OK);
  make_record(record);
  CHECK("layout-overlap source is ready",
        context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&store) == AL_OWNING_BANK_OK);
  active = al_owning_byte_store_active(&store);
  slice = (al_owning_bank_stack_slice){TEST_LAYOUT_RECORD, 0u, 24u, 0u};
  CHECK("layout in bank storage is rejected before metadata or payload writes",
        al_owning_byte_store_stage_stack_values(
            &store, &source_storage.context, &aliased_bank.layout, &slice, 1u,
            0u) == AL_OWNING_BANK_OVERLAP &&
            memcmp(aliased_bank.bytes, aliased_before,
                   sizeof(aliased_before)) == 0 &&
            active == al_owning_byte_store_active(&store));
  CHECK("layout-overlap transaction can abort without publishing",
        al_owning_byte_store_abort(&store) == AL_OWNING_BANK_OK &&
            active == al_owning_byte_store_active(&store));
}

static void test_success_and_source_reset(void) {
  test_bank_storage storage;
  test_context_storage source_storage;
  uint8_t source[24];
  uint8_t source_before[24];
  al_owning_bank_stack_slice slices[2];
  const al_owning_byte_bank *active;
  uint32_t payload_bytes;
  uint32_t extent_bytes;

  make_record(source);
  memcpy(source_before, source, sizeof(source));
  CHECK("store and source context initialize",
        store_init(&storage, TEST_BANK_CAPACITY, 2u) &&
            context_init(&source_storage));
  CHECK("record is copied into the working arena",
        context_load(&source_storage, source, 20u, 24u, TEST_TYPE_RECORD));

  CHECK("transaction begins in the inactive bank",
        al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  slices[0].type_index = TEST_LAYOUT_RECORD;
  slices[0].source_offset_bytes = 0u;
  slices[0].source_owner_end_bytes = 24u;
  slices[0].reserved = 0u;
  slices[1].type_index = TEST_LAYOUT_STRING;
  slices[1].source_offset_bytes = 8u;
  slices[1].source_owner_end_bytes = 24u;
  slices[1].reserved = 0u;
  CHECK("whole root batch stages as independent values",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, slices, 2u,
            0u) == AL_OWNING_BANK_OK);
  CHECK("staging copies full extents including headers and padding",
        source_storage.context.retained_copy_bytes == 40u &&
            storage.store.banks[1].used_bytes == 40u &&
            storage.store.banks[1].root_count == 2u);
  CHECK("projected string receives a self-contained bank range",
        storage.bank1_roots[0].type_id == TEST_TYPE_RECORD &&
            storage.bank1_roots[0].offset_bytes == 0u &&
            storage.bank1_roots[0].extent_bytes == 24u &&
            storage.bank1_roots[0].payload_bytes == 20u &&
            storage.bank1_roots[0].owner_end_bytes == 24u &&
            storage.bank1_roots[1].type_id == TEST_TYPE_STRING &&
            storage.bank1_roots[1].offset_bytes == 24u &&
            storage.bank1_roots[1].extent_bytes == 16u &&
            storage.bank1_roots[1].payload_bytes == 12u &&
            storage.bank1_roots[1].owner_end_bytes == 40u);
  CHECK("publishing never mutates the stack source",
        memcmp(source_storage.stack, source_before, sizeof(source)) == 0 &&
            memcmp(source, source_before, sizeof(source)) == 0);
  CHECK("commit publishes the complete bank and exact copy count",
        al_owning_byte_store_commit(&storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_last_committed_copy_bytes(&storage.store) ==
                40u);

  al_owning_release_to(&source_storage.context, 0u, 0u, 0u, 0u);
  CHECK("source arena is poisoned/reset independently",
        source_storage.context.cursor_bytes == 0u &&
            source_storage.stack[0] == 0xa5u);
  al_owning_begin(&source_storage.context);
  active = al_owning_byte_store_active(&storage.store);
  CHECK("published bank remains addressable after source reset",
        active != NULL && active->used_bytes == 40u && active->root_count == 2u &&
            active->bytes[0] == source_before[0] &&
            active->bytes[24u + 8u] == 0x41u &&
            active->bytes[24u + 10u] == 0xa9u &&
            active->bytes[24u + 11u] == 0x03u);
  CHECK("record root still validates from bank bytes",
        al_owning_measure_external_value(
            &source_storage.context, &test_layout, TEST_LAYOUT_RECORD,
            active->bytes, active->used_bytes,
            active->roots[0].offset_bytes, 0u, &payload_bytes,
            &extent_bytes) == 0 &&
            payload_bytes == 20u && extent_bytes == 24u);
    CHECK("independent projected string still validates from bank bytes",
        al_owning_measure_external_value(
            &source_storage.context, &test_layout, TEST_LAYOUT_STRING,
            active->bytes, active->used_bytes,
            active->roots[1].offset_bytes, 0u, &payload_bytes,
            &extent_bytes) == 0 &&
            payload_bytes == 12u && extent_bytes == 16u);
}

static void test_empty_publication_boundary(void) {
  test_bank_storage storage;
  test_context_storage source_storage;
  uint8_t standalone_empty[8] = {0u};
  uint8_t record[24];
  uint8_t staging_before[TEST_BANK_CAPACITY];
  al_owning_bank_stack_slice slice;
  const al_owning_byte_bank *active;

  CHECK("empty store and source initialize",
        store_init(&storage, TEST_BANK_CAPACITY, 2u) &&
            context_init(&source_storage));
  CHECK("standalone Empty has the existing eight-byte token",
        context_load(&source_storage, standalone_empty, 0u, 8u,
                     TEST_TYPE_EMPTY) &&
            al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  slice = (al_owning_bank_stack_slice){TEST_LAYOUT_EMPTY, 0u, 8u, 0u};
  CHECK("standalone Empty token is independently publishable",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, &slice, 1u,
            0u) == AL_OWNING_BANK_OK &&
            storage.store.banks[1].used_bytes == 8u &&
            storage.store.banks[1].roots[0].payload_bytes == 0u &&
            storage.store.banks[1].roots[0].extent_bytes == 8u &&
            al_owning_byte_store_commit(&storage.store) == AL_OWNING_BANK_OK);

  make_record(record);
  CHECK("nested Empty field remains zero-width inside its parent",
        context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  memset(storage.bank0_bytes, 0x6d, sizeof(storage.bank0_bytes));
  memcpy(staging_before, storage.bank0_bytes, sizeof(staging_before));
  active = al_owning_byte_store_active(&storage.store);
  slice = (al_owning_bank_stack_slice){TEST_LAYOUT_EMPTY, 24u, 24u, 0u};
  CHECK("zero-width nested Empty cannot be published as an independent root",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, &slice, 1u,
            0u) == AL_OWNING_BANK_INVALID_VALUE &&
            memcmp(storage.bank0_bytes, staging_before,
                   sizeof(staging_before)) == 0 &&
            active == al_owning_byte_store_active(&storage.store));
  CHECK("failed nested Empty projection preserves prior standalone result",
        active->root_count == 1u && active->used_bytes == 8u &&
            active->roots[0].type_id == TEST_TYPE_EMPTY &&
            al_owning_byte_store_abort(&storage.store) == AL_OWNING_BANK_OK);
}

static void test_prevalidation_and_abort(void) {
  test_bank_storage storage;
  test_context_storage source_storage;
  uint8_t record[24];
  uint8_t staged_before[TEST_BANK_CAPACITY];
  uint8_t active_before[TEST_BANK_CAPACITY];
  al_owning_bank_stack_slice slices[2];
  const al_owning_byte_bank *active;

  make_record(record);
  CHECK("invalid-slice store initializes with prior active value",
        store_init(&storage, TEST_BANK_CAPACITY, 2u) &&
            seed_int_bank(&storage.store, &source_storage, 77));
  CHECK("second transaction uses old bank only as staging",
        context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  memset(storage.bank0_bytes, 0xcc, sizeof(storage.bank0_bytes));
  memcpy(staged_before, storage.bank0_bytes, sizeof(staged_before));
  active = al_owning_byte_store_active(&storage.store);
  memcpy(active_before, active->bytes, active->used_bytes);
  slices[0].type_index = TEST_LAYOUT_RECORD;
  slices[0].source_offset_bytes = 0u;
  slices[0].source_owner_end_bytes = 24u;
  slices[0].reserved = 0u;
  slices[1].type_index = TEST_LAYOUT_STRING;
  slices[1].source_offset_bytes = 120u;
  slices[1].source_owner_end_bytes = 128u;
  slices[1].reserved = 0u;
  CHECK("invalid later slice rejects whole batch before payload writes",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, slices, 2u,
            0u) == AL_OWNING_BANK_INVALID_VALUE &&
            storage.store.banks[0].used_bytes == 0u &&
            storage.store.banks[0].root_count == 0u &&
            memcmp(storage.bank0_bytes, staged_before,
                   sizeof(staged_before)) == 0);
  CHECK("invalid later slice leaves active bytes and roots unchanged",
        active == al_owning_byte_store_active(&storage.store) &&
            active->root_count == 1u && active->used_bytes == 8u &&
            memcmp(active->bytes, active_before, active->used_bytes) == 0);
  CHECK("abort closes failed transaction without touching active value",
        al_owning_byte_store_abort(&storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_active(&storage.store)->root_count == 1u &&
            al_owning_byte_store_active(&storage.store)->used_bytes == 8u);
}

static void test_capacity_failures(void) {
  test_bank_storage small_storage;
  test_bank_storage root_storage;
  test_context_storage source_storage;
  uint8_t record[24];
  uint8_t small_before[TEST_BANK_CAPACITY];
  uint8_t roots_before[TEST_BANK_CAPACITY];
  al_owning_bank_stack_slice slices[2];
  const al_owning_byte_bank *active;

  make_record(record);
  CHECK("small-byte store initializes with active seed",
        store_init(&small_storage, 32u, 2u) &&
            seed_int_bank(&small_storage.store, &source_storage, 91));
  CHECK("byte-capacity preflight context is ready",
        context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&small_storage.store) ==
                AL_OWNING_BANK_OK);
  memset(small_storage.bank0_bytes, 0x5a, sizeof(small_storage.bank0_bytes));
  memcpy(small_before, small_storage.bank0_bytes, sizeof(small_before));
  active = al_owning_byte_store_active(&small_storage.store);
  slices[0] = (al_owning_bank_stack_slice){TEST_LAYOUT_RECORD, 0u, 24u, 0u};
  slices[1] = (al_owning_bank_stack_slice){TEST_LAYOUT_STRING, 8u, 24u, 0u};
  CHECK("byte-capacity failure performs no payload copy",
        al_owning_byte_store_stage_stack_values(
            &small_storage.store, &source_storage.context, &test_layout,
            slices, 2u, 0u) == AL_OWNING_BANK_BYTE_CAPACITY &&
            small_storage.store.banks[0].used_bytes == 0u &&
            small_storage.store.banks[0].root_count == 0u &&
            memcmp(small_storage.bank0_bytes, small_before,
                   sizeof(small_before)) == 0 &&
            active == al_owning_byte_store_active(&small_storage.store));
  CHECK("byte-capacity transaction can abort with seed intact",
        al_owning_byte_store_abort(&small_storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_active(&small_storage.store)->root_count == 1u &&
            al_owning_byte_store_active(&small_storage.store)->bytes[0] == 91u);

  CHECK("root-capacity store initializes with active seed",
        store_init(&root_storage, TEST_BANK_CAPACITY, 1u) &&
            seed_int_bank(&root_storage.store, &source_storage, 19));
  CHECK("root-capacity transaction starts",
        context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&root_storage.store) ==
                AL_OWNING_BANK_OK);
  memset(root_storage.bank0_bytes, 0x3c, sizeof(root_storage.bank0_bytes));
  memcpy(roots_before, root_storage.bank0_bytes, sizeof(roots_before));
  active = al_owning_byte_store_active(&root_storage.store);
  CHECK("root-capacity failure prevalidates before payload copy",
        al_owning_byte_store_stage_stack_values(
            &root_storage.store, &source_storage.context, &test_layout, slices,
            2u, 0u) == AL_OWNING_BANK_ROOT_CAPACITY &&
            root_storage.store.banks[0].used_bytes == 0u &&
            root_storage.store.banks[0].root_count == 0u &&
            memcmp(root_storage.bank0_bytes, roots_before,
                   sizeof(roots_before)) == 0 &&
            active == al_owning_byte_store_active(&root_storage.store));
  CHECK("root-capacity failure leaves seed published",
        al_owning_byte_store_abort(&root_storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_active(&root_storage.store)->root_count == 1u &&
            al_owning_byte_store_active(&root_storage.store)->bytes[0] == 19u);
}

static void test_slice_overlap_and_transaction_state(void) {
  test_bank_storage storage;
  test_context_storage source_storage;
  uint8_t record[24];
  al_owning_bank_stack_slice slice;
  const al_owning_byte_bank *active;

  make_record(record);
  CHECK("overlap test store initializes",
        store_init(&storage, TEST_BANK_CAPACITY, 2u) &&
            context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD));
  CHECK("open transaction rejects nested begin",
        al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_begin(&storage.store) ==
                AL_OWNING_BANK_BUSY);
  slice = (al_owning_bank_stack_slice){TEST_LAYOUT_RECORD, 0u, 24u, 1u};
  CHECK("nonzero source reserved field is rejected",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, &slice, 1u,
            0u) == AL_OWNING_BANK_INVALID_VALUE &&
            storage.store.banks[1].used_bytes == 0u);
  CHECK("abort makes the inactive bank reusable",
        al_owning_byte_store_abort(&storage.store) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  active = al_owning_byte_store_active(&storage.store);
  CHECK("commit without a complete batch is rejected",
        al_owning_byte_store_commit(&storage.store) == AL_OWNING_BANK_BUSY &&
            active == al_owning_byte_store_active(&storage.store));
  CHECK("open transaction aborts cleanly",
        al_owning_byte_store_abort(&storage.store) == AL_OWNING_BANK_OK);
}

static void test_trim_last_root(void) {
  test_bank_storage storage;
  test_context_storage source_storage;
  uint8_t record[24];
  uint8_t active_before[TEST_BANK_CAPACITY];
  al_owning_bank_root roots_before[2];
  al_owning_bank_stack_slice slices[2];
  const al_owning_byte_bank *active;
  al_owning_bank_root *mutable_roots;

  make_record(record);
  CHECK("trim store and source initialize",
        store_init(&storage, TEST_BANK_CAPACITY, 2u) &&
            context_init(&source_storage) &&
            context_load(&source_storage, record, 20u, 24u,
                         TEST_TYPE_RECORD) &&
            al_owning_byte_store_begin(&storage.store) == AL_OWNING_BANK_OK);
  slices[0] = (al_owning_bank_stack_slice){TEST_LAYOUT_RECORD, 0u, 24u, 0u};
  slices[1] = (al_owning_bank_stack_slice){TEST_LAYOUT_STRING, 8u, 24u, 0u};
  CHECK("trim fixture publishes State and Continuation roots",
        al_owning_byte_store_stage_stack_values(
            &storage.store, &source_storage.context, &test_layout, slices, 2u,
            0u) == AL_OWNING_BANK_OK &&
            al_owning_byte_store_commit(&storage.store) == AL_OWNING_BANK_OK);
  active = al_owning_byte_store_active(&storage.store);
  CHECK("trim fixture has two contiguous roots",
        active != NULL && active->root_count == 2u &&
            active->used_bytes == 40u &&
            active->byte_capacity == sizeof(active_before));
  memcpy(active_before, active->bytes, active->byte_capacity);
  memcpy(roots_before, active->roots, sizeof(roots_before));
  mutable_roots = storage.store.storage_roots[storage.store.active_index];
  mutable_roots[1].owner_end_bytes -= 1u;
  {
    al_owning_bank_root corrupt_roots[2];
    memcpy(corrupt_roots, mutable_roots, sizeof(corrupt_roots));
    CHECK("invalid tail rejects trim without changing bank bytes or metadata",
          al_owning_byte_store_trim_last_root(&storage.store) ==
                  AL_OWNING_BANK_INVALID_VALUE &&
              active == al_owning_byte_store_active(&storage.store) &&
              active->used_bytes == 40u &&
              active->root_count == 2u &&
              memcmp(active->bytes, active_before,
                     active->byte_capacity) == 0 &&
              memcmp(active->roots, corrupt_roots,
                     sizeof(corrupt_roots)) == 0);
  }
  memcpy(mutable_roots, roots_before, sizeof(roots_before));
  CHECK("valid trim preserves prefix and removes only the last root",
        al_owning_byte_store_trim_last_root(&storage.store) ==
                AL_OWNING_BANK_OK &&
            active == al_owning_byte_store_active(&storage.store) &&
            active->used_bytes == roots_before[0].owner_end_bytes &&
            active->root_count == 1u &&
            memcmp(active->bytes, active_before,
                   active->byte_capacity) == 0 &&
            memcmp(&active->roots[1], &(al_owning_bank_root){0},
                   sizeof(al_owning_bank_root)) == 0);
}

int main(void) {
  test_storage_validation();
  test_layout_overlap_rejected();
  test_success_and_source_reset();
  test_empty_publication_boundary();
  test_prevalidation_and_abort();
  test_capacity_failures();
  test_slice_overlap_and_transaction_state();
  test_trim_last_root();
  if (failures != 0u) {
    printf("owning bank: %u/%u checks failed\n", failures, checks);
    return 1;
  }
  printf("owning bank: %u checks passed\n", checks);
  return 0;
}

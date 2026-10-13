#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "../../src/AgentLang.Llvm/native/owning_stack_runtime.c"

#define AL_TEST_BITMAP_BYTES 40u
#define AL_TEST_STACK_CAPACITY 256u
#define AL_TEST_GUARD_BYTES 8u
#define AL_TEST_STORAGE_BYTES (AL_TEST_STACK_CAPACITY + 2u * AL_TEST_GUARD_BYTES)
#define AL_TEST_FULL_CAPACITY_BYTES (4u * 1024u * 1024u)
#define AL_TEST_FULL_BITMAP_BYTES (AL_TEST_FULL_CAPACITY_BYTES / 8u)
#define AL_TEST_LARGE_GUARDED_BITMAP_BYTES \
  (AL_TEST_FULL_BITMAP_BYTES + 2u * AL_TEST_GUARD_BYTES)
#define AL_TEST_LARGE_GUARDED_STORAGE_BYTES \
  (AL_TEST_FULL_CAPACITY_BYTES + 2u * AL_TEST_GUARD_BYTES)

static uint8_t large_expected_init[AL_TEST_LARGE_GUARDED_BITMAP_BYTES];
static uint8_t large_expected_poison[AL_TEST_LARGE_GUARDED_BITMAP_BYTES];
static uint8_t large_actual_init[AL_TEST_LARGE_GUARDED_BITMAP_BYTES];
static uint8_t large_actual_poison[AL_TEST_LARGE_GUARDED_BITMAP_BYTES];
static uint8_t large_expected_storage[AL_TEST_LARGE_GUARDED_STORAGE_BYTES];
static uint8_t large_actual_storage[AL_TEST_LARGE_GUARDED_STORAGE_BYTES];

static void reference_bit_set(uint8_t *bits, uint32_t index, int32_t value) {
  const uint8_t mask = (uint8_t)(1u << (index & 7u));
  if (value)
    bits[index >> 3u] = (uint8_t)(bits[index >> 3u] | mask);
  else
    bits[index >> 3u] = (uint8_t)(bits[index >> 3u] & (uint8_t)~mask);
}

static void reference_mark(uint8_t *init_bitmap, uint8_t *poison_bitmap,
                           uint32_t offset, uint32_t byte_count,
                           int32_t initialized, int32_t poisoned) {
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    reference_bit_set(init_bitmap, offset + index, initialized);
    if (poisoned)
      reference_bit_set(poison_bitmap, offset + index, 1);
    else if (!initialized)
      reference_bit_set(poison_bitmap, offset + index, 0);
  }
}

static int32_t reference_range_valid(const al_owning_stack_context *ctx,
                                     uint32_t offset, uint32_t byte_count) {
  return offset <= ctx->stack_capacity_bytes &&
         byte_count <= ctx->stack_capacity_bytes - offset;
}

static void reference_fill(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t byte_count, uint8_t value) {
  uint32_t index;
  if (!reference_range_valid(ctx, offset, byte_count)) {
    if (ctx->status == AL_OWNING_STATUS_OK) {
      ctx->status = AL_OWNING_STATUS_INTERNAL;
      ctx->error_id = 0u;
      ctx->required_bytes = offset + byte_count;
      ctx->available_bytes = ctx->stack_capacity_bytes;
      ctx->diagnostic_float_bits = 0u;
    }
    return;
  }
  for (index = 0u; index < byte_count; ++index)
    ctx->stack_data[offset + index] = value;
}

static void initialize_bytes(uint8_t *bytes, uint32_t count, uint32_t salt) {
  uint32_t index;
  for (index = 0u; index < count; ++index)
    bytes[index] = (uint8_t)(index * 37u + salt * 53u + 0x6du);
}

static int32_t same_bytes(const uint8_t *left, const uint8_t *right,
                          uint32_t count) {
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (left[index] != right[index]) return 0;
  }
  return 1;
}

static uint32_t touched_bitmap_bytes(uint32_t offset, uint32_t byte_count) {
  const uint64_t last_bit = (uint64_t)offset + byte_count - 1u;
  if (byte_count == 0u) return 0u;
  return (uint32_t)(last_bit >> 3u) - (offset >> 3u) + 1u;
}

static int32_t test_mark_ranges(void) {
  uint8_t expected_left[AL_TEST_BITMAP_BYTES];
  uint8_t expected_right[AL_TEST_BITMAP_BYTES];
  uint8_t actual_left[AL_TEST_BITMAP_BYTES];
  uint8_t actual_right[AL_TEST_BITMAP_BYTES];
  uint64_t cases = 0u;
  uint32_t offset, byte_count;
  int32_t initialized, poisoned;

  for (offset = 0u; offset <= 128u; ++offset) {
    for (byte_count = 0u; byte_count <= 128u; ++byte_count) {
      for (initialized = 0; initialized <= 1; ++initialized) {
        for (poisoned = 0; poisoned <= 1; ++poisoned) {
          uint32_t alias_mode;
          for (alias_mode = 0u; alias_mode < 4u; ++alias_mode) {
            al_owning_stack_context context;
            int32_t can_batch;
            initialize_bytes(expected_left, AL_TEST_BITMAP_BYTES, 1u);
            initialize_bytes(expected_right, AL_TEST_BITMAP_BYTES, 2u);
            memcpy(actual_left, expected_left, sizeof(actual_left));
            memcpy(actual_right, expected_right, sizeof(actual_right));
            memset(&context, 0, sizeof(context));
            switch (alias_mode) {
            case 0u:
              context.init_bitmap = actual_left;
              context.poison_bitmap = actual_right;
              can_batch = byte_count == 0u || al_owning_mark_can_batch(
                  &context, offset, byte_count);
              reference_mark(expected_left, expected_right, offset, byte_count,
                             initialized, poisoned);
              break;
            case 1u:
              context.init_bitmap = actual_left;
              context.poison_bitmap = actual_left;
              can_batch = byte_count == 0u || al_owning_mark_can_batch(
                  &context, offset, byte_count);
              reference_mark(expected_left, expected_left, offset, byte_count,
                             initialized, poisoned);
              break;
            case 2u:
              context.init_bitmap = actual_left;
              context.poison_bitmap = actual_left + 1u;
              can_batch = byte_count == 0u || al_owning_mark_can_batch(
                  &context, offset, byte_count);
              reference_mark(expected_left, expected_left + 1u, offset,
                             byte_count, initialized, poisoned);
              break;
            default:
              context.init_bitmap = actual_left + 1u;
              context.poison_bitmap = actual_left;
              can_batch = byte_count == 0u || al_owning_mark_can_batch(
                  &context, offset, byte_count);
              reference_mark(expected_left + 1u, expected_left, offset,
                             byte_count, initialized, poisoned);
              break;
            }
            al_owning_mark(&context, offset, byte_count, initialized,
                           poisoned);
            ++cases;
            if (alias_mode == 0u) {
              if (!same_bytes(expected_left, actual_left,
                              AL_TEST_BITMAP_BYTES) ||
                  !same_bytes(expected_right, actual_right,
                              AL_TEST_BITMAP_BYTES))
                return 0;
            } else if (!same_bytes(expected_left, actual_left,
                                   AL_TEST_BITMAP_BYTES)) {
              return 0;
            }
            if (byte_count != 0u) {
              const int32_t overlap =
                  alias_mode == 1u ||
                  ((alias_mode == 2u || alias_mode == 3u) &&
                   touched_bitmap_bytes(offset, byte_count) > 1u);
              if (can_batch == overlap) return 0;
            }
          }
        }
      }
    }
  }

  for (initialized = 0; initialized <= 1; ++initialized) {
    for (poisoned = 0; poisoned <= 1; ++poisoned) {
      al_owning_stack_context expected_context, actual_context;
      uint8_t expected_poison[AL_TEST_BITMAP_BYTES];
      uint8_t actual_poison[AL_TEST_BITMAP_BYTES];
      const uint32_t offset =
          (uint32_t)(offsetof(al_owning_stack_context, trace_event_count) * 8u) +
          2u;
      memset(&expected_context, 0, sizeof(expected_context));
      memset(&actual_context, 0, sizeof(actual_context));
      expected_context.trace_event_count = 0x12345678u;
      actual_context.trace_event_count = 0x12345678u;
      initialize_bytes(expected_poison, AL_TEST_BITMAP_BYTES, 3u);
      memcpy(actual_poison, expected_poison, sizeof(actual_poison));
      expected_context.init_bitmap = (uint8_t *)&expected_context;
      expected_context.poison_bitmap = expected_poison;
      actual_context.init_bitmap = (uint8_t *)&actual_context;
      actual_context.poison_bitmap = actual_poison;
      if (al_owning_mark_can_batch(&actual_context, offset, 12u)) return 0;
      reference_mark((uint8_t *)&expected_context, expected_poison, offset,
                     12u, initialized, poisoned);
      al_owning_mark(&actual_context, offset, 12u, initialized, poisoned);
      ++cases;
      if (expected_context.trace_event_count !=
              actual_context.trace_event_count ||
          !same_bytes(expected_poison, actual_poison,
                      AL_TEST_BITMAP_BYTES))
        return 0;
    }
  }

  {
    al_owning_stack_context context;
    uint8_t init_bitmap[AL_TEST_BITMAP_BYTES] = {0};
    uint8_t poison_bitmap[AL_TEST_BITMAP_BYTES] = {0};
    memset(&context, 0, sizeof(context));
    context.init_bitmap = init_bitmap;
    context.poison_bitmap = poison_bitmap;
    if (al_owning_mark_can_batch(&context, UINT32_MAX - 1u, 3u)) return 0;
    context.init_bitmap = (uint8_t *)(~(uintptr_t)0 - 1u);
    if (al_owning_mark_can_batch(&context, 0u, 16u)) return 0;
    if (!al_owning_address_ranges_overlap((uintptr_t)(~(uintptr_t)0 - 1u),
                                          4u, (uintptr_t)&context,
                                          sizeof(context)))
      return 0;
    if (al_owning_address_ranges_overlap(100u, 4u, 104u, 2u) ||
        !al_owning_address_ranges_overlap(100u, 5u, 104u, 2u) ||
        al_owning_address_ranges_overlap(100u, 0u, 100u, 1u))
      return 0;
  }
  return cases == 266260u;
}

static int32_t same_fill_context_state(const al_owning_stack_context *left,
                                       const al_owning_stack_context *right) {
  return left->status == right->status && left->error_id == right->error_id &&
         left->required_bytes == right->required_bytes &&
         left->available_bytes == right->available_bytes &&
         left->trace_event_count == right->trace_event_count &&
         left->diagnostic_float_bits == right->diagnostic_float_bits;
}

static int32_t test_fill_ranges(void) {
  static const uint8_t values[] = {0x00u, 0xa5u, 0x5au, 0xffu};
  uint8_t expected[AL_TEST_STORAGE_BYTES];
  uint8_t actual[AL_TEST_STORAGE_BYTES];
  uint64_t cases = 0u;
  uint32_t offset, byte_count, value_index;

  for (offset = 0u; offset <= 128u; ++offset) {
    for (byte_count = 0u; byte_count <= 128u; ++byte_count) {
      for (value_index = 0u; value_index < sizeof(values); ++value_index) {
        al_owning_stack_context expected_context, actual_context;
        initialize_bytes(expected, AL_TEST_STORAGE_BYTES, offset + byte_count +
                         value_index);
        memcpy(actual, expected, sizeof(actual));
        memset(&expected_context, 0, sizeof(expected_context));
        memset(&actual_context, 0, sizeof(actual_context));
        expected_context.stack_capacity_bytes = AL_TEST_STACK_CAPACITY;
        actual_context.stack_capacity_bytes = AL_TEST_STACK_CAPACITY;
        expected_context.stack_data = expected + AL_TEST_GUARD_BYTES;
        actual_context.stack_data = actual + AL_TEST_GUARD_BYTES;
        if (byte_count != 0u &&
            !al_owning_fill_cache_allowed(&actual_context, offset,
                                          byte_count))
          return 0;
        reference_fill(&expected_context, offset, byte_count,
                       values[value_index]);
        al_owning_fill(&actual_context, offset, byte_count,
                       values[value_index]);
        ++cases;
        if (!same_bytes(expected, actual, AL_TEST_STORAGE_BYTES) ||
            !same_fill_context_state(&expected_context, &actual_context))
          return 0;
      }
    }
  }

  for (value_index = 0u; value_index < sizeof(values); ++value_index) {
    const uint32_t invalid_offsets[] = {255u, UINT32_MAX};
    const uint32_t invalid_counts[] = {2u, 2u};
    uint32_t invalid_index;
    for (invalid_index = 0u; invalid_index < 2u; ++invalid_index) {
      al_owning_stack_context expected_context, actual_context;
      initialize_bytes(expected, AL_TEST_STORAGE_BYTES, 91u + value_index);
      memcpy(actual, expected, sizeof(actual));
      memset(&expected_context, 0, sizeof(expected_context));
      memset(&actual_context, 0, sizeof(actual_context));
      expected_context.stack_capacity_bytes = AL_TEST_STACK_CAPACITY;
      actual_context.stack_capacity_bytes = AL_TEST_STACK_CAPACITY;
      expected_context.stack_data = expected + AL_TEST_GUARD_BYTES;
      actual_context.stack_data = actual + AL_TEST_GUARD_BYTES;
      reference_fill(&expected_context, invalid_offsets[invalid_index],
                     invalid_counts[invalid_index], values[value_index]);
      al_owning_fill(&actual_context, invalid_offsets[invalid_index],
                     invalid_counts[invalid_index], values[value_index]);
      ++cases;
      if (!same_bytes(expected, actual, AL_TEST_STORAGE_BYTES) ||
          !same_fill_context_state(&expected_context, &actual_context))
        return 0;
    }
  }

  for (value_index = 0u; value_index < sizeof(values); ++value_index) {
    al_owning_stack_context expected_context, actual_context;
    const uint32_t offset =
        (uint32_t)offsetof(al_owning_stack_context, trace_event_count) + 1u;
    memset(&expected_context, 0, sizeof(expected_context));
    memset(&actual_context, 0, sizeof(actual_context));
    expected_context.trace_event_count = 0x12345678u;
    actual_context.trace_event_count = 0x12345678u;
    expected_context.stack_capacity_bytes = (uint32_t)sizeof(expected_context);
    actual_context.stack_capacity_bytes = (uint32_t)sizeof(actual_context);
    expected_context.stack_data = (uint8_t *)&expected_context;
    actual_context.stack_data = (uint8_t *)&actual_context;
    if (al_owning_fill_cache_allowed(&actual_context, offset, 2u)) return 0;
    reference_fill(&expected_context, offset, 2u, values[value_index]);
    al_owning_fill(&actual_context, offset, 2u, values[value_index]);
    ++cases;
    if (!same_fill_context_state(&expected_context, &actual_context)) return 0;
  }

  {
    al_owning_stack_context context;
    memset(&context, 0, sizeof(context));
    context.stack_capacity_bytes = 16u;
    context.stack_data = (uint8_t *)(~(uintptr_t)0 - 1u);
    if (al_owning_fill_cache_allowed(&context, 2u, 1u)) return 0;
    context.stack_data = (uint8_t *)(~(uintptr_t)0 - 4u);
    if (al_owning_fill_cache_allowed(&context, 0u, 8u)) return 0;
  }
  return cases == 66576u;
}

static int32_t test_large_ranges(void) {
  static const uint32_t mark_offsets[] = {
      0u, 1u, AL_TEST_FULL_CAPACITY_BYTES - 17u};
  static const uint32_t mark_counts[] = {
      AL_TEST_FULL_CAPACITY_BYTES, AL_TEST_FULL_CAPACITY_BYTES - 2u, 17u};
  static const int32_t mark_initialized[] = {1, 0, 1};
  static const int32_t mark_poisoned[] = {0, 1, 1};
  static const uint32_t fill_offsets[] = {
      0u, 1u, AL_TEST_FULL_CAPACITY_BYTES - 17u};
  static const uint32_t fill_counts[] = {
      AL_TEST_FULL_CAPACITY_BYTES, AL_TEST_FULL_CAPACITY_BYTES - 2u, 17u};
  static const uint8_t fill_values[] = {0xa5u, 0x5au, 0x00u};
  uint32_t index;

  for (index = 0u; index < 3u; ++index) {
    al_owning_stack_context context;
    initialize_bytes(large_expected_init, AL_TEST_LARGE_GUARDED_BITMAP_BYTES,
                     index + 11u);
    initialize_bytes(large_expected_poison,
                     AL_TEST_LARGE_GUARDED_BITMAP_BYTES, index + 29u);
    memcpy(large_actual_init, large_expected_init,
           sizeof(large_actual_init));
    memcpy(large_actual_poison, large_expected_poison,
           sizeof(large_actual_poison));
    memset(&context, 0, sizeof(context));
    context.init_bitmap = large_actual_init + AL_TEST_GUARD_BYTES;
    context.poison_bitmap = large_actual_poison + AL_TEST_GUARD_BYTES;
    if (!al_owning_mark_can_batch(&context, mark_offsets[index],
                                  mark_counts[index]))
      return 0;
    reference_mark(large_expected_init + AL_TEST_GUARD_BYTES,
                   large_expected_poison + AL_TEST_GUARD_BYTES,
                   mark_offsets[index], mark_counts[index],
                   mark_initialized[index], mark_poisoned[index]);
    al_owning_mark(&context, mark_offsets[index], mark_counts[index],
                   mark_initialized[index], mark_poisoned[index]);
    if (!same_bytes(large_expected_init, large_actual_init,
                    AL_TEST_LARGE_GUARDED_BITMAP_BYTES) ||
        !same_bytes(large_expected_poison, large_actual_poison,
                    AL_TEST_LARGE_GUARDED_BITMAP_BYTES))
      return 0;
  }

  for (index = 0u; index < 3u; ++index) {
    al_owning_stack_context expected_context, actual_context;
    initialize_bytes(large_expected_storage,
                     AL_TEST_LARGE_GUARDED_STORAGE_BYTES, index + 43u);
    memcpy(large_actual_storage, large_expected_storage,
           sizeof(large_actual_storage));
    memset(&expected_context, 0, sizeof(expected_context));
    memset(&actual_context, 0, sizeof(actual_context));
    expected_context.stack_capacity_bytes = AL_TEST_FULL_CAPACITY_BYTES;
    actual_context.stack_capacity_bytes = AL_TEST_FULL_CAPACITY_BYTES;
    expected_context.stack_data =
        large_expected_storage + AL_TEST_GUARD_BYTES;
    actual_context.stack_data = large_actual_storage + AL_TEST_GUARD_BYTES;
    if (!al_owning_fill_cache_allowed(&actual_context, fill_offsets[index],
                                      fill_counts[index]))
      return 0;
    reference_fill(&expected_context, fill_offsets[index], fill_counts[index],
                   fill_values[index]);
    al_owning_fill(&actual_context, fill_offsets[index], fill_counts[index],
                   fill_values[index]);
    if (!same_bytes(large_expected_storage, large_actual_storage,
                    AL_TEST_LARGE_GUARDED_STORAGE_BYTES) ||
        !same_fill_context_state(&expected_context, &actual_context))
      return 0;
  }
  return 1;
}

int main(void) {
  if (!test_mark_ranges()) {
    (void)fprintf(stderr, "owning stack bitmap range differential failed\n");
    return 1;
  }
  if (!test_fill_ranges()) {
    (void)fprintf(stderr, "owning stack fill range differential failed\n");
    return 2;
  }
  if (!test_large_ranges()) {
    (void)fprintf(stderr, "owning stack large range differential failed\n");
    return 3;
  }
  (void)printf(
      "owning_stack_runtime_ranges_v1=passed mark_cases=266260 fill_cases=66576 "
      "large_mark_cases=3 large_fill_cases=3 "
      "mark_aliases=distinct,same,shifted-plus-one,shifted-minus-one "
      "ctx-overlap=checked invalid-ranges=checked pointer-overflow=checked\n");
  return 0;
}

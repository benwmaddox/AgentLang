#include "owning_bank.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

typedef struct al_owning_bank_span {
  uintptr_t begin;
  uintptr_t end;
} al_owning_bank_span;

static int al_owning_bank_make_span(const void *pointer, size_t bytes,
                                    al_owning_bank_span *out_span) {
  uintptr_t begin;
  if (out_span == NULL)
    return 0;
  if (bytes == 0u) {
    out_span->begin = 0u;
    out_span->end = 0u;
    return 1;
  }
  if (pointer == NULL)
    return 0;
  begin = (uintptr_t)pointer;
  if (bytes > (size_t)(UINTPTR_MAX - begin))
    return 0;
  out_span->begin = begin;
  out_span->end = begin + bytes;
  return 1;
}

static int al_owning_bank_spans_overlap(const al_owning_bank_span *left,
                                        const al_owning_bank_span *right) {
  if (left->begin == left->end || right->begin == right->end)
    return 0;
  return left->begin < right->end && right->begin < left->end;
}

static int al_owning_bank_span_overlaps_region(const al_owning_bank_span *span,
                                               const void *pointer,
                                               size_t bytes) {
  al_owning_bank_span region;
  if (!al_owning_bank_make_span(pointer, bytes, &region))
    return 1;
  return al_owning_bank_spans_overlap(span, &region);
}

static int al_owning_bank_pointer_aligned(const void *pointer,
                                          uintptr_t alignment) {
  return pointer != NULL && alignment != 0u &&
         (((uintptr_t)pointer & (alignment - 1u)) == 0u);
}

static int al_owning_bank_valid(const al_owning_byte_store *store) {
  uint32_t index;
  if (store == NULL || store->active_index > 1u ||
      store->transaction_open > 1u || store->transaction_ready > 1u ||
      store->reserved != 0u)
    return 0;
  for (index = 0u; index < 2u; ++index) {
    const al_owning_byte_bank *bank = &store->banks[index];
    al_owning_bank_span bytes_span;
    al_owning_bank_span roots_span;
    size_t roots_bytes;
    if (store->storage_bytes[index] == NULL ||
        store->storage_roots[index] == NULL || bank->bytes == NULL ||
        bank->roots == NULL || bank->bytes != store->storage_bytes[index] ||
        bank->roots != store->storage_roots[index] ||
        bank->byte_capacity == 0u || bank->root_capacity == 0u ||
        (size_t)bank->root_capacity >
            SIZE_MAX / sizeof(al_owning_bank_root) ||
        bank->used_bytes > bank->byte_capacity ||
        bank->root_count > bank->root_capacity)
      return 0;
    roots_bytes =
        (size_t)bank->root_capacity * sizeof(al_owning_bank_root);
    if (!al_owning_bank_make_span(bank->bytes, bank->byte_capacity,
                                  &bytes_span) ||
        !al_owning_bank_make_span(bank->roots, roots_bytes, &roots_span))
      return 0;
  }
  return 1;
}

static void al_owning_bank_clear_roots(al_owning_byte_bank *bank) {
  uint32_t index;
  for (index = 0u; index < bank->root_capacity; ++index) {
    memset(&((al_owning_bank_root *)bank->roots)[index], 0,
           sizeof(al_owning_bank_root));
  }
  bank->used_bytes = 0u;
  bank->root_count = 0u;
}

static int al_owning_bank_storage_disjoint_from_context(
    const al_owning_byte_store *store,
    const al_owning_stack_context *context) {
  al_owning_bank_span context_span;
  al_owning_bank_span stack_span;
  al_owning_bank_span init_span;
  al_owning_bank_span poison_span;
  al_owning_bank_span trace_span;
  al_owning_bank_span store_span;
  uint32_t index;
  size_t root_bytes;

  if (store == NULL || context == NULL ||
      context->abi_version != AL_OWNING_STACK_ABI_VERSION ||
      context->status != AL_OWNING_STATUS_OK ||
      context->stack_capacity_bytes == 0u ||
      context->cursor_bytes > context->stack_capacity_bytes ||
      context->stack_data == NULL || context->init_bitmap == NULL ||
      context->poison_bitmap == NULL ||
      context->init_bitmap_bytes <
          context->stack_capacity_bytes / 8u +
              (context->stack_capacity_bytes % 8u != 0u ? 1u : 0u) ||
      (context->trace_event_capacity != 0u &&
       context->trace_events == NULL) ||
      (size_t)context->trace_event_capacity >
          SIZE_MAX / sizeof(al_owning_stack_event))
    return 0;

  if (!al_owning_bank_make_span(context, sizeof(*context), &context_span) ||
      !al_owning_bank_make_span(store, sizeof(*store), &store_span) ||
      !al_owning_bank_make_span(context->stack_data,
                                context->stack_capacity_bytes, &stack_span) ||
      !al_owning_bank_make_span(context->init_bitmap,
                                context->init_bitmap_bytes, &init_span) ||
      !al_owning_bank_make_span(context->poison_bitmap,
                                context->init_bitmap_bytes, &poison_span) ||
      !al_owning_bank_make_span(
          context->trace_events,
          (size_t)context->trace_event_capacity *
              sizeof(al_owning_stack_event),
          &trace_span) ||
      al_owning_bank_spans_overlap(&store_span, &context_span) ||
      al_owning_bank_spans_overlap(&store_span, &stack_span) ||
      al_owning_bank_spans_overlap(&store_span, &init_span) ||
      al_owning_bank_spans_overlap(&store_span, &poison_span) ||
      al_owning_bank_spans_overlap(&store_span, &trace_span) ||
      al_owning_bank_spans_overlap(&context_span, &stack_span) ||
      al_owning_bank_spans_overlap(&context_span, &init_span) ||
      al_owning_bank_spans_overlap(&context_span, &poison_span) ||
      al_owning_bank_spans_overlap(&context_span, &trace_span) ||
      al_owning_bank_spans_overlap(&stack_span, &init_span) ||
      al_owning_bank_spans_overlap(&stack_span, &poison_span) ||
      al_owning_bank_spans_overlap(&stack_span, &trace_span) ||
      al_owning_bank_spans_overlap(&init_span, &poison_span) ||
      al_owning_bank_spans_overlap(&init_span, &trace_span) ||
      al_owning_bank_spans_overlap(&poison_span, &trace_span))
    return 0;

  for (index = 0u; index < 2u; ++index) {
    al_owning_bank_span bytes_span;
    al_owning_bank_span roots_span;
    if ((size_t)store->banks[index].root_capacity >
        SIZE_MAX / sizeof(al_owning_bank_root))
      return 0;
    root_bytes = (size_t)store->banks[index].root_capacity *
                 sizeof(al_owning_bank_root);
    if (!al_owning_bank_make_span(store->storage_bytes[index],
                                  store->banks[index].byte_capacity,
                                  &bytes_span) ||
        !al_owning_bank_make_span(store->storage_roots[index], root_bytes,
                                  &roots_span) ||
        al_owning_bank_spans_overlap(&context_span, &bytes_span) ||
        al_owning_bank_spans_overlap(&context_span, &roots_span) ||
        al_owning_bank_spans_overlap(&stack_span, &bytes_span) ||
        al_owning_bank_spans_overlap(&stack_span, &roots_span) ||
        al_owning_bank_spans_overlap(&init_span, &bytes_span) ||
        al_owning_bank_spans_overlap(&init_span, &roots_span) ||
        al_owning_bank_spans_overlap(&poison_span, &bytes_span) ||
        al_owning_bank_spans_overlap(&poison_span, &roots_span) ||
        al_owning_bank_spans_overlap(&trace_span, &bytes_span) ||
        al_owning_bank_spans_overlap(&trace_span, &roots_span))
      return 0;
  }
  return 1;
}

static al_owning_bank_result al_owning_bank_validate_layout_separation(
    const al_owning_byte_store *store,
    const al_owning_stack_context *context,
    const al_owning_layout *layout,
    const al_owning_bank_span *slice_array_span) {
  al_owning_bank_span layout_spans[3];
  al_owning_bank_span store_span;
  al_owning_bank_span context_span;
  al_owning_bank_span stack_span;
  al_owning_bank_span init_span;
  al_owning_bank_span poison_span;
  al_owning_bank_span trace_span;
  size_t type_bytes;
  size_t field_bytes;
  uint32_t left;
  uint32_t right;
  uint32_t index;

  if (store == NULL || context == NULL || layout == NULL ||
      slice_array_span == NULL ||
      (size_t)layout->type_count >
          SIZE_MAX / sizeof(al_owning_type_descriptor) ||
      (size_t)layout->field_count >
          SIZE_MAX / sizeof(al_owning_field_descriptor))
    return AL_OWNING_BANK_INVALID_ARGUMENT;

  type_bytes = (size_t)layout->type_count * sizeof(al_owning_type_descriptor);
  field_bytes =
      (size_t)layout->field_count * sizeof(al_owning_field_descriptor);
  if (!al_owning_bank_make_span(layout, sizeof(*layout), &layout_spans[0]) ||
      !al_owning_bank_make_span(layout->types, type_bytes, &layout_spans[1]) ||
      !al_owning_bank_make_span(layout->fields, field_bytes,
                                &layout_spans[2]) ||
      !al_owning_bank_make_span(store, sizeof(*store), &store_span) ||
      !al_owning_bank_make_span(context, sizeof(*context), &context_span) ||
      !al_owning_bank_make_span(context->stack_data,
                                context->stack_capacity_bytes, &stack_span) ||
      !al_owning_bank_make_span(context->init_bitmap,
                                context->init_bitmap_bytes, &init_span) ||
      !al_owning_bank_make_span(context->poison_bitmap,
                                context->init_bitmap_bytes, &poison_span) ||
      !al_owning_bank_make_span(
          context->trace_events,
          (size_t)context->trace_event_capacity *
              sizeof(al_owning_stack_event),
          &trace_span))
    return AL_OWNING_BANK_INVALID_ARGUMENT;

  for (left = 0u; left < 3u; ++left) {
    for (right = left + 1u; right < 3u; ++right) {
      if (al_owning_bank_spans_overlap(&layout_spans[left],
                                       &layout_spans[right]))
        return AL_OWNING_BANK_OVERLAP;
    }
    if (al_owning_bank_spans_overlap(&layout_spans[left], &store_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], &context_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], &stack_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], &init_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], &poison_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], &trace_span) ||
        al_owning_bank_spans_overlap(&layout_spans[left], slice_array_span))
      return AL_OWNING_BANK_OVERLAP;
  }

  for (index = 0u; index < 2u; ++index) {
    al_owning_bank_span bytes_span;
    al_owning_bank_span roots_span;
    if ((size_t)store->banks[index].root_capacity >
        SIZE_MAX / sizeof(al_owning_bank_root))
      return AL_OWNING_BANK_INVALID_STORAGE;
    size_t roots_bytes =
        (size_t)store->banks[index].root_capacity * sizeof(al_owning_bank_root);
    if (!al_owning_bank_make_span(store->storage_bytes[index],
                                  store->banks[index].byte_capacity,
                                  &bytes_span) ||
        !al_owning_bank_make_span(store->storage_roots[index], roots_bytes,
                                  &roots_span))
      return AL_OWNING_BANK_INVALID_STORAGE;
    for (left = 0u; left < 3u; ++left) {
      if (al_owning_bank_spans_overlap(&layout_spans[left], &bytes_span) ||
          al_owning_bank_spans_overlap(&layout_spans[left], &roots_span))
        return AL_OWNING_BANK_OVERLAP;
    }
  }
  return AL_OWNING_BANK_OK;
}

al_owning_bank_result al_owning_byte_store_init(
    al_owning_byte_store *store, uint8_t *bank0_bytes,
    uint32_t bank0_byte_capacity, al_owning_bank_root *bank0_roots,
    uint32_t bank0_root_capacity, uint8_t *bank1_bytes,
    uint32_t bank1_byte_capacity, al_owning_bank_root *bank1_roots,
    uint32_t bank1_root_capacity) {
  al_owning_bank_span regions[5];
  size_t root0_bytes;
  size_t root1_bytes;
  uint32_t left;
  uint32_t right;

  if (store == NULL || bank0_bytes == NULL || bank1_bytes == NULL ||
      bank0_roots == NULL || bank1_roots == NULL || bank0_byte_capacity == 0u ||
      bank1_byte_capacity == 0u || bank0_root_capacity == 0u ||
      bank1_root_capacity == 0u ||
      !al_owning_bank_pointer_aligned(store,
                                     _Alignof(al_owning_byte_store)) ||
      !al_owning_bank_pointer_aligned(bank0_bytes, 8u) ||
      !al_owning_bank_pointer_aligned(bank1_bytes, 8u) ||
      !al_owning_bank_pointer_aligned(bank0_roots,
                                     _Alignof(al_owning_bank_root)) ||
      !al_owning_bank_pointer_aligned(bank1_roots,
                                     _Alignof(al_owning_bank_root)) ||
      (size_t)bank0_root_capacity > SIZE_MAX / sizeof(al_owning_bank_root) ||
      (size_t)bank1_root_capacity > SIZE_MAX / sizeof(al_owning_bank_root))
    return AL_OWNING_BANK_INVALID_ARGUMENT;

  root0_bytes = (size_t)bank0_root_capacity * sizeof(al_owning_bank_root);
  root1_bytes = (size_t)bank1_root_capacity * sizeof(al_owning_bank_root);
  if (!al_owning_bank_make_span(store, sizeof(*store), &regions[0]) ||
      !al_owning_bank_make_span(bank0_bytes, bank0_byte_capacity, &regions[1]) ||
      !al_owning_bank_make_span(bank0_roots, root0_bytes, &regions[2]) ||
      !al_owning_bank_make_span(bank1_bytes, bank1_byte_capacity, &regions[3]) ||
      !al_owning_bank_make_span(bank1_roots, root1_bytes, &regions[4]))
    return AL_OWNING_BANK_INVALID_STORAGE;
  for (left = 0u; left < 5u; ++left) {
    for (right = left + 1u; right < 5u; ++right) {
      if (al_owning_bank_spans_overlap(&regions[left], &regions[right]))
        return AL_OWNING_BANK_INVALID_STORAGE;
    }
  }

  memset(store, 0, sizeof(*store));
  store->storage_bytes[0] = bank0_bytes;
  store->storage_bytes[1] = bank1_bytes;
  store->storage_roots[0] = bank0_roots;
  store->storage_roots[1] = bank1_roots;
  store->banks[0].bytes = bank0_bytes;
  store->banks[0].byte_capacity = bank0_byte_capacity;
  store->banks[0].roots = bank0_roots;
  store->banks[0].root_capacity = bank0_root_capacity;
  store->banks[1].bytes = bank1_bytes;
  store->banks[1].byte_capacity = bank1_byte_capacity;
  store->banks[1].roots = bank1_roots;
  store->banks[1].root_capacity = bank1_root_capacity;
  memset(bank0_roots, 0, root0_bytes);
  memset(bank1_roots, 0, root1_bytes);
  return AL_OWNING_BANK_OK;
}

al_owning_bank_result al_owning_byte_store_begin(
    al_owning_byte_store *store) {
  uint32_t staging_index;
  if (!al_owning_bank_valid(store))
    return AL_OWNING_BANK_INVALID_STORAGE;
  if (store->transaction_open != 0u)
    return AL_OWNING_BANK_BUSY;
  staging_index = 1u - store->active_index;
  al_owning_bank_clear_roots(&store->banks[staging_index]);
  store->transaction_open = 1u;
  store->transaction_ready = 0u;
  return AL_OWNING_BANK_OK;
}

static void al_owning_bank_reset_staging(al_owning_byte_store *store,
                                         uint32_t staging_index,
                                         uint32_t prepared_roots) {
  uint32_t index;
  al_owning_byte_bank *staging = &store->banks[staging_index];
  al_owning_bank_root *roots =
      (al_owning_bank_root *)store->storage_roots[staging_index];
  for (index = 0u; index < prepared_roots; ++index)
    memset(&roots[index], 0, sizeof(roots[index]));
  staging->used_bytes = 0u;
  staging->root_count = 0u;
  store->transaction_ready = 0u;
}

al_owning_bank_result al_owning_byte_store_stage_stack_values(
    al_owning_byte_store *store, al_owning_stack_context *context,
    const al_owning_layout *layout,
    const al_owning_bank_stack_slice *slices, uint32_t slice_count,
    uint32_t error_id) {
  uint32_t staging_index;
  uint32_t index;
  uint64_t total_bytes = 0u;
  size_t slice_array_bytes;
  al_owning_byte_bank *staging;
  al_owning_bank_root *prepared_roots;
  al_owning_bank_span slice_array_span;
  al_owning_bank_span destination_span;
  al_owning_bank_span active_span;
  al_owning_bank_span context_span;
  uint32_t active_index;
  al_owning_bank_result layout_separation;

  if (!al_owning_bank_valid(store))
    return AL_OWNING_BANK_INVALID_STORAGE;
  if (store->transaction_open == 0u)
    return AL_OWNING_BANK_NO_TRANSACTION;
  if (store->transaction_ready != 0u)
    return AL_OWNING_BANK_BUSY;
  staging_index = 1u - store->active_index;
  staging = &store->banks[staging_index];
  if (staging->used_bytes != 0u || staging->root_count != 0u)
    return AL_OWNING_BANK_BUSY;
  if (slice_count > staging->root_capacity)
    return AL_OWNING_BANK_ROOT_CAPACITY;
  if (slice_count == 0u) {
    if (slices != NULL)
      return AL_OWNING_BANK_INVALID_ARGUMENT;
    store->transaction_ready = 1u;
    return AL_OWNING_BANK_OK;
  }
  if (context == NULL || layout == NULL || slices == NULL ||
      (size_t)slice_count > SIZE_MAX / sizeof(*slices))
    return AL_OWNING_BANK_INVALID_ARGUMENT;
  slice_array_bytes = (size_t)slice_count * sizeof(*slices);
  if (!al_owning_bank_storage_disjoint_from_context(store, context) ||
      !al_owning_bank_make_span(slices, slice_array_bytes, &slice_array_span) ||
      !al_owning_bank_make_span(context, sizeof(*context), &context_span))
    return AL_OWNING_BANK_INVALID_STORAGE;
  layout_separation = al_owning_bank_validate_layout_separation(
      store, context, layout, &slice_array_span);
  if (layout_separation != AL_OWNING_BANK_OK)
    return layout_separation;

  for (index = 0u; index < 2u; ++index) {
    size_t roots_bytes = (size_t)store->banks[index].root_capacity *
                         sizeof(al_owning_bank_root);
    if (al_owning_bank_span_overlaps_region(&slice_array_span, store,
                                            sizeof(*store)) ||
        al_owning_bank_span_overlaps_region(
            &slice_array_span, store->storage_bytes[index],
            store->banks[index].byte_capacity) ||
        al_owning_bank_span_overlaps_region(&slice_array_span,
                                            store->storage_roots[index],
                                            roots_bytes))
      return AL_OWNING_BANK_OVERLAP;
  }
  if (al_owning_bank_spans_overlap(&slice_array_span, &context_span) ||
      al_owning_bank_span_overlaps_region(
          &slice_array_span, context->stack_data,
          context->stack_capacity_bytes) ||
      al_owning_bank_span_overlaps_region(
          &slice_array_span, context->init_bitmap,
          context->init_bitmap_bytes) ||
      al_owning_bank_span_overlaps_region(
          &slice_array_span, context->poison_bitmap,
          context->init_bitmap_bytes) ||
      (context->trace_event_capacity != 0u &&
       al_owning_bank_span_overlaps_region(
           &slice_array_span, context->trace_events,
           (size_t)context->trace_event_capacity *
               sizeof(al_owning_stack_event))))
    return AL_OWNING_BANK_OVERLAP;

  prepared_roots = store->storage_roots[staging_index];
  for (index = 0u; index < slice_count; ++index) {
    al_owning_value_size measured;
    const al_owning_bank_stack_slice *slice = &slices[index];
    uint32_t type_id;
    if (slice->reserved != 0u ||
        al_owning_measure_value(context, layout, slice->type_index,
                                slice->source_offset_bytes,
                                slice->source_owner_end_bytes, error_id,
                                &measured) != 0 ||
        measured.extent_bytes == 0u) {
      al_owning_bank_reset_staging(store, staging_index, index + 1u);
      return AL_OWNING_BANK_INVALID_VALUE;
    }
    if ((uint64_t)measured.extent_bytes > UINT32_MAX - total_bytes) {
      al_owning_bank_reset_staging(store, staging_index, index + 1u);
      return AL_OWNING_BANK_OVERFLOW;
    }
    type_id = layout->types[slice->type_index].type_id;
    prepared_roots[index].type_id = type_id;
    prepared_roots[index].offset_bytes = (uint32_t)total_bytes;
    prepared_roots[index].extent_bytes = measured.extent_bytes;
    prepared_roots[index].payload_bytes = measured.payload_bytes;
    prepared_roots[index].owner_end_bytes =
        (uint32_t)total_bytes + measured.extent_bytes;
    total_bytes += measured.extent_bytes;
  }

  if (total_bytes > staging->byte_capacity) {
    al_owning_bank_reset_staging(store, staging_index, slice_count);
    return AL_OWNING_BANK_BYTE_CAPACITY;
  }
  if (total_bytes > UINT64_MAX - context->retained_copy_bytes ||
      !al_owning_bank_make_span(staging->bytes, (size_t)total_bytes,
                                &destination_span)) {
    al_owning_bank_reset_staging(store, staging_index, slice_count);
    return AL_OWNING_BANK_OVERFLOW;
  }
  active_index = store->active_index;
  if (!al_owning_bank_make_span(
          store->banks[active_index].bytes,
          store->banks[active_index].byte_capacity, &active_span) ||
      al_owning_bank_spans_overlap(&destination_span, &active_span)) {
    al_owning_bank_reset_staging(store, staging_index, slice_count);
    return AL_OWNING_BANK_OVERLAP;
  }
  for (index = 0u; index < slice_count; ++index) {
    const al_owning_bank_stack_slice *slice = &slices[index];
    if (al_owning_bank_span_overlaps_region(
            &destination_span,
            context->stack_data + slice->source_offset_bytes,
            prepared_roots[index].extent_bytes)) {
      al_owning_bank_reset_staging(store, staging_index, slice_count);
      return AL_OWNING_BANK_OVERLAP;
    }
  }

  for (index = 0u; index < slice_count; ++index) {
    const al_owning_bank_stack_slice *slice = &slices[index];
    const al_owning_bank_root *root = &prepared_roots[index];
    uint32_t remaining = staging->byte_capacity - root->offset_bytes;
    al_owning_publish(context, store->storage_bytes[staging_index] +
                                   root->offset_bytes,
                      remaining, slice->source_offset_bytes,
                      root->extent_bytes, root->type_id);
    if (context->status != AL_OWNING_STATUS_OK) {
      al_owning_bank_reset_staging(store, staging_index, slice_count);
      return AL_OWNING_BANK_INVALID_VALUE;
    }
  }

  staging->used_bytes = (uint32_t)total_bytes;
  staging->root_count = slice_count;
  store->transaction_ready = 1u;
  return AL_OWNING_BANK_OK;
}

al_owning_bank_result al_owning_byte_store_commit(
    al_owning_byte_store *store) {
  uint32_t staging_index;
  if (!al_owning_bank_valid(store))
    return AL_OWNING_BANK_INVALID_STORAGE;
  if (store->transaction_open == 0u)
    return AL_OWNING_BANK_NO_TRANSACTION;
  if (store->transaction_ready == 0u)
    return AL_OWNING_BANK_BUSY;
  staging_index = 1u - store->active_index;
  store->last_committed_copy_bytes = store->banks[staging_index].used_bytes;
  store->active_index = staging_index;
  store->transaction_open = 0u;
  store->transaction_ready = 0u;
  return AL_OWNING_BANK_OK;
}

al_owning_bank_result al_owning_byte_store_abort(
    al_owning_byte_store *store) {
  uint32_t staging_index;
  if (!al_owning_bank_valid(store))
    return AL_OWNING_BANK_INVALID_STORAGE;
  if (store->transaction_open == 0u)
    return AL_OWNING_BANK_NO_TRANSACTION;
  staging_index = 1u - store->active_index;
  al_owning_bank_clear_roots(&store->banks[staging_index]);
  store->transaction_open = 0u;
  store->transaction_ready = 0u;
  return AL_OWNING_BANK_OK;
}

al_owning_bank_result al_owning_byte_store_trim_last_root(
    al_owning_byte_store *store) {
  al_owning_byte_bank *active;
  al_owning_bank_root *roots;
  uint32_t active_index;
  uint32_t retained_end;

  if (!al_owning_bank_valid(store))
    return AL_OWNING_BANK_INVALID_STORAGE;
  if (store->transaction_open != 0u)
    return AL_OWNING_BANK_BUSY;
  active_index = store->active_index;
  active = &store->banks[active_index];
  if (active->root_count != 2u || active->used_bytes == 0u)
    return AL_OWNING_BANK_INVALID_VALUE;
  roots = store->storage_roots[active_index];
  if (roots[0].offset_bytes != 0u || roots[0].extent_bytes == 0u ||
      roots[0].owner_end_bytes != roots[0].extent_bytes ||
      roots[0].owner_end_bytes > active->used_bytes ||
      roots[0].payload_bytes > roots[0].extent_bytes ||
      roots[1].offset_bytes != roots[0].owner_end_bytes ||
      roots[1].extent_bytes == 0u ||
      roots[1].offset_bytes > active->used_bytes ||
      roots[1].extent_bytes > active->used_bytes - roots[1].offset_bytes ||
      roots[1].owner_end_bytes !=
          roots[1].offset_bytes + roots[1].extent_bytes ||
      roots[1].owner_end_bytes != active->used_bytes ||
      roots[1].payload_bytes > roots[1].extent_bytes)
    return AL_OWNING_BANK_INVALID_VALUE;

  retained_end = roots[0].owner_end_bytes;
  memset(&roots[1], 0, sizeof(roots[1]));
  active->used_bytes = retained_end;
  active->root_count = 1u;
  return AL_OWNING_BANK_OK;
}

const al_owning_byte_bank *al_owning_byte_store_active(
    const al_owning_byte_store *store) {
  if (!al_owning_bank_valid(store))
    return NULL;
  return &store->banks[store->active_index];
}

uint64_t al_owning_byte_store_last_committed_copy_bytes(
    const al_owning_byte_store *store) {
  if (!al_owning_bank_valid(store))
    return 0u;
  return store->last_committed_copy_bytes;
}

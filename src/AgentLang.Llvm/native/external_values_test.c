/* Values admitted from a host or caller (external views) are still checked
 * byte for byte when the runtime trusts the compiler's layout (report 313).
 * Built twice by Test-NativeRuntime.ps1: with AL_OWNING_TRUSTED_GENERATED (the
 * layout walk and descriptor checks skipped) and without (both run, which also
 * checks this file's hand-written layout). Every valid value must be accepted
 * and every malformed one refused, in both builds. */
#include "owning_stack_runtime.c"
#include <stdio.h>
#include <string.h>

enum {
  T_INT = 0u,
  T_BOOL = 1u,
  T_STRING = 2u,
  T_ENUM = 3u,
  T_INT_LIST = 4u,
  T_OPTION_STRING = 5u,
  T_RECORD = 6u,
  T_STRING_LIST = 7u,
  T_COUNT = 8u
};

#define DYN AL_OWNING_LAYOUT_DYNAMIC_U32

static const al_owning_type_descriptor types[] = {
    {AL_OWNING_TYPE_I64, 1u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_BOOL, 2u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING, 3u, 0u, 0u, DYN, DYN, 8u, 8u, 0u},
    {AL_OWNING_TYPE_ENUM, 4u, 0u, 0u, 8u, 8u, 8u, 8u, 3u},
    {AL_OWNING_TYPE_LIST, 5u, 0u, 1u, DYN, DYN, 8u, 8u, 0u},
    {AL_OWNING_TYPE_OPTION, 6u, 1u, 2u, DYN, DYN, 8u, 8u, 2u},
    {AL_OWNING_TYPE_RECORD, 7u, 3u, 2u, DYN, DYN, 16u, 16u, 0u},
    {AL_OWNING_TYPE_LIST, 8u, 5u, 1u, DYN, DYN, 8u, 8u, 0u}};

static const al_owning_field_descriptor fields[] = {
    {T_INT, 8u, 0u, 0u},     /* 0: List<Int> element */
    {T_STRING, 8u, 0u, 0u},  /* 1: Option some */
    {DYN, 8u, 0u, 0u},       /* 2: Option none */
    {T_STRING, 0u, 0u, 0u},  /* 3: record name */
    {T_INT, DYN, 0u, 0u},    /* 4: record count */
    {T_STRING, 8u, 0u, 0u}}; /* 5: List<String> element */

static const al_owning_layout layout = {AL_OWNING_LAYOUT_ABI_VERSION, types,
                                        T_COUNT, fields,
                                        (uint32_t)(sizeof(fields) / sizeof(fields[0]))};

static uint8_t buffer[256];
static uint32_t used;
static int failures = 0;
static int checks = 0;

static void reset(void) {
  memset(buffer, 0, sizeof buffer);
  used = 0u;
}
static void put_u32(uint32_t value) {
  al_owning_put_le32_at(buffer + used, value);
  used += 4u;
}
static void put_u64(uint64_t value) {
  al_owning_put_le64_at(buffer + used, value);
  used += 8u;
}
static void put_text(const char *text, uint32_t length) {
  put_u32(length);
  put_u32(0u);
  memcpy(buffer + used, text, length);
  used += (length + 7u) & ~7u;
}

static void expect(const char *label, uint32_t type, uint32_t length, int accepted,
                   uint32_t extent_if_accepted) {
  al_owning_stack_context ctx;
  uint32_t payload = 0u;
  uint32_t extent = 0u;
  int32_t status;
  memset(&ctx, 0, sizeof ctx);
  ctx.abi_version = AL_OWNING_STACK_ABI_VERSION;
  ctx.status = AL_OWNING_STATUS_OK;
  status = al_owning_measure_external_value(&ctx, &layout, type, buffer, length,
                                            0u, 7u, &payload, &extent);
  ++checks;
  if ((status == 0) != accepted ||
      (accepted && extent != extent_if_accepted)) {
    ++failures;
    printf("FAIL %s: status %d extent %u (expected %s, extent %u)\n", label,
           status, extent, accepted ? "accepted" : "refused",
           extent_if_accepted);
  }
}

int main(void) {
  /* Int: any 8 bytes; fewer is refused. */
  reset(); put_u64(0xFFFFFFFFFFFFFFFFull);
  expect("int", T_INT, 8u, 1, 8u);
  expect("int truncated", T_INT, 7u, 0, 0u);

  /* Bool: 0 or 1 only. */
  reset(); put_u64(1u); expect("bool true", T_BOOL, 8u, 1, 8u);
  reset(); put_u64(2u); expect("bool 2", T_BOOL, 8u, 0, 0u);

  /* Enum: ordinal below its case count. */
  reset(); put_u64(2u); expect("enum 2", T_ENUM, 8u, 1, 8u);
  reset(); put_u64(3u); expect("enum 3", T_ENUM, 8u, 0, 0u);

  /* String: header, reserved bytes zero, padding zero, length in range, UTF-8. */
  reset(); put_text("abc", 3u); expect("string", T_STRING, used, 1, 16u);
  reset(); put_text("abc", 3u); buffer[4] = 1u; expect("string reserved", T_STRING, used, 0, 0u);
  reset(); put_text("abc", 3u); buffer[11] = 'x'; expect("string padding", T_STRING, used, 0, 0u);
  reset(); put_text("abc", 3u); al_owning_put_le32_at(buffer, 40u); expect("string length past end", T_STRING, used, 0, 0u);
  reset(); put_text("a\xC3", 2u); expect("string cut UTF-8", T_STRING, used, 0, 0u);
  reset(); put_text("\xED\xA0\x80", 3u); expect("string surrogate", T_STRING, used, 0, 0u);
  reset(); put_text("\xC3\xA9\xF0\x9F\x9A\x80", 6u); expect("string UTF-8", T_STRING, used, 1, 16u);
  reset(); put_text("abcdefgh", 8u); expect("string truncated", T_STRING, used - 1u, 0, 0u);

  /* List<Int>: count elements fit; reserved zero. */
  reset(); put_u32(2u); put_u32(0u); put_u64(5u); put_u64(6u);
  expect("int list", T_INT_LIST, used, 1, 24u);
  reset(); put_u32(3u); put_u32(0u); put_u64(5u); put_u64(6u);
  expect("int list count too big", T_INT_LIST, used, 0, 0u);
  reset(); put_u32(0x40000000u); put_u32(0u);
  expect("int list huge count", T_INT_LIST, used, 0, 0u);
  reset(); put_u32(1u); put_u32(9u); put_u64(5u);
  expect("int list reserved", T_INT_LIST, used, 0, 0u);

  /* List<String>: each element checked. */
  reset(); put_u32(2u); put_u32(0u); put_text("ab", 2u); put_text("c", 1u);
  expect("string list", T_STRING_LIST, used, 1, 40u);
  reset(); put_u32(2u); put_u32(0u); put_text("ab", 2u); put_text("\xFF", 1u);
  expect("string list bad element", T_STRING_LIST, used, 0, 0u);

  /* Option<String>: tag 0 some (payload checked), 1 none, 2 refused. */
  reset(); put_u64(0u); put_text("hi", 2u); expect("option some", T_OPTION_STRING, used, 1, 24u);
  reset(); put_u64(1u); expect("option none", T_OPTION_STRING, used, 1, 8u);
  reset(); put_u64(2u); expect("option tag 2", T_OPTION_STRING, used, 0, 0u);
  reset(); put_u64(0u); put_text("\xC0\x80", 2u); expect("option some bad UTF-8", T_OPTION_STRING, used, 0, 0u);

  /* Record { name: String, count: Int }: both fields, in bounds. */
  reset(); put_text("name", 4u); put_u64(7u); expect("record", T_RECORD, used, 1, 24u);
  reset(); put_text("name", 4u); put_u64(7u); expect("record truncated", T_RECORD, used - 8u, 0, 0u);
  reset(); put_text("name", 4u); buffer[7] = 1u; put_u64(7u); expect("record name reserved", T_RECORD, used, 0, 0u);

  printf("external values: %d checks, %d failures (%s)\n", checks, failures,
#if AL_OWNING_TRUSTED_GENERATED
         "trusted layout"
#else
         "layout walked"
#endif
  );
  return failures != 0;
}

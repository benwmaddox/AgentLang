/* The C library of the foreign-call tests (report 307): one function per ABI
   shape the bindings support. Built by the test with the toolchain's clang. */
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define FFI_EXPORT __declspec(dllexport)
#else
#define FFI_EXPORT __attribute__((visibility("default")))
#endif

FFI_EXPORT int64_t ffi_add64(int64_t a, int64_t b) { return a + b; }
FFI_EXPORT uint32_t ffi_xor32(uint32_t a, int32_t b) { return a ^ (uint32_t)b; }
FFI_EXPORT int8_t ffi_negate8(int8_t a) { return (int8_t)-a; }
FFI_EXPORT uint16_t ffi_double16(uint16_t a) { return (uint16_t)(a * 2u); }
FFI_EXPORT uint64_t ffi_flip_high(uint64_t a) { return a ^ 0xFFFFFFFF00000000ull; }
FFI_EXPORT unsigned long ffi_next_long(unsigned long a) { return a + 1ul; }
FFI_EXPORT long ffi_negate_long(long a) { return -a; }
FFI_EXPORT double ffi_scale(double x, int32_t n) { return x * (double)n; }
FFI_EXPORT bool ffi_even(int64_t n) { return n % 2 == 0; }
FFI_EXPORT bool ffi_not(bool b) { return !b; }

/* bytes first, middle and last; -1000000 marks a null pointer, which never comes. */
FFI_EXPORT int64_t ffi_sum(const uint8_t *data, size_t length) {
  int64_t sum = 0;
  if (data == 0) return -1000000;
  for (size_t index = 0; index < length; ++index) sum += data[index];
  return sum;
}
FFI_EXPORT int64_t ffi_first(const uint8_t *data, size_t length, int64_t seed) {
  return ffi_sum(data, length) * 1000 + seed;
}
FFI_EXPORT int64_t ffi_middle(int64_t a, const uint8_t *data, size_t length, int64_t b) {
  return a * 1000000 + ffi_sum(data, length) * 1000 + b;
}
FFI_EXPORT int64_t ffi_last(int64_t a, const uint8_t *data, size_t length) {
  return a * 1000 + (int64_t)length;
}
FFI_EXPORT size_t ffi_text_bytes(const char *text, size_t length) {
  return text == 0 ? (size_t)-1 : length;
}
FFI_EXPORT int32_t ffi_compare(const char *left, size_t left_length, const uint8_t *right, size_t right_length) {
  size_t shorter = left_length < right_length ? left_length : right_length;
  for (size_t index = 0; index < shorter; ++index) {
    if ((uint8_t)left[index] != right[index]) return (uint8_t)left[index] < right[index] ? -1 : 1;
  }
  return left_length == right_length ? 0 : (left_length < right_length ? -1 : 1);
}

/* zlib's CRC-32 (the polynomial and seeding of crc32_z), for comparing with Flow's. */
FFI_EXPORT uint32_t ffi_crc32(uint32_t crc, const uint8_t *data, size_t length) {
  crc = ~crc;
  for (size_t index = 0; index < length; ++index) {
    crc ^= data[index];
    for (int bit = 0; bit < 8; ++bit) crc = (crc & 1u) ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
  }
  return ~crc;
}

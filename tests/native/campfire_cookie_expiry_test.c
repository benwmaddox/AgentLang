#if defined(_MSC_VER)
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <inttypes.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../../applications/campfire/Native/http/cookie_expiry.h"

#define OUTPUT_BYTES 29u
#define GUARD_BYTES 8u
#define SENTINEL_BYTE 0xa5u
#define SENTINEL_WRITTEN UINT32_C(0x76543210)
#define MICROSECONDS_PER_DAY INT64_C(86400000000)
#define DOTNET_MIN_UNIX_MICROSECONDS INT64_C(-62135596800000000)
#define DOTNET_MAX_UNIX_MICROSECONDS INT64_C(253402300799999999)

typedef struct expiry_case {
  const char *name;
  int64_t now_microseconds;
} expiry_case;

static const expiry_case valid_cases[] = {
    {"min_year_one", INT64_C(-62135596800000000)},
    {"year_1601", INT64_C(-11644473600000000)},
    {"unix_epoch", INT64_C(0)},
    {"pre_epoch_minus_one_microsecond", INT64_C(-1)},
    {"pre_epoch_negative_day_floor", INT64_C(-86400000001)},
    {"leap_2000_to_2020", INT64_C(951827696789012)},
    {"leap_2080_clamps_in_2100", INT64_C(3476435696123456)},
    {"leap_2380_to_2400", INT64_C(12943456496654321)},
    {"current_2026", INT64_C(1791815445123456)},
    {"last_year_9979_start", INT64_C(252739612800000000)},
    {"last_year_9979_end", INT64_C(252771148799999999)}};

static int fail(const char *message, const char *name) {
  fprintf(stderr, "FAIL:%s:%s\n", message, name);
  return 1;
}

static void fill_sentinel(uint8_t *bytes, uint32_t count) {
  uint32_t index;
  for (index = 0u; index < count; ++index) bytes[index] = SENTINEL_BYTE;
}

static int all_sentinel(const uint8_t *bytes, uint32_t count) {
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (bytes[index] != SENTINEL_BYTE) return 0;
  }
  return 1;
}

static int expect_success(const expiry_case *test_case) {
  uint8_t guarded[OUTPUT_BYTES + GUARD_BYTES];
  uint32_t written = SENTINEL_WRITTEN;
  uint32_t index;
  fill_sentinel(guarded, (uint32_t)sizeof(guarded));
  if (al_campfire_cookie_expiry_20_years_v1(
          test_case->now_microseconds, guarded, OUTPUT_BYTES, &written) != 1)
    return fail("valid input rejected", test_case->name);
  if (written != OUTPUT_BYTES)
    return fail("wrong output length", test_case->name);
  for (index = OUTPUT_BYTES; index < (uint32_t)sizeof(guarded); ++index) {
    if (guarded[index] != SENTINEL_BYTE)
      return fail("output canary changed", test_case->name);
  }
  printf("CASE|%s|%" PRId64 "|", test_case->name,
         test_case->now_microseconds);
  if (fwrite(guarded, 1u, OUTPUT_BYTES, stdout) != OUTPUT_BYTES)
    return fail("could not write oracle case", test_case->name);
  putchar('\n');
  return 0;
}

static int expect_rejection(int64_t now_microseconds, const char *name) {
  uint8_t output[OUTPUT_BYTES + GUARD_BYTES];
  uint32_t written = SENTINEL_WRITTEN;
  fill_sentinel(output, (uint32_t)sizeof(output));
  if (al_campfire_cookie_expiry_20_years_v1(
          now_microseconds, output, OUTPUT_BYTES, &written) != 0)
    return fail("invalid input accepted", name);
  if (!all_sentinel(output, (uint32_t)sizeof(output)))
    return fail("invalid input modified output", name);
  if (written != SENTINEL_WRITTEN)
    return fail("invalid input modified length", name);
  return 0;
}

static int test_capacity_and_null_outputs(int64_t now_microseconds) {
  uint8_t output[OUTPUT_BYTES + GUARD_BYTES];
  uint32_t written;
  uint32_t capacity;

  for (capacity = 0u; capacity < OUTPUT_BYTES; ++capacity) {
    fill_sentinel(output, (uint32_t)sizeof(output));
    written = SENTINEL_WRITTEN;
    if (al_campfire_cookie_expiry_20_years_v1(
            now_microseconds, output, capacity, &written) != 0)
      return fail("short capacity accepted", "capacity_0_to_28");
    if (!all_sentinel(output, (uint32_t)sizeof(output)) ||
        written != SENTINEL_WRITTEN)
      return fail("short capacity changed output", "capacity_0_to_28");
  }

  fill_sentinel(output, (uint32_t)sizeof(output));
  written = SENTINEL_WRITTEN;
  if (al_campfire_cookie_expiry_20_years_v1(
          now_microseconds, NULL, OUTPUT_BYTES, &written) != 0 ||
      written != SENTINEL_WRITTEN)
    return fail("null output accepted or changed length", "null_output");

  fill_sentinel(output, (uint32_t)sizeof(output));
  if (al_campfire_cookie_expiry_20_years_v1(
          now_microseconds, output, OUTPUT_BYTES, NULL) != 0 ||
      !all_sentinel(output, (uint32_t)sizeof(output)) )
    return fail("null length accepted or changed output", "null_written");

  if (al_campfire_cookie_expiry_20_years_v1(
          now_microseconds, NULL, 0u, NULL) != 0)
    return fail("null zero-capacity outputs accepted", "null_zero_capacity");
  return 0;
}

static int run_oracle_file(const char *path) {
  FILE *input = fopen(path, "rb");
  char line[96];
  uint32_t cases = 0u;
  if (input == NULL) return fail("could not open oracle file", "oracle_file");

  while (fgets(line, (int)sizeof(line), input) != NULL) {
    size_t length = strlen(line);
    char *separator, *end = NULL;
    long long parsed;
    int64_t now_microseconds;
    const char *expected;
    uint8_t guarded[OUTPUT_BYTES + GUARD_BYTES];
    uint32_t written = SENTINEL_WRITTEN;
    uint32_t index;

    if (length == 0u || line[length - 1u] != '\n') {
      fclose(input);
      return fail("oracle row is incomplete", "oracle_file");
    }
    line[--length] = '\0';
    if (length > 0u && line[length - 1u] == '\r') line[--length] = '\0';
    separator = strchr(line, '|');
    if (separator == NULL || separator == line) {
      fclose(input);
      return fail("oracle row has no timestamp separator", "oracle_file");
    }
    *separator = '\0';
    errno = 0;
    parsed = strtoll(line, &end, 10);
    if (errno == ERANGE || end == line || *end != '\0') {
      fclose(input);
      return fail("oracle timestamp is invalid", "oracle_file");
    }
    now_microseconds = (int64_t)parsed;
    expected = separator + 1;
    if (strlen(expected) != OUTPUT_BYTES) {
      fclose(input);
      return fail("oracle expiry has wrong length", "oracle_file");
    }

    fill_sentinel(guarded, (uint32_t)sizeof(guarded));
    if (al_campfire_cookie_expiry_20_years_v1(
            now_microseconds, guarded, OUTPUT_BYTES, &written) != 1 ||
        written != OUTPUT_BYTES) {
      fclose(input);
      return fail("native helper rejected oracle timestamp", "oracle_file");
    }
    for (index = 0u; index < OUTPUT_BYTES; ++index) {
      if (guarded[index] != (uint8_t)expected[index]) {
        fclose(input);
        return fail("native result differs from .NET oracle", "oracle_file");
      }
    }
    for (index = OUTPUT_BYTES; index < (uint32_t)sizeof(guarded); ++index) {
      if (guarded[index] != SENTINEL_BYTE) {
        fclose(input);
        return fail("batch output canary changed", "oracle_file");
      }
    }
    ++cases;
  }

  if (ferror(input)) {
    fclose(input);
    return fail("could not read oracle file", "oracle_file");
  }
  if (fclose(input) != 0) return fail("could not close oracle file", "oracle_file");
  if (cases != 9600u) return fail("oracle file has wrong case count", "oracle_file");
  printf("BATCH|cases=%" PRIu32 "\n", cases);
  return 0;
}

int main(int argc, char **argv) {
  uint32_t index;
  int batch_mode = 0;
  const int64_t year_9980_start = INT64_C(252771148800000000);
  const int64_t invalid_low = DOTNET_MIN_UNIX_MICROSECONDS - 1;
  const int64_t invalid_high = DOTNET_MAX_UNIX_MICROSECONDS + 1;

  for (index = 0u; index < (uint32_t)(sizeof(valid_cases) / sizeof(valid_cases[0])); ++index) {
    if (expect_success(&valid_cases[index]) != 0) return 1;
  }
  if (test_capacity_and_null_outputs(valid_cases[0].now_microseconds) != 0) return 1;
  if (expect_rejection(year_9980_start, "expiry_year_overflow_9980") != 0) return 1;
  if (expect_rejection(invalid_low, "below_dotnet_minimum") != 0) return 1;
  if (expect_rejection(invalid_high, "above_dotnet_maximum") != 0) return 1;
  if (expect_rejection(INT64_MIN, "int64_minimum") != 0) return 1;
  if (expect_rejection(INT64_MAX, "int64_maximum") != 0) return 1;
  if (argc == 3 && strcmp(argv[1], "--batch-file") == 0) {
    if (run_oracle_file(argv[2]) != 0) return 1;
    batch_mode = 1;
  } else if (argc != 1) {
    return fail("unexpected arguments", "main");
  }

  if (batch_mode)
    puts("campfire_cookie_expiry_v1=passed valid_cases=11 rejection_cases=5 short_capacity_cases=29 batch_cases=9600");
  else
    puts("campfire_cookie_expiry_v1=passed valid_cases=11 rejection_cases=5 short_capacity_cases=29 batch_cases=0");
  return 0;
}

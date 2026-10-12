#include "campfire_room_host_abi.h"
#include "room_sqlite_adapter.h"
#include "sqlite_subset.h"

#include <stddef.h>

#if !defined(_WIN32)
#error "The Campfire native SQLite adapter is currently Windows x64 only."
#endif

/* This adapter is linked into the native module. Its only host operation is
 * the fixed, compiler-authorized rooms.find-for-user callback. */
#define SQLITE_BUSY_TIMEOUT_MS 5000
/* A context is confined to its initializing thread and serialized by active,
 * so SQLite's per-connection mutex is redundant. Global SQLite threading stays
 * enabled for distinct worker-owned connections. */
#define SQLITE_OPEN_FLAGS \
  (SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_PRIVATECACHE)
#define ROOM_CONTEXT_COOKIE UINT64_C(0x4346524F4F4D5631)
#define HOST_BOUNDARY_FAILURE 1
#define SQLITE_TICKS_PER_SECOND INT64_C(10000000)
#define SQLITE_TICKS_PER_MINUTE (INT64_C(60) * SQLITE_TICKS_PER_SECOND)
#define SQLITE_TICKS_PER_DAY (INT64_C(86400) * SQLITE_TICKS_PER_SECOND)
#define DATETIME_MAX_TICKS INT64_C(3155378975999999999)

extern __declspec(dllimport) uint32_t __stdcall GetCurrentThreadId(void);

static const char room_query[] =
    "SELECT rooms.id, rooms.name, rooms.type, rooms.creator_id, "
    "rooms.created_at, rooms.updated_at "
    "FROM rooms INNER JOIN memberships ON rooms.id = memberships.room_id "
    "WHERE memberships.user_id = $userId AND rooms.id = $roomId LIMIT 1";

static void clear_bytes(void *memory, size_t count) {
  volatile uint8_t *bytes = (volatile uint8_t *)memory;
  size_t index;
  for (index = 0u; index < count; ++index)
    bytes[index] = 0u;
}

static int byte_sequence_equal(const uint8_t *left, const uint8_t *right,
                               uint32_t count) {
  volatile const uint8_t *left_bytes = left;
  volatile const uint8_t *right_bytes = right;
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (left_bytes[index] != right_bytes[index])
      return 0;
  }
  return 1;
}

static int code_unit_sequence_equal(const uint16_t *left,
                                    const uint16_t *right,
                                    uint32_t count) {
  volatile const uint16_t *left_units = left;
  volatile const uint16_t *right_units = right;
  uint32_t index;
  for (index = 0u; index < count; ++index) {
    if (left_units[index] != right_units[index])
      return 0;
  }
  return 1;
}

static void store_u64_le(uint8_t *output, uint32_t offset, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    output[offset + index] = (uint8_t)(value >> (index * 8u));
}

static uint32_t padded_string_extent(uint32_t code_units) {
  uint64_t raw = UINT64_C(8) + (uint64_t)code_units * UINT64_C(2);
  uint64_t padded = (raw + UINT64_C(7)) & ~UINT64_C(7);
  if (padded > UINT32_MAX)
    return 0u;
  return (uint32_t)padded;
}

static void store_string(uint8_t *output, uint32_t offset,
                         const uint16_t *text, uint32_t code_units) {
  uint32_t extent = padded_string_extent(code_units);
  uint32_t index;
  uint32_t raw_extent = 8u + code_units * 2u;
  store_u64_le(output, offset, (uint64_t)code_units);
  for (index = 0u; index < code_units; ++index) {
    uint16_t unit = text[index];
    output[offset + 8u + index * 2u] = (uint8_t)(unit & 0xffu);
    output[offset + 9u + index * 2u] = (uint8_t)(unit >> 8u);
  }
  for (index = raw_extent; index < extent; ++index)
    output[offset + index] = 0u;
}

static int store_none(uint8_t *output, uint32_t capacity,
                      uint32_t *output_extent) {
  if (capacity < 16u)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u); /* Result.Ok */
  store_u64_le(output, 8u, 1u); /* Option.None */
  *output_extent = 16u;
  return 0;
}

static int store_error(uint8_t *output, uint32_t capacity,
                       uint32_t error_tag, uint32_t *output_extent) {
  if (capacity < 16u)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 1u); /* Result.Error */
  store_u64_le(output, 8u, (uint64_t)error_tag);
  *output_extent = 16u;
  return 0;
}

static uint32_t sqlite_error_tag(sqlite3 *database) {
  int extended = database == NULL ? SQLITE_ERROR
                                  : sqlite3_extended_errcode(database);
  if (extended == 1811)
    return AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  if (extended == 1555 || extended == 2067)
    return AL_CAMPFIRE_DB_ERROR_CONSTRAINT_CONFLICT;
  if (extended == 787)
    return AL_CAMPFIRE_DB_ERROR_NOT_FOUND;
  if ((extended & 0xff) == 19)
    return AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
  return AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
}

static int is_ascii_digit(uint16_t value) {
  return value >= (uint16_t)'0' && value <= (uint16_t)'9';
}

static int read_digits(const uint16_t *text, uint32_t start, uint32_t count,
                       uint32_t *value) {
  uint32_t index;
  uint32_t result = 0u;
  for (index = 0u; index < count; ++index) {
    uint16_t character = text[start + index];
    if (!is_ascii_digit(character))
      return 0;
    result = result * 10u + (uint32_t)(character - (uint16_t)'0');
  }
  *value = result;
  return 1;
}

static int is_unicode_whitespace(uint32_t value) {
  if ((value >= 0x0009u && value <= 0x000du) || value == 0x0020u ||
      value == 0x0085u || value == 0x00a0u || value == 0x1680u ||
      (value >= 0x2000u && value <= 0x200au) || value == 0x2028u ||
      value == 0x2029u || value == 0x202fu || value == 0x205fu ||
      value == 0x3000u)
    return 1;
  return 0;
}

static int is_leap_year(uint32_t year) {
  return (year % 4u == 0u && year % 100u != 0u) || year % 400u == 0u;
}

static int try_parse_timestamp(const uint16_t *value, uint32_t value_length) {
  static const uint32_t month_days[] = { 31u, 28u, 31u, 30u, 31u, 30u,
                                         31u, 31u, 30u, 31u, 30u, 31u };
  uint32_t start = 0u;
  uint32_t end = value_length;
  uint32_t year, month, day, hour, minute, second;
  uint32_t fraction_microseconds = 0u;
  uint32_t offset_hours = 0u;
  uint32_t offset_minutes = 0u;
  int offset_sign = 1;
  uint32_t suffix;
  uint32_t month_day_limit;
  int64_t days;
  int64_t local_ticks;
  int64_t utc_ticks;
  uint32_t index;

  while (start < end && is_unicode_whitespace(value[start]))
    ++start;
  while (end > start && is_unicode_whitespace(value[end - 1u]))
    --end;
  if (end - start < 19u)
    return 0;

  if (value[start + 4u] != (uint16_t)'-' ||
      value[start + 7u] != (uint16_t)'-' ||
      (value[start + 10u] != (uint16_t)' ' &&
       value[start + 10u] != (uint16_t)'T') ||
      value[start + 13u] != (uint16_t)':' ||
      value[start + 16u] != (uint16_t)':')
    return 0;
  if (!read_digits(value, start, 4u, &year) ||
      !read_digits(value, start + 5u, 2u, &month) ||
      !read_digits(value, start + 8u, 2u, &day) ||
      !read_digits(value, start + 11u, 2u, &hour) ||
      !read_digits(value, start + 14u, 2u, &minute) ||
      !read_digits(value, start + 17u, 2u, &second))
    return 0;
  if (year == 0u || month == 0u || month > 12u || hour > 23u ||
      minute > 59u || second > 59u)
    return 0;
  month_day_limit = month_days[month - 1u];
  if (month == 2u && is_leap_year(year))
    month_day_limit = 29u;
  if (day == 0u || day > month_day_limit)
    return 0;

  suffix = start + 19u;
  if (suffix < end && value[suffix] == (uint16_t)'.') {
    uint32_t first_digit;
    uint32_t digit_count;
    ++suffix;
    first_digit = suffix;
    while (suffix < end && is_ascii_digit(value[suffix]))
      ++suffix;
    digit_count = suffix - first_digit;
    if (digit_count == 0u)
      return 0;
    for (index = 0u; index < digit_count && index < 6u; ++index)
      fraction_microseconds = fraction_microseconds * 10u +
          (uint32_t)(value[first_digit + index] - (uint16_t)'0');
    for (; index < 6u; ++index)
      fraction_microseconds *= 10u;
  }

  if (suffix == end) {
    offset_sign = 1;
  } else if (end - suffix == 1u && value[suffix] == (uint16_t)'Z') {
    offset_sign = 1;
  } else if (end - suffix == 4u && value[suffix] == (uint16_t)' ' &&
             value[suffix + 1u] == (uint16_t)'U' &&
             value[suffix + 2u] == (uint16_t)'T' &&
             value[suffix + 3u] == (uint16_t)'C') {
    offset_sign = 1;
  } else if (end - suffix == 6u &&
             (value[suffix] == (uint16_t)'+' ||
              value[suffix] == (uint16_t)'-') &&
             value[suffix + 3u] == (uint16_t)':') {
    offset_sign = value[suffix] == (uint16_t)'-' ? -1 : 1;
    if (!read_digits(value, suffix + 1u, 2u, &offset_hours) ||
        !read_digits(value, suffix + 4u, 2u, &offset_minutes) ||
        offset_hours > 14u || offset_minutes > 59u ||
        (offset_hours == 14u && offset_minutes != 0u))
      return 0;
  } else {
    return 0;
  }

  /* Match DateTimeOffset's supported UTC range after applying the offset. */
  days = (int64_t)(year - 1u) * 365 + (int64_t)((year - 1u) / 4u) -
         (int64_t)((year - 1u) / 100u) +
         (int64_t)((year - 1u) / 400u);
  for (index = 1u; index < month; ++index) {
    days += (int64_t)month_days[index - 1u];
    if (index == 2u && is_leap_year(year))
      ++days;
  }
  days += (int64_t)day - 1;
  local_ticks = days * SQLITE_TICKS_PER_DAY +
      (int64_t)hour * 3600 * SQLITE_TICKS_PER_SECOND +
      (int64_t)minute * 60 * SQLITE_TICKS_PER_SECOND +
      (int64_t)second * SQLITE_TICKS_PER_SECOND +
      (int64_t)fraction_microseconds * 10;
  utc_ticks = local_ticks -
      (int64_t)(offset_sign * (int)(offset_hours * 60u + offset_minutes)) *
          SQLITE_TICKS_PER_MINUTE;
  return utc_ticks >= 0 && utc_ticks <= DATETIME_MAX_TICKS;
}

static int decode_utf8(const uint8_t *text, uint32_t length, uint32_t *offset,
                       uint32_t *codepoint) {
  uint32_t index = *offset;
  uint8_t first;
  uint32_t result;
  uint32_t continuation_count;
  uint32_t minimum;
  uint32_t part;

  if (index >= length)
    return 0;
  first = text[index++];
  if (first < 0x80u) {
    result = first;
    continuation_count = 0u;
    minimum = 0u;
  } else if (first >= 0xc2u && first <= 0xdfu) {
    result = first & 0x1fu;
    continuation_count = 1u;
    minimum = 0x80u;
  } else if (first >= 0xe0u && first <= 0xefu) {
    result = first & 0x0fu;
    continuation_count = 2u;
    minimum = 0x800u;
  } else if (first >= 0xf0u && first <= 0xf4u) {
    result = first & 0x07u;
    continuation_count = 3u;
    minimum = 0x10000u;
  } else {
    return 0;
  }

  for (part = 0u; part < continuation_count; ++part) {
    uint8_t next;
    if (index >= length)
      return 0;
    next = text[index++];
    if ((next & 0xc0u) != 0x80u)
      return 0;
    result = (result << 6u) | (uint32_t)(next & 0x3fu);
  }
  if ((continuation_count != 0u && result < minimum) ||
      result > 0x10ffffu || (result >= 0xd800u && result <= 0xdfffu))
    return 0;
  *offset = index;
  *codepoint = result;
  return 1;
}

static int valid_database_path(const uint8_t *path, uint32_t path_bytes) {
  uint32_t offset = 0u;
  int only_whitespace = 1;
  static const uint8_t memory_name[] = { ':', 'm', 'e', 'm', 'o', 'r', 'y', ':' };

  if (path == NULL || path_bytes == 0u ||
      path_bytes > AL_CAMPFIRE_ROOM_SQLITE_PATH_LIMIT_BYTES)
    return 0;
  if (path[path_bytes] != 0u)
    return 0;
  if (path_bytes == sizeof(memory_name) &&
      byte_sequence_equal(path, memory_name, (uint32_t)sizeof(memory_name)))
    return 0;
  if (path_bytes >= 5u &&
      byte_sequence_equal(path, (const uint8_t *)"file:", 5u))
    return 0;
  while (offset < path_bytes) {
    uint32_t codepoint;
    if (!decode_utf8(path, path_bytes, &offset, &codepoint) ||
        codepoint == 0u)
      return 0;
    if (!is_unicode_whitespace(codepoint))
      only_whitespace = 0;
  }
  return !only_whitespace;
}

static int read_column_text16(sqlite3 *database, sqlite3_stmt *statement,
                              int column, const uint16_t **out_text,
                              uint32_t *out_code_units) {
  const void *value;
  int byte_count;
  if (sqlite3_column_type(statement, column) == SQLITE_NULL)
    return 0;
  value = sqlite3_column_text16(statement, column);
  byte_count = sqlite3_column_bytes16(statement, column);
  if (value == NULL || byte_count < 0 || (byte_count & 1) != 0 ||
      sqlite3_errcode(database) == SQLITE_NOMEM)
    return -1;
  *out_text = (const uint16_t *)value;
  *out_code_units = (uint32_t)byte_count / 2u;
  return 1;
}

static int room_kind_tag(const uint16_t *text, uint32_t length,
                         uint32_t *tag) {
  static const uint16_t open_kind[] = { 'R', 'o', 'o', 'm', 's', ':', ':',
                                        'O', 'p', 'e', 'n' };
  static const uint16_t closed_kind[] = { 'R', 'o', 'o', 'm', 's', ':', ':',
                                          'C', 'l', 'o', 's', 'e', 'd' };
  static const uint16_t direct_kind[] = { 'R', 'o', 'o', 'm', 's', ':', ':',
                                          'D', 'i', 'r', 'e', 'c', 't' };
  if (length == sizeof(open_kind) / sizeof(open_kind[0]) &&
      code_unit_sequence_equal(text, open_kind,
                               (uint32_t)(sizeof(open_kind) / sizeof(open_kind[0])))) {
    *tag = 0u;
    return 1;
  }
  if (length == sizeof(closed_kind) / sizeof(closed_kind[0]) &&
      code_unit_sequence_equal(
          text, closed_kind,
          (uint32_t)(sizeof(closed_kind) / sizeof(closed_kind[0])))) {
    *tag = 1u;
    return 1;
  }
  if (length == sizeof(direct_kind) / sizeof(direct_kind[0]) &&
      code_unit_sequence_equal(
          text, direct_kind,
          (uint32_t)(sizeof(direct_kind) / sizeof(direct_kind[0])))) {
    *tag = 2u;
    return 1;
  }
  return 0;
}

static int output_extent_for_row(uint32_t name_extent, uint32_t created_extent,
                                 uint32_t updated_extent, uint32_t *extent) {
  uint64_t total = UINT64_C(48) + (uint64_t)name_extent +
                   (uint64_t)created_extent + (uint64_t)updated_extent;
  if (total > UINT32_MAX)
    return 0;
  *extent = (uint32_t)total;
  return 1;
}

static int32_t fail_provider_context(al_campfire_room_sqlite_context_v1 *ctx,
                                     uint8_t *output, uint32_t capacity,
                                     uint32_t *output_extent) {
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  int status;
  if (database != NULL)
    error_tag = sqlite_error_tag(database);
  status = store_error(output, capacity, error_tag, output_extent);
  return status;
}

int32_t al_campfire_room_sqlite_context_init_v1(
    al_campfire_room_sqlite_context_v1 *ctx,
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    uint32_t *out_db_error_tag) {
  sqlite3 *database = NULL;
  sqlite3_stmt *statement = NULL;
  const char *tail = NULL;
  int rc;
  int foreign_keys_enabled = 0;
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;

  if (out_db_error_tag == NULL)
    return HOST_BOUNDARY_FAILURE;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  if (ctx == NULL)
    return HOST_BOUNDARY_FAILURE;
  if (ctx->cookie == ROOM_CONTEXT_COOKIE) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
    return HOST_BOUNDARY_FAILURE;
  }
  clear_bytes(ctx, sizeof(*ctx));
  if (!valid_database_path(canonical_db_path_utf8, path_bytes)) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
    return HOST_BOUNDARY_FAILURE;
  }

  rc = sqlite3_open_v2((const char *)canonical_db_path_utf8, &database,
                       SQLITE_OPEN_FLAGS, NULL);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }

  rc = sqlite3_extended_result_codes(database, 1);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }
  rc = sqlite3_busy_timeout(database, SQLITE_BUSY_TIMEOUT_MS);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }
  rc = sqlite3_db_config(database, SQLITE_DBCONFIG_ENABLE_FKEY, 1,
                         &foreign_keys_enabled);
  if (rc != SQLITE_OK || foreign_keys_enabled != 1) {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }
  rc = sqlite3_prepare_v2(database, room_query, -1, &statement, &tail);
  if (rc != SQLITE_OK || statement == NULL || tail == NULL || *tail != '\0') {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }

  ctx->opaque_db = database;
  ctx->opaque_find_stmt = statement;
  ctx->cookie = ROOM_CONTEXT_COOKIE;
  ctx->owner_thread_id = GetCurrentThreadId();
  ctx->active = 0u;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  return 0;

failed:
  if (statement != NULL)
    (void)sqlite3_finalize(statement);
  if (database != NULL)
    (void)sqlite3_close(database);
  clear_bytes(ctx, sizeof(*ctx));
  *out_db_error_tag = error_tag;
  return HOST_BOUNDARY_FAILURE;
}

int32_t al_campfire_room_sqlite_context_destroy_v1(
    al_campfire_room_sqlite_context_v1 *ctx) {
  sqlite3 *database;
  sqlite3_stmt *statement;
  int rc;
  if (ctx == NULL)
    return HOST_BOUNDARY_FAILURE;
  if (ctx->cookie == 0u && ctx->opaque_db == NULL &&
      ctx->opaque_find_stmt == NULL && ctx->owner_thread_id == 0u &&
      ctx->active == 0u)
    return 0;
  if (ctx->cookie != ROOM_CONTEXT_COOKIE ||
      ctx->owner_thread_id != GetCurrentThreadId() ||
      __atomic_load_n(&ctx->active, __ATOMIC_ACQUIRE) != 0u)
    return HOST_BOUNDARY_FAILURE;

  database = (sqlite3 *)ctx->opaque_db;
  statement = (sqlite3_stmt *)ctx->opaque_find_stmt;
  if (statement != NULL) {
    (void)sqlite3_finalize(statement);
    ctx->opaque_find_stmt = NULL;
  }
  rc = database == NULL ? SQLITE_OK : sqlite3_close(database);
  if (rc != SQLITE_OK)
    return HOST_BOUNDARY_FAILURE;
  clear_bytes(ctx, sizeof(*ctx));
  return 0;
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdll-attribute-on-redeclaration"
AL_CAMPFIRE_EXPORT int32_t al_host_campfire_rooms_find_for_user_v1(
    void *provider_context, int64_t user_id, int64_t room_id, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_room_sqlite_context_v1 *ctx =
      (al_campfire_room_sqlite_context_v1 *)provider_context;
  sqlite3 *database;
  sqlite3_stmt *statement;
  uint32_t expected_active = 0u;
  int rc;
  int32_t host_status = 0;
  uint32_t result_error = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;

  if (output_extent_bytes == NULL)
    return HOST_BOUNDARY_FAILURE;
  *output_extent_bytes = 0u;
  if (ctx == NULL || output == NULL ||
      output_capacity_bytes > AL_CAMPFIRE_ROOM_HOST_OUTPUT_LIMIT ||
      output_capacity_bytes < 16u || ctx->cookie != ROOM_CONTEXT_COOKIE ||
      ctx->opaque_db == NULL || ctx->opaque_find_stmt == NULL ||
      ctx->owner_thread_id != GetCurrentThreadId())
    return HOST_BOUNDARY_FAILURE;
  if (!__atomic_compare_exchange_n(&ctx->active, &expected_active, 1u, 0,
                                   __ATOMIC_ACQUIRE, __ATOMIC_RELAXED))
    return HOST_BOUNDARY_FAILURE;

  database = (sqlite3 *)ctx->opaque_db;
  statement = (sqlite3_stmt *)ctx->opaque_find_stmt;
  (void)sqlite3_reset(statement);
  rc = sqlite3_clear_bindings(statement);
  if (rc != SQLITE_OK) {
    host_status = fail_provider_context(ctx, output, output_capacity_bytes,
                                        output_extent_bytes);
    goto finished;
  }
  rc = sqlite3_bind_int64(statement, 1, (sqlite3_int64)user_id);
  if (rc == SQLITE_OK)
    rc = sqlite3_bind_int64(statement, 2, (sqlite3_int64)room_id);
  if (rc != SQLITE_OK) {
    host_status = fail_provider_context(ctx, output, output_capacity_bytes,
                                        output_extent_bytes);
    goto finished;
  }

  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE) {
    host_status = store_none(output, output_capacity_bytes,
                             output_extent_bytes);
    goto finished;
  }
  if (rc != SQLITE_ROW) {
    host_status = fail_provider_context(ctx, output, output_capacity_bytes,
                                        output_extent_bytes);
    goto finished;
  }

  {
    int name_state;
    int kind_state;
    int created_state;
    int updated_state;
    int64_t row_id;
    int64_t creator_id;
    const uint16_t *name = NULL;
    const uint16_t *kind = NULL;
    const uint16_t *created = NULL;
    const uint16_t *updated = NULL;
    uint32_t name_length = 0u;
    uint32_t kind_length = 0u;
    uint32_t created_length = 0u;
    uint32_t updated_length = 0u;
    uint32_t name_tag = 1u;
    uint32_t kind_tag = 0u;
    uint32_t name_extent = 0u;
    uint32_t created_extent;
    uint32_t updated_extent;
    uint32_t total_extent;
    uint32_t cursor = 0u;

    if (sqlite3_column_type(statement, 0) == SQLITE_NULL ||
        sqlite3_column_type(statement, 3) == SQLITE_NULL) {
      host_status = store_error(output, output_capacity_bytes,
                                AL_CAMPFIRE_DB_ERROR_UNAVAILABLE,
                                output_extent_bytes);
      goto finished;
    }
    row_id = sqlite3_column_int64(statement, 0);
    name_state = read_column_text16(database, statement, 1, &name,
                                    &name_length);
    kind_state = read_column_text16(database, statement, 2, &kind,
                                    &kind_length);
    creator_id = sqlite3_column_int64(statement, 3);
    created_state = read_column_text16(database, statement, 4, &created,
                                       &created_length);
    updated_state = read_column_text16(database, statement, 5, &updated,
                                       &updated_length);

    if (name_state < 0 || kind_state <= 0 || created_state <= 0 ||
        updated_state <= 0 || !room_kind_tag(kind, kind_length, &kind_tag) ||
        !try_parse_timestamp(created, created_length) ||
        !try_parse_timestamp(updated, updated_length)) {
      result_error = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
      host_status = store_error(output, output_capacity_bytes, result_error,
                                output_extent_bytes);
      goto finished;
    }
    if (name_state > 0) {
      name_tag = 0u;
      name_extent = padded_string_extent(name_length);
      if (name_extent == 0u) {
        host_status = HOST_BOUNDARY_FAILURE;
        goto finished;
      }
    }
    created_extent = padded_string_extent(created_length);
    updated_extent = padded_string_extent(updated_length);
    if (created_extent == 0u || updated_extent == 0u ||
        !output_extent_for_row(name_extent, created_extent, updated_extent,
                               &total_extent) ||
        total_extent > output_capacity_bytes) {
      host_status = HOST_BOUNDARY_FAILURE;
      goto finished;
    }

    store_u64_le(output, cursor, 0u); /* Result.Ok */
    cursor += 8u;
    store_u64_le(output, cursor, 0u); /* Option.Some */
    cursor += 8u;
    store_u64_le(output, cursor, (uint64_t)row_id);
    cursor += 8u;
    store_u64_le(output, cursor, (uint64_t)name_tag);
    cursor += 8u;
    if (name_state > 0) {
      store_string(output, cursor, name, name_length);
      cursor += name_extent;
    }
    store_u64_le(output, cursor, (uint64_t)kind_tag);
    cursor += 8u;
    store_u64_le(output, cursor, (uint64_t)creator_id);
    cursor += 8u;
    store_string(output, cursor, created, created_length);
    cursor += created_extent;
    store_string(output, cursor, updated, updated_length);
    cursor += updated_extent;
    *output_extent_bytes = cursor;
  }

finished:
  (void)sqlite3_reset(statement);
  (void)sqlite3_clear_bindings(statement);
  __atomic_store_n(&ctx->active, 0u, __ATOMIC_RELEASE);
  return host_status;
}
#pragma clang diagnostic pop

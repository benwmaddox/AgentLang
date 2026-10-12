#include "room_read_adapter.h"
#include "room_read_pure.h"
#include "room_read_sqlite_subset.h"

#include <limits.h>
#include <stddef.h>

#if !defined(_WIN32)
#error "The Campfire native room-read adapter currently requires Windows x64."
#endif

#define SQLITE_BUSY_TIMEOUT_MS 5000
#define SQLITE_MINIMUM_VERSION_NUMBER 3051003
#define SQLITE_SYNCHRONOUS_FULL 2
#define SQLITE_SYNCHRONOUS_EXTRA 3
#define WAL_AUTO_CHECKPOINT_PAGES 1000
#define SQLITE_OPEN_FLAGS \
  (SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_PRIVATECACHE)
#define ROOM_READ_CONTEXT_COOKIE UINT64_C(0x4346525245414431)
#define HOST_BOUNDARY_FAILURE 1
#define MAX_LAYOUT_DEPTH 64u
#define MAX_LAST_PAGE_ROWS 40u
#define UNIX_MICROS_PER_SECOND INT64_C(1000000)
#define UNIX_MICROS_PER_MINUTE (INT64_C(60) * UNIX_MICROS_PER_SECOND)
#define UNIX_MICROS_PER_DAY (INT64_C(86400) * UNIX_MICROS_PER_SECOND)
#define UNIX_EPOCH_TICKS INT64_C(621355968000000000)
#define DATETIME_MAX_TICKS INT64_C(3155378975999999999)
#define UtcInstant_MIN_MICROS INT64_C(-62135596800000000)
#define UtcInstant_MAX_MICROS INT64_C(253402300799999999)
#define ROOM_READ_CONTEXT_STATE_ACTIVE 1u
#define ROOM_READ_CONTEXT_STATE_POISONED 2u

extern __declspec(dllimport) unsigned long __stdcall GetCurrentThreadId(void);

static const char *const room_read_queries[AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT] = {
    "SELECT id, user_id, token, ip_address, user_agent, last_active_at, "
    "created_at, updated_at FROM sessions WHERE token = $token LIMIT 1",
    "SELECT id, user_id, token, ip_address, user_agent, last_active_at, "
    "created_at, updated_at FROM sessions WHERE id = $sessionId LIMIT 1",
    "UPDATE sessions SET ip_address = $ipAddress, user_agent = $userAgent, "
    "last_active_at = $now, updated_at = $now "
    "WHERE id = $sessionId AND last_active_at = $observedLastActiveAt",
    "SELECT id, name, role, status FROM users WHERE id = $userId LIMIT 1",
    "SELECT id, name, role, status FROM users "
    "WHERE status = 0 AND role = 2 AND id = $id AND bot_token = $token LIMIT 1",
    "SELECT rooms.id, rooms.name, rooms.type, rooms.creator_id, "
    "rooms.created_at, rooms.updated_at "
    "FROM rooms INNER JOIN memberships ON rooms.id = memberships.room_id "
    "WHERE memberships.user_id = $userId AND rooms.id = $roomId LIMIT 1",
    "SELECT EXISTS (SELECT 1 FROM \"memberships\" "
    "WHERE \"user_id\" = $userId AND \"room_id\" = $roomId)",
    "SELECT EXISTS (SELECT 1 FROM \"rooms\" WHERE \"id\" = $roomId)",
    "SELECT messages.id, messages.room_id, messages.creator_id, "
    "messages.client_message_id, rich_text.body, messages.created_at, "
    "messages.updated_at FROM messages LEFT JOIN action_text_rich_texts "
    "AS rich_text ON rich_text.record_id = messages.id AND "
    "rich_text.record_type = 'Message' AND rich_text.name = 'body' "
    "WHERE messages.room_id = $roomId "
    "ORDER BY messages.created_at DESC LIMIT 40",
    "BEGIN DEFERRED",
    "BEGIN IMMEDIATE",
    "COMMIT",
    "ROLLBACK"};

typedef struct al_room_read_text {
  const uint8_t *bytes;
  uint32_t code_units;
} al_room_read_text;

typedef struct al_room_read_session_row {
  int64_t id;
  int64_t user_id;
  al_room_read_text token;
  int ip_state;
  al_room_read_text ip_address;
  int agent_state;
  al_room_read_text user_agent;
  al_room_read_text last_active_at_text;
  al_room_read_text created_at_text;
  al_room_read_text updated_at_text;
  int64_t last_active_at_micros;
  int64_t created_at_micros;
  int64_t updated_at_micros;
  const uint16_t *observed_last_active_at;
  int observed_last_active_bytes;
} al_room_read_session_row;

typedef struct al_room_read_user_row {
  int64_t id;
  al_room_read_text name;
  uint32_t role;
  uint32_t status;
} al_room_read_user_row;

typedef struct al_room_read_room_row {
  int64_t id;
  int name_state;
  al_room_read_text name;
  uint32_t kind;
  int64_t creator_id;
  al_room_read_text created_at;
  al_room_read_text updated_at;
} al_room_read_room_row;

typedef struct al_room_read_message_row {
  int64_t id;
  int64_t room_id;
  int64_t creator_id;
  al_room_read_text client_message_id;
  int body_state;
  al_room_read_text body;
  al_room_read_text created_at;
  al_room_read_text updated_at;
} al_room_read_message_row;

typedef struct al_room_read_refresh_input {
  int64_t session_id;
  int64_t cutoff;
  int64_t now;
  const uint8_t *ip_option;
  uint32_t ip_option_extent;
  const uint8_t *agent_option;
  uint32_t agent_option_extent;
} al_room_read_refresh_input;

typedef struct al_room_read_message_extent {
  uint32_t extent;
  uint32_t output_offset;
  int64_t message_id;
} al_room_read_message_extent;

static void clear_bytes(void *memory, size_t count) {
  volatile uint8_t *bytes = (volatile uint8_t *)memory;
  size_t index;
  for (index = 0u; index < count; ++index)
    bytes[index] = 0u;
}

static uint64_t load_u64_le(const uint8_t *bytes) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[index] << (index * 8u);
  return value;
}

static uint16_t load_u16_le(const uint8_t *bytes) {
  return (uint16_t)((uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8u));
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

static int measure_value(const al_owning_layout *layout, uint32_t type_index,
                         const uint8_t *data, uint32_t available,
                         uint32_t *out_extent, uint32_t depth);

static int valid_layout(const al_owning_layout *layout) {
  return layout != NULL &&
         layout->abi_version == AL_OWNING_LAYOUT_ABI_VERSION &&
         layout->type_count != 0u &&
         layout->type_count <= AL_OWNING_LAYOUT_MAX_TYPES &&
         layout->types != NULL &&
         layout->field_count <= AL_OWNING_LAYOUT_MAX_FIELDS &&
         (layout->field_count == 0u || layout->fields != NULL);
}

static int get_type(const al_owning_layout *layout, uint32_t type_index,
                    const al_owning_type_descriptor **out_type) {
  if (!valid_layout(layout) || type_index >= layout->type_count)
    return 0;
  *out_type = &layout->types[type_index];
  return 1;
}

static int get_field(const al_owning_layout *layout,
                     const al_owning_type_descriptor *type,
                     uint32_t field_index,
                     const al_owning_field_descriptor **out_field) {
  uint64_t index = (uint64_t)type->first_field + field_index;
  if (field_index >= type->field_count || index >= layout->field_count)
    return 0;
  *out_field = &layout->fields[(uint32_t)index];
  return 1;
}

static int measure_value(const al_owning_layout *layout, uint32_t type_index,
                         const uint8_t *data, uint32_t available,
                         uint32_t *out_extent, uint32_t depth) {
  const al_owning_type_descriptor *type;
  uint32_t extent = 0u;
  uint32_t index;
  if (data == NULL || out_extent == NULL || depth >= MAX_LAYOUT_DEPTH ||
      !get_type(layout, type_index, &type))
    return 0;

  switch (type->kind) {
  case AL_OWNING_TYPE_I64:
  case AL_OWNING_TYPE_BOOL:
  case AL_OWNING_TYPE_UNIT:
  case AL_OWNING_TYPE_F64:
    if (type->fixed_extent_bytes != 8u || available < 8u)
      return 0;
    if (type->kind == AL_OWNING_TYPE_BOOL && load_u64_le(data) > 1u)
      return 0;
    extent = 8u;
    break;
  case AL_OWNING_TYPE_ENUM:
    if (type->fixed_extent_bytes != 8u || type->case_count == 0u ||
        type->case_count > 64u || available < 8u ||
        load_u64_le(data) >= type->case_count)
      return 0;
    extent = 8u;
    break;
  case AL_OWNING_TYPE_STRING: {
    uint64_t code_units;
    uint64_t raw;
    uint64_t padded;
    uint32_t pad_index;
    if (available < 8u)
      return 0;
    code_units = load_u64_le(data);
    if (code_units > (UINT32_MAX - 16u) / 2u)
      return 0;
    raw = UINT64_C(8) + code_units * UINT64_C(2);
    padded = (raw + UINT64_C(7)) & ~UINT64_C(7);
    if (padded > available || padded > UINT32_MAX)
      return 0;
    for (pad_index = (uint32_t)raw; pad_index < (uint32_t)padded;
         ++pad_index) {
      if (data[pad_index] != 0u)
        return 0;
    }
    extent = (uint32_t)padded;
    break;
  }
  case AL_OWNING_TYPE_RECORD: {
    uint32_t offset = 0u;
    if (type->field_count == 0u) {
      if (type->fixed_extent_bytes != 8u || available < 8u)
        return 0;
      extent = 8u;
      break;
    }
    for (index = 0u; index < type->field_count; ++index) {
      const al_owning_field_descriptor *field;
      uint32_t child_extent;
      if (!get_field(layout, type, index, &field) ||
          field->reserved != 0u ||
          (field->flags & ~AL_OWNING_FIELD_ZERO_WIDTH) != 0u ||
          (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
           field->fixed_offset_bytes != offset))
        return 0;
      if (field->flags & AL_OWNING_FIELD_ZERO_WIDTH) {
        if (field->child_type_index >= layout->type_count)
          return 0;
        continue;
      }
      if (field->child_type_index >= layout->type_count || offset > available ||
          !measure_value(layout, field->child_type_index, data + offset,
                         available - offset, &child_extent, depth + 1u) ||
          child_extent > UINT32_MAX - offset)
        return 0;
      offset += child_extent;
    }
    if (offset == 0u)
      offset = 8u;
    if (available < offset)
      return 0;
    if (type->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
        type->fixed_extent_bytes != offset)
      return 0;
    extent = offset;
    break;
  }
  case AL_OWNING_TYPE_OPTION:
  case AL_OWNING_TYPE_RESULT: {
    uint64_t tag;
    const al_owning_field_descriptor *field;
    uint32_t child_extent;
    if (available < 8u || type->case_count == 0u ||
        type->case_count != type->field_count || type->case_count > 64u)
      return 0;
    tag = load_u64_le(data);
    if (tag >= type->case_count ||
        !get_field(layout, type, (uint32_t)tag, &field) ||
        field->fixed_offset_bytes != 8u || field->reserved != 0u ||
        field->flags != 0u)
      return 0;
    if (field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32) {
      if (type->kind != AL_OWNING_TYPE_OPTION || tag != 1u)
        return 0;
      extent = 8u;
    } else {
      if (field->child_type_index >= layout->type_count ||
          !measure_value(layout, field->child_type_index, data + 8u,
                         available - 8u, &child_extent, depth + 1u) ||
          child_extent > UINT32_MAX - 8u)
        return 0;
      extent = 8u + child_extent;
    }
    if (type->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
        type->fixed_extent_bytes != extent)
      return 0;
    break;
  }
  case AL_OWNING_TYPE_LIST: {
    const al_owning_field_descriptor *field;
    uint64_t count;
    uint32_t cursor = 8u;
    if (available < 8u || type->field_count != 1u || type->case_count != 0u ||
        !get_field(layout, type, 0u, &field) ||
        field->fixed_offset_bytes != 8u || field->flags != 0u ||
        field->reserved != 0u ||
        field->child_type_index >= layout->type_count)
      return 0;
    count = load_u64_le(data);
    if (count > (available - 8u) / 8u)
      return 0;
    for (index = 0u; index < (uint32_t)count; ++index) {
      uint32_t child_extent;
      if (cursor > available ||
          !measure_value(layout, field->child_type_index, data + cursor,
                         available - cursor, &child_extent, depth + 1u) ||
          child_extent > UINT32_MAX - cursor)
        return 0;
      cursor += child_extent;
    }
    extent = cursor;
    if (type->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
        type->fixed_extent_bytes != extent)
      return 0;
    break;
  }
  default:
    return 0;
  }

  *out_extent = extent;
  return 1;
}

static int input_matches(const al_owning_layout *layout,
                         const al_campfire_room_read_input *input,
                         uint32_t expected_kind) {
  const al_owning_type_descriptor *type;
  uint32_t extent;
  return input != NULL && input->data != NULL &&
         get_type(layout, input->type_index, &type) &&
         type->kind == expected_kind &&
         measure_value(layout, input->type_index, input->data,
                       input->extent_bytes, &extent, 0u) &&
         extent == input->extent_bytes;
}

static int record_field_location(const al_owning_layout *layout,
                                 uint32_t record_type_index,
                                 const uint8_t *record_data,
                                 uint32_t record_extent,
                                 uint32_t field_index, uint32_t *out_offset,
                                 uint32_t *out_child_type_index) {
  const al_owning_type_descriptor *record_type;
  uint32_t offset = 0u;
  uint32_t index;
  if (!get_type(layout, record_type_index, &record_type) ||
      record_type->kind != AL_OWNING_TYPE_RECORD ||
      field_index >= record_type->field_count)
    return 0;
  for (index = 0u; index <= field_index; ++index) {
    const al_owning_field_descriptor *field;
    uint32_t child_extent;
    if (!get_field(layout, record_type, index, &field) ||
        field->reserved != 0u ||
        field->flags != 0u ||
        (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
         field->fixed_offset_bytes != offset))
      return 0;
    if (index == field_index) {
      *out_offset = offset;
      *out_child_type_index = field->child_type_index;
      return 1;
    }
    if (field->flags & AL_OWNING_FIELD_ZERO_WIDTH)
      continue;
    if (field->child_type_index >= layout->type_count || offset > record_extent ||
        !measure_value(layout, field->child_type_index,
                       record_data + offset, record_extent - offset,
                       &child_extent, 0u) ||
        child_extent > UINT32_MAX - offset)
      return 0;
    offset += child_extent;
  }
  return 0;
}

static int record_field_type(const al_owning_layout *layout,
                             uint32_t record_type_index,
                             uint32_t field_index,
                             uint32_t *out_child_type_index) {
  const al_owning_type_descriptor *record_type;
  const al_owning_field_descriptor *field;
  if (!get_type(layout, record_type_index, &record_type) ||
      record_type->kind != AL_OWNING_TYPE_RECORD ||
      !get_field(layout, record_type, field_index, &field) ||
      field->reserved != 0u ||
      field->flags != 0u ||
      field->child_type_index >= layout->type_count)
    return 0;
  *out_child_type_index = field->child_type_index;
  return 1;
}

static int type_has_kind(const al_owning_layout *layout, uint32_t type_index,
                         uint32_t kind) {
  const al_owning_type_descriptor *type;
  return get_type(layout, type_index, &type) && type->kind == kind;
}

static int enum_has_cases(const al_owning_layout *layout, uint32_t type_index,
                          uint32_t case_count) {
  const al_owning_type_descriptor *type;
  return get_type(layout, type_index, &type) &&
         type->kind == AL_OWNING_TYPE_ENUM && type->field_count == 0u &&
         type->case_count == case_count && type->fixed_extent_bytes == 8u;
}

static int option_payload_type(const al_owning_layout *layout,
                               uint32_t option_type_index,
                               uint32_t expected_payload_kind,
                               uint32_t *out_payload_type) {
  const al_owning_type_descriptor *type;
  const al_owning_field_descriptor *some_field;
  const al_owning_field_descriptor *none_field;
  if (!get_type(layout, option_type_index, &type) ||
      type->kind != AL_OWNING_TYPE_OPTION || type->field_count != 2u ||
      type->case_count != 2u ||
      !get_field(layout, type, 0u, &some_field) ||
      !get_field(layout, type, 1u, &none_field) ||
      some_field->flags != 0u || none_field->flags != 0u ||
      some_field->reserved != 0u || none_field->reserved != 0u ||
      some_field->fixed_offset_bytes != 8u ||
      none_field->fixed_offset_bytes != 8u ||
      some_field->child_type_index >= layout->type_count ||
      none_field->child_type_index != AL_OWNING_LAYOUT_DYNAMIC_U32 ||
      !type_has_kind(layout, some_field->child_type_index,
                     expected_payload_kind))
    return 0;
  *out_payload_type = some_field->child_type_index;
  return 1;
}

static int record_has_kinds(const al_owning_layout *layout,
                            uint32_t record_type_index,
                            const uint32_t *expected_kinds,
                            uint32_t field_count) {
  const al_owning_type_descriptor *record_type;
  uint32_t index;
  if (!get_type(layout, record_type_index, &record_type) ||
      record_type->kind != AL_OWNING_TYPE_RECORD ||
      record_type->field_count != field_count)
    return 0;
  for (index = 0u; index < field_count; ++index) {
    uint32_t child_type;
    if (!record_field_type(layout, record_type_index, index, &child_type))
      return 0;
    if (expected_kinds[index] == AL_OWNING_TYPE_OPTION) {
      uint32_t option_payload;
      if (!option_payload_type(layout, child_type, AL_OWNING_TYPE_STRING,
                               &option_payload))
        return 0;
    } else if (expected_kinds[index] == (uint32_t)-1) {
      const al_owning_type_descriptor *enum_type;
      if (!get_type(layout, child_type, &enum_type) ||
          enum_type->kind != AL_OWNING_TYPE_ENUM ||
          enum_type->fixed_extent_bytes != 8u || enum_type->case_count != 3u)
        return 0;
    } else if (!type_has_kind(layout, child_type, expected_kinds[index])) {
      return 0;
    }
  }
  return 1;
}

static int result_success_type(const al_owning_layout *layout,
                               uint32_t result_type_index,
                               uint32_t expected_success_kind,
                               uint32_t *out_success_type) {
  const al_owning_type_descriptor *result_type;
  const al_owning_field_descriptor *ok_field;
  const al_owning_field_descriptor *error_field;
  if (!get_type(layout, result_type_index, &result_type) ||
      result_type->kind != AL_OWNING_TYPE_RESULT ||
      result_type->field_count != 2u || result_type->case_count != 2u ||
      !get_field(layout, result_type, 0u, &ok_field) ||
      !get_field(layout, result_type, 1u, &error_field) ||
      ok_field->flags != 0u || error_field->flags != 0u ||
      ok_field->reserved != 0u || error_field->reserved != 0u ||
      ok_field->fixed_offset_bytes != 8u ||
      error_field->fixed_offset_bytes != 8u ||
      ok_field->child_type_index >= layout->type_count ||
      error_field->child_type_index >= layout->type_count ||
      !enum_has_cases(layout, error_field->child_type_index, 4u) ||
      !type_has_kind(layout, ok_field->child_type_index,
                     expected_success_kind))
    return 0;
  *out_success_type = ok_field->child_type_index;
  return 1;
}

static int validate_sql_output_type(const al_owning_layout *layout,
                                    uint32_t operation,
                                    uint32_t output_type_index) {
  uint32_t success_index;
  uint32_t payload_index;
  static const uint32_t session_fields[] = {
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_I64, AL_OWNING_TYPE_STRING,
      AL_OWNING_TYPE_OPTION, AL_OWNING_TYPE_OPTION, AL_OWNING_TYPE_I64,
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_I64};
  static const uint32_t user_fields[] = {
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_STRING, (uint32_t)-1, (uint32_t)-1};
  static const uint32_t room_fields[] = {
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_OPTION, (uint32_t)-1,
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_STRING, AL_OWNING_TYPE_STRING};
  static const uint32_t message_fields[] = {
      AL_OWNING_TYPE_I64, AL_OWNING_TYPE_I64, AL_OWNING_TYPE_I64,
      AL_OWNING_TYPE_STRING, AL_OWNING_TYPE_OPTION,
      AL_OWNING_TYPE_STRING, AL_OWNING_TYPE_STRING};
  uint32_t option_payload;

  if (operation == AL_CAMPFIRE_MEMBERSHIP_EXISTS)
    return result_success_type(layout, output_type_index, AL_OWNING_TYPE_BOOL,
                                &success_index);
  if (operation == AL_CAMPFIRE_MESSAGES_LAST_PAGE) {
    const al_owning_type_descriptor *list_type;
    const al_owning_field_descriptor *element_field;
    if (!result_success_type(layout, output_type_index, AL_OWNING_TYPE_LIST,
                             &success_index) ||
        !get_type(layout, success_index, &list_type) ||
        list_type->field_count != 1u ||
        !get_field(layout, list_type, 0u, &element_field) ||
        element_field->fixed_offset_bytes != 8u ||
        element_field->child_type_index >= layout->type_count ||
        !record_has_kinds(layout, element_field->child_type_index,
                          message_fields, 7u))
      return 0;
    return 1;
  }
  if (operation == AL_CAMPFIRE_SESSION_REFRESH) {
    return result_success_type(layout, output_type_index,
                               AL_OWNING_TYPE_RECORD, &success_index) &&
           record_has_kinds(layout, success_index, session_fields, 8u);
  }
  if (operation == AL_CAMPFIRE_SESSION_FIND ||
      operation == AL_CAMPFIRE_USER_FIND ||
      operation == AL_CAMPFIRE_USER_BOT_KEY ||
      operation == AL_CAMPFIRE_ROOM_FIND) {
    if (!result_success_type(layout, output_type_index,
                             AL_OWNING_TYPE_OPTION, &success_index) ||
        !option_payload_type(layout, success_index, AL_OWNING_TYPE_RECORD,
                             &payload_index))
      return 0;
    switch (operation) {
    case AL_CAMPFIRE_SESSION_FIND:
      return record_has_kinds(layout, payload_index, session_fields, 8u);
    case AL_CAMPFIRE_USER_FIND:
    case AL_CAMPFIRE_USER_BOT_KEY:
      return record_has_kinds(layout, payload_index, user_fields, 4u);
    case AL_CAMPFIRE_ROOM_FIND:
      if (!record_has_kinds(layout, payload_index, room_fields, 6u) ||
          !record_field_type(layout, payload_index, 1u, &payload_index))
        return 0;
      return option_payload_type(layout, payload_index,
                                 AL_OWNING_TYPE_STRING, &option_payload);
    default:
      return 0;
    }
  }
  return 0;
}

static int valid_utf8_path(const uint8_t *path, uint32_t byte_count) {
  uint32_t offset = 0u;
  int only_whitespace = 1;
  if (path == NULL || byte_count == 0u ||
      byte_count > AL_CAMPFIRE_ROOM_READ_PATH_LIMIT_BYTES ||
      path[byte_count] != 0u ||
      (byte_count == 8u && path[0] == ':' && path[1] == 'm' &&
       path[2] == 'e' && path[3] == 'm' && path[4] == 'o' &&
       path[5] == 'r' && path[6] == 'y' && path[7] == ':') ||
      (byte_count >= 5u && path[0] == 'f' && path[1] == 'i' &&
       path[2] == 'l' && path[3] == 'e' && path[4] == ':'))
    return 0;
  while (offset < byte_count) {
    uint32_t codepoint;
    uint8_t first = path[offset++];
    uint32_t count;
    uint32_t minimum;
    uint32_t part;
    if (first == 0u)
      return 0;
    if (first < 0x80u) {
      codepoint = first;
      count = 0u;
      minimum = 0u;
    } else if (first >= 0xc2u && first <= 0xdfu) {
      codepoint = first & 0x1fu;
      count = 1u;
      minimum = 0x80u;
    } else if (first >= 0xe0u && first <= 0xefu) {
      codepoint = first & 0x0fu;
      count = 2u;
      minimum = 0x800u;
    } else if (first >= 0xf0u && first <= 0xf4u) {
      codepoint = first & 0x07u;
      count = 3u;
      minimum = 0x10000u;
    } else {
      return 0;
    }
    for (part = 0u; part < count; ++part) {
      uint8_t next;
      if (offset >= byte_count)
        return 0;
      next = path[offset++];
      if ((next & 0xc0u) != 0x80u)
        return 0;
      codepoint = (codepoint << 6u) | (uint32_t)(next & 0x3fu);
    }
    if ((count != 0u && codepoint < minimum) || codepoint > 0x10ffffu ||
        (codepoint >= 0xd800u && codepoint <= 0xdfffu))
      return 0;
    if (!((codepoint >= 0x0009u && codepoint <= 0x000du) ||
          codepoint == 0x0020u || codepoint == 0x0085u ||
          codepoint == 0x00a0u || codepoint == 0x1680u ||
          (codepoint >= 0x2000u && codepoint <= 0x200au) ||
          codepoint == 0x2028u || codepoint == 0x2029u ||
          codepoint == 0x202fu || codepoint == 0x205fu ||
          codepoint == 0x3000u))
      only_whitespace = 0;
  }
  return !only_whitespace;
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
  if ((extended & 0xff) == SQLITE_CONSTRAINT)
    return AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
  return AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
}

static int read_column_text16(sqlite3 *database, sqlite3_stmt *statement,
                              int column, int allow_null,
                              al_room_read_text *out_text) {
  const void *value;
  int byte_count;
  int type = sqlite3_column_type(statement, column);
  if (type == SQLITE_NULL)
    return allow_null ? 0 : -1;
  value = sqlite3_column_text16(statement, column);
  byte_count = sqlite3_column_bytes16(statement, column);
  if (value == NULL || byte_count < 0 || (byte_count & 1) != 0 ||
      sqlite3_errcode(database) == SQLITE_NOMEM)
    return -1;
  out_text->bytes = (const uint8_t *)value;
  out_text->code_units = (uint32_t)byte_count / 2u;
  return 1;
}

static int read_column_int64(sqlite3_stmt *statement, int column,
                             int64_t *out_value) {
  if (sqlite3_column_type(statement, column) == SQLITE_NULL)
    return 0;
  *out_value = (int64_t)sqlite3_column_int64(statement, column);
  return 1;
}

static int text_equal_ascii(const al_room_read_text *text,
                            const char *ascii, uint32_t length) {
  uint32_t index;
  if (text->code_units != length)
    return 0;
  for (index = 0u; index < length; ++index) {
    if (load_u16_le(text->bytes + index * 2u) != (uint16_t)ascii[index])
      return 0;
  }
  return 1;
}

static int is_ascii_digit(uint16_t value) {
  return value >= (uint16_t)'0' && value <= (uint16_t)'9';
}

static int read_digits(const al_room_read_text *text, uint32_t start,
                       uint32_t count, uint32_t *out_value) {
  uint32_t index;
  uint32_t result = 0u;
  for (index = 0u; index < count; ++index) {
    uint16_t character = load_u16_le(text->bytes + (start + index) * 2u);
    if (!is_ascii_digit(character))
      return 0;
    result = result * 10u + (uint32_t)(character - (uint16_t)'0');
  }
  *out_value = result;
  return 1;
}

static int is_unicode_whitespace(uint32_t value) {
  return ((value >= 0x0009u && value <= 0x000du) || value == 0x0020u ||
          value == 0x0085u || value == 0x00a0u || value == 0x1680u ||
          (value >= 0x2000u && value <= 0x200au) || value == 0x2028u ||
          value == 0x2029u || value == 0x202fu || value == 0x205fu ||
          value == 0x3000u);
}

static int is_leap_year(uint32_t year) {
  return (year % 4u == 0u && year % 100u != 0u) || year % 400u == 0u;
}

/* This matches Authentication.Persistence.SqliteDetails.tryParseTimestamp:
 * trim Unicode whitespace, accept Rails/ISO date-time text and explicit UTC
 * offsets, retain only the first six fractional digits, and normalize to UTC. */
static int try_parse_timestamp(const al_room_read_text *text,
                               int64_t *out_unix_micros) {
  static const uint32_t month_days[] = {31u, 28u, 31u, 30u, 31u, 30u,
                                        31u, 31u, 30u, 31u, 30u, 31u};
  uint32_t start = 0u;
  uint32_t end = text->code_units;
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

  if (text->bytes == NULL)
    return 0;
  while (start < end && is_unicode_whitespace(
                             load_u16_le(text->bytes + start * 2u)))
    ++start;
  while (end > start && is_unicode_whitespace(
                             load_u16_le(text->bytes + (end - 1u) * 2u)))
    --end;
  if (end - start < 19u)
    return 0;
  if (load_u16_le(text->bytes + (start + 4u) * 2u) != (uint16_t)'-' ||
      load_u16_le(text->bytes + (start + 7u) * 2u) != (uint16_t)'-' ||
      (load_u16_le(text->bytes + (start + 10u) * 2u) != (uint16_t)' ' &&
       load_u16_le(text->bytes + (start + 10u) * 2u) != (uint16_t)'T') ||
      load_u16_le(text->bytes + (start + 13u) * 2u) != (uint16_t)':' ||
      load_u16_le(text->bytes + (start + 16u) * 2u) != (uint16_t)':')
    return 0;
  if (!read_digits(text, start, 4u, &year) ||
      !read_digits(text, start + 5u, 2u, &month) ||
      !read_digits(text, start + 8u, 2u, &day) ||
      !read_digits(text, start + 11u, 2u, &hour) ||
      !read_digits(text, start + 14u, 2u, &minute) ||
      !read_digits(text, start + 17u, 2u, &second))
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
  if (suffix < end &&
      load_u16_le(text->bytes + suffix * 2u) == (uint16_t)'.') {
    uint32_t first_digit;
    uint32_t digit_count;
    ++suffix;
    first_digit = suffix;
    while (suffix < end && is_ascii_digit(
                               load_u16_le(text->bytes + suffix * 2u)))
      ++suffix;
    digit_count = suffix - first_digit;
    if (digit_count == 0u)
      return 0;
    for (index = 0u; index < digit_count && index < 6u; ++index) {
      fraction_microseconds = fraction_microseconds * 10u +
          (uint32_t)(load_u16_le(text->bytes + (first_digit + index) * 2u) -
                     (uint16_t)'0');
    }
    for (; index < 6u; ++index)
      fraction_microseconds *= 10u;
  }

  if (suffix == end) {
    offset_sign = 1;
  } else if (end - suffix == 1u &&
             load_u16_le(text->bytes + suffix * 2u) == (uint16_t)'Z') {
    offset_sign = 1;
  } else if (end - suffix == 4u &&
             load_u16_le(text->bytes + suffix * 2u) == (uint16_t)' ' &&
             load_u16_le(text->bytes + (suffix + 1u) * 2u) == (uint16_t)'U' &&
             load_u16_le(text->bytes + (suffix + 2u) * 2u) == (uint16_t)'T' &&
             load_u16_le(text->bytes + (suffix + 3u) * 2u) == (uint16_t)'C') {
    offset_sign = 1;
  } else if (end - suffix == 6u &&
             (load_u16_le(text->bytes + suffix * 2u) == (uint16_t)'+' ||
              load_u16_le(text->bytes + suffix * 2u) == (uint16_t)'-') &&
             load_u16_le(text->bytes + (suffix + 3u) * 2u) == (uint16_t)':') {
    offset_sign =
        load_u16_le(text->bytes + suffix * 2u) == (uint16_t)'-' ? -1 : 1;
    if (!read_digits(text, suffix + 1u, 2u, &offset_hours) ||
        !read_digits(text, suffix + 4u, 2u, &offset_minutes) ||
        offset_hours > 14u || offset_minutes > 59u ||
        (offset_hours == 14u && offset_minutes != 0u))
      return 0;
  } else {
    return 0;
  }

  days = (int64_t)(year - 1u) * 365 + (int64_t)((year - 1u) / 4u) -
         (int64_t)((year - 1u) / 100u) +
         (int64_t)((year - 1u) / 400u);
  for (index = 1u; index < month; ++index) {
    days += (int64_t)month_days[index - 1u];
    if (index == 2u && is_leap_year(year))
      ++days;
  }
  days += (int64_t)day - 1;
  local_ticks = days * INT64_C(864000000000) +
      (int64_t)hour * 3600 * INT64_C(10000000) +
      (int64_t)minute * 60 * INT64_C(10000000) +
      (int64_t)second * INT64_C(10000000) +
      (int64_t)fraction_microseconds * 10;
  utc_ticks = local_ticks -
      (int64_t)(offset_sign * (int)(offset_hours * 60u + offset_minutes)) *
          INT64_C(600000000);
  if (utc_ticks < 0 || utc_ticks > DATETIME_MAX_TICKS)
    return 0;
  *out_unix_micros = (utc_ticks - UNIX_EPOCH_TICKS) / 10;
  return 1;
}

static void write_string(uint8_t *output, uint32_t offset,
                         const al_room_read_text *text) {
  uint32_t extent = padded_string_extent(text->code_units);
  uint32_t raw_extent = 8u + text->code_units * 2u;
  uint32_t index;
  store_u64_le(output, offset, text->code_units);
  for (index = 0u; index < text->code_units; ++index) {
    uint16_t unit = load_u16_le(text->bytes + index * 2u);
    output[offset + 8u + index * 2u] = (uint8_t)(unit & 0xffu);
    output[offset + 9u + index * 2u] = (uint8_t)(unit >> 8u);
  }
  for (index = raw_extent; index < extent; ++index)
    output[offset + index] = 0u;
}

static int write_result_error(uint8_t *output, uint32_t capacity,
                              uint32_t error_tag,
                              uint32_t *out_extent_bytes) {
  if (output == NULL || out_extent_bytes == NULL || capacity < 16u)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 1u); /* Result.Error */
  store_u64_le(output, 8u, error_tag);
  *out_extent_bytes = 16u;
  return 0;
}

static int write_result_option_none(uint8_t *output, uint32_t capacity,
                                    uint32_t *out_extent_bytes) {
  if (output == NULL || out_extent_bytes == NULL || capacity < 16u)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u); /* Result.Ok */
  store_u64_le(output, 8u, 1u); /* Option.None */
  *out_extent_bytes = 16u;
  return 0;
}

static int write_result_bool(uint8_t *output, uint32_t capacity, int value,
                             uint32_t *out_extent_bytes) {
  if (output == NULL || out_extent_bytes == NULL || capacity < 16u)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u); /* Result.Ok */
  store_u64_le(output, 8u, value ? 1u : 0u);
  *out_extent_bytes = 16u;
  return 0;
}

static int text_output_extent(const al_room_read_text *text,
                              uint32_t *out_extent) {
  uint32_t extent = padded_string_extent(text->code_units);
  if (extent == 0u)
    return 0;
  *out_extent = extent;
  return 1;
}

static int option_text_output_extent(int state,
                                     const al_room_read_text *text,
                                     uint32_t *out_extent) {
  uint32_t string_extent = 0u;
  if (state < 0 || state > 1 ||
      (state == 1 && !text_output_extent(text, &string_extent)))
    return 0;
  *out_extent = 8u + string_extent;
  return 1;
}

static int load_session_row(sqlite3 *database, sqlite3_stmt *statement,
                            al_room_read_session_row *row) {
  int token_state;
  int ip_state;
  int agent_state;
  int active_state;
  int created_state;
  int updated_state;
  if (!read_column_int64(statement, 0, &row->id) ||
      !read_column_int64(statement, 1, &row->user_id))
    return 0;
  token_state = read_column_text16(database, statement, 2, 0, &row->token);
  ip_state = read_column_text16(database, statement, 3, 1, &row->ip_address);
  agent_state =
      read_column_text16(database, statement, 4, 1, &row->user_agent);
  active_state = read_column_text16(database, statement, 5, 0,
                                    &row->last_active_at_text);
  created_state =
      read_column_text16(database, statement, 6, 0, &row->created_at_text);
  updated_state =
      read_column_text16(database, statement, 7, 0, &row->updated_at_text);
  if (token_state != 1 || ip_state < 0 || agent_state < 0 ||
      active_state != 1 || created_state != 1 || updated_state != 1 ||
      !try_parse_timestamp(&row->last_active_at_text,
                           &row->last_active_at_micros) ||
      !try_parse_timestamp(&row->created_at_text, &row->created_at_micros) ||
      !try_parse_timestamp(&row->updated_at_text, &row->updated_at_micros))
    return 0;
  row->observed_last_active_at =
      (const uint16_t *)row->last_active_at_text.bytes;
  row->observed_last_active_bytes =
      (int)(row->last_active_at_text.code_units * 2u);
  row->ip_state = ip_state;
  row->agent_state = agent_state;
  return 1;
}

static int session_output_extent(const al_room_read_session_row *row,
                                 uint32_t *out_extent) {
  uint32_t token_extent;
  uint32_t ip_extent;
  uint32_t agent_extent;
  uint64_t total;
  if (!text_output_extent(&row->token, &token_extent) ||
      !option_text_output_extent(row->ip_state, &row->ip_address,
                                 &ip_extent) ||
      !option_text_output_extent(row->agent_state, &row->user_agent,
                                 &agent_extent))
    return 0;
  total = UINT64_C(16) + token_extent + ip_extent + agent_extent +
          UINT64_C(24);
  if (total > UINT32_MAX)
    return 0;
  *out_extent = (uint32_t)total;
  return 1;
}

static void write_session_row(uint8_t *output, uint32_t offset,
                              const al_room_read_session_row *row) {
  uint32_t token_extent = padded_string_extent(row->token.code_units);
  uint32_t option_extent;
  store_u64_le(output, offset, (uint64_t)row->id);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->user_id);
  offset += 8u;
  write_string(output, offset, &row->token);
  offset += token_extent;
  store_u64_le(output, offset, row->ip_state == 1 ? 0u : 1u);
  offset += 8u;
  if (row->ip_state == 1) {
    option_extent = padded_string_extent(row->ip_address.code_units);
    write_string(output, offset, &row->ip_address);
    offset += option_extent;
  }
  store_u64_le(output, offset, row->agent_state == 1 ? 0u : 1u);
  offset += 8u;
  if (row->agent_state == 1) {
    option_extent = padded_string_extent(row->user_agent.code_units);
    write_string(output, offset, &row->user_agent);
    offset += option_extent;
  }
  store_u64_le(output, offset, (uint64_t)row->last_active_at_micros);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->created_at_micros);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->updated_at_micros);
}

static int reset_statement(sqlite3_stmt *statement) {
  int reset_status;
  int clear_status;
  if (statement == NULL)
    return SQLITE_OK;
  reset_status = sqlite3_reset(statement);
  clear_status = sqlite3_clear_bindings(statement);
  return clear_status != SQLITE_OK ? clear_status :
         (reset_status == SQLITE_OK ? SQLITE_OK : reset_status);
}

static void cleanup_statements(al_campfire_room_read_context_v1 *ctx) {
  uint32_t index;
  for (index = 0u; index < AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT;
       ++index)
    (void)reset_statement((sqlite3_stmt *)ctx->opaque_statements[index]);
}

static void poison_context(al_campfire_room_read_context_v1 *ctx) {
  uint32_t state = __atomic_load_n(&ctx->active, __ATOMIC_RELAXED);
  __atomic_store_n(&ctx->active,
                   state | ROOM_READ_CONTEXT_STATE_POISONED,
                   __ATOMIC_RELEASE);
}

static void release_context_call(al_campfire_room_read_context_v1 *ctx) {
  uint32_t state = __atomic_load_n(&ctx->active, __ATOMIC_RELAXED);
  __atomic_store_n(&ctx->active,
                   state & ROOM_READ_CONTEXT_STATE_POISONED,
                   __ATOMIC_RELEASE);
}

static int bind_int64(sqlite3_stmt *statement, int index, int64_t value) {
  return sqlite3_bind_int64(statement, index, (sqlite3_int64)value);
}

static int bind_text(sqlite3_stmt *statement, int index,
                     const uint8_t *bytes, uint32_t code_units) {
  uint64_t byte_count = (uint64_t)code_units * 2u;
  if (byte_count > INT_MAX)
    return SQLITE_ERROR;
  return sqlite3_bind_text16(statement, index, bytes, (int)byte_count,
                             SQLITE_TRANSIENT);
}

static int bind_option_text(sqlite3_stmt *statement, int index,
                            const uint8_t *option, uint32_t option_extent) {
  uint64_t tag;
  uint32_t string_extent;
  uint64_t code_units;
  if (option == NULL || option_extent < 8u)
    return SQLITE_ERROR;
  tag = load_u64_le(option);
  if (tag == 1u)
    return sqlite3_bind_null(statement, index);
  if (tag != 0u || option_extent < 16u)
    return SQLITE_ERROR;
  code_units = load_u64_le(option + 8u);
  if (code_units > UINT32_MAX ||
      (string_extent = padded_string_extent((uint32_t)code_units)) == 0u ||
      string_extent + 8u != option_extent)
    return SQLITE_ERROR;
  return bind_text(statement, index, option + 16u, (uint32_t)code_units);
}

static void write_text_option(uint8_t *output, uint32_t offset, int state,
                              const al_room_read_text *text) {
  store_u64_le(output, offset, state == 1 ? 0u : 1u);
  if (state == 1)
    write_string(output, offset + 8u, text);
}

static int load_user_row(sqlite3 *database, sqlite3_stmt *statement,
                         al_room_read_user_row *row) {
  int name_state;
  int64_t raw_role;
  int64_t raw_status;
  if (!read_column_int64(statement, 0, &row->id))
    return 0;
  name_state = read_column_text16(database, statement, 1, 0, &row->name);
  if (name_state != 1 || !read_column_int64(statement, 2, &raw_role) ||
      !read_column_int64(statement, 3, &raw_status) || raw_role < 0 ||
      raw_role > 2 || raw_status < 0 || raw_status > 2)
    return 0;
  row->role = (uint32_t)raw_role;
  row->status = (uint32_t)raw_status;
  return 1;
}

static int user_output_extent(const al_room_read_user_row *row,
                              uint32_t *out_extent) {
  uint32_t name_extent;
  if (!text_output_extent(&row->name, &name_extent))
    return 0;
  *out_extent = 24u + name_extent;
  return 1;
}

static void write_user_row(uint8_t *output, uint32_t offset,
                           const al_room_read_user_row *row) {
  store_u64_le(output, offset, (uint64_t)row->id);
  write_string(output, offset + 8u, &row->name);
  store_u64_le(output, offset + 8u + padded_string_extent(row->name.code_units),
               row->role);
  store_u64_le(output,
               offset + 16u + padded_string_extent(row->name.code_units),
               row->status);
}

static int room_kind_tag(const al_room_read_text *text, uint32_t *out_tag) {
  if (text_equal_ascii(text, "Rooms::Open", 11u)) {
    *out_tag = 0u;
    return 1;
  }
  if (text_equal_ascii(text, "Rooms::Closed", 13u)) {
    *out_tag = 1u;
    return 1;
  }
  if (text_equal_ascii(text, "Rooms::Direct", 13u)) {
    *out_tag = 2u;
    return 1;
  }
  return 0;
}

static int load_room_row(sqlite3 *database, sqlite3_stmt *statement,
                         al_room_read_room_row *row) {
  al_room_read_text kind_text;
  int kind_state;
  int created_state;
  int updated_state;
  int64_t parsed_ticks;
  if (!read_column_int64(statement, 0, &row->id))
    return 0;
  row->name_state =
      read_column_text16(database, statement, 1, 1, &row->name);
  kind_state = read_column_text16(database, statement, 2, 0, &kind_text);
  if (kind_state != 1 ||
      !room_kind_tag(&kind_text, &row->kind) ||
      !read_column_int64(statement, 3, &row->creator_id))
    return 0;
  created_state = read_column_text16(database, statement, 4, 0,
                                     &row->created_at);
  updated_state = read_column_text16(database, statement, 5, 0,
                                     &row->updated_at);
  return row->name_state >= 0 && created_state == 1 && updated_state == 1 &&
         try_parse_timestamp(&row->created_at, &parsed_ticks) &&
         try_parse_timestamp(&row->updated_at, &parsed_ticks);
}

static int room_output_extent(const al_room_read_room_row *row,
                              uint32_t *out_extent) {
  uint32_t name_extent;
  uint32_t created_extent;
  uint32_t updated_extent;
  uint64_t total;
  if (!option_text_output_extent(row->name_state, &row->name, &name_extent) ||
      !text_output_extent(&row->created_at, &created_extent) ||
      !text_output_extent(&row->updated_at, &updated_extent))
    return 0;
  total = UINT64_C(24) + name_extent + created_extent +
          updated_extent;
  if (total > UINT32_MAX)
    return 0;
  *out_extent = (uint32_t)total;
  return 1;
}

static void write_room_row(uint8_t *output, uint32_t offset,
                           const al_room_read_room_row *row) {
  uint32_t name_extent;
  store_u64_le(output, offset, (uint64_t)row->id);
  offset += 8u;
  name_extent = row->name_state == 1
                    ? padded_string_extent(row->name.code_units)
                    : 0u;
  write_text_option(output, offset, row->name_state, &row->name);
  offset += 8u + name_extent;
  store_u64_le(output, offset, row->kind);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->creator_id);
  offset += 8u;
  write_string(output, offset, &row->created_at);
  offset += padded_string_extent(row->created_at.code_units);
  write_string(output, offset, &row->updated_at);
}

static int load_message_row(sqlite3 *database, sqlite3_stmt *statement,
                            al_room_read_message_row *row) {
  int client_state;
  int body_state;
  int created_state;
  int updated_state;
  if (!read_column_int64(statement, 0, &row->id) ||
      !read_column_int64(statement, 1, &row->room_id) ||
      !read_column_int64(statement, 2, &row->creator_id))
    return 0;
  client_state =
      read_column_text16(database, statement, 3, 0, &row->client_message_id);
  body_state = read_column_text16(database, statement, 4, 1, &row->body);
  created_state =
      read_column_text16(database, statement, 5, 0, &row->created_at);
  updated_state =
      read_column_text16(database, statement, 6, 0, &row->updated_at);
  if (client_state != 1 || body_state < 0 || created_state != 1 ||
      updated_state != 1)
    return 0;
  row->body_state = body_state;
  return 1;
}

static int message_output_extent(const al_room_read_message_row *row,
                                 uint32_t *out_extent) {
  uint32_t client_extent;
  uint32_t body_extent;
  uint32_t created_extent;
  uint32_t updated_extent;
  uint64_t total;
  if (!text_output_extent(&row->client_message_id, &client_extent) ||
      !option_text_output_extent(row->body_state, &row->body, &body_extent) ||
      !text_output_extent(&row->created_at, &created_extent) ||
      !text_output_extent(&row->updated_at, &updated_extent))
    return 0;
  total = UINT64_C(24) + client_extent + body_extent + created_extent +
          updated_extent;
  if (total > UINT32_MAX)
    return 0;
  *out_extent = (uint32_t)total;
  return 1;
}

static void write_message_row(uint8_t *output, uint32_t offset,
                              const al_room_read_message_row *row) {
  uint32_t client_extent =
      padded_string_extent(row->client_message_id.code_units);
  uint32_t body_extent = row->body_state == 1
                             ? padded_string_extent(row->body.code_units)
                             : 0u;
  store_u64_le(output, offset, (uint64_t)row->id);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->room_id);
  offset += 8u;
  store_u64_le(output, offset, (uint64_t)row->creator_id);
  offset += 8u;
  write_string(output, offset, &row->client_message_id);
  offset += client_extent;
  write_text_option(output, offset, row->body_state, &row->body);
  offset += 8u + body_extent;
  write_string(output, offset, &row->created_at);
  offset += padded_string_extent(row->created_at.code_units);
  write_string(output, offset, &row->updated_at);
}

static void format_timestamp(int64_t unix_micros, uint16_t output[26],
                             uint32_t *out_code_units) {
  int64_t days = unix_micros / UNIX_MICROS_PER_DAY;
  int64_t day_micros = unix_micros % UNIX_MICROS_PER_DAY;
  int64_t z;
  int64_t era;
  int64_t doe;
  int64_t yoe;
  int64_t year;
  int64_t doy;
  int64_t mp;
  int64_t day;
  int64_t month;
  int64_t hour;
  int64_t minute;
  int64_t second;
  int64_t micros;
  uint32_t length = 19u;
  uint32_t index;

  if (day_micros < 0) {
    day_micros += UNIX_MICROS_PER_DAY;
    --days;
  }
  z = days + INT64_C(719468);
  era = z / INT64_C(146097);
  doe = z - era * INT64_C(146097);
  yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  year = yoe + era * 400;
  doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  mp = (5 * doy + 2) / 153;
  day = doy - (153 * mp + 2) / 5 + 1;
  month = mp + (mp < 10 ? 3 : -9);
  year += month <= 2;
  hour = day_micros / (3600 * UNIX_MICROS_PER_SECOND);
  day_micros %= 3600 * UNIX_MICROS_PER_SECOND;
  minute = day_micros / (60 * UNIX_MICROS_PER_SECOND);
  day_micros %= 60 * UNIX_MICROS_PER_SECOND;
  second = day_micros / UNIX_MICROS_PER_SECOND;
  micros = day_micros % UNIX_MICROS_PER_SECOND;

  for (index = 0u; index < 26u; ++index)
    output[index] = 0u;
#define PUT_DIGIT(position, digit) \
  output[(position)] = (uint16_t)('0' + (digit))
  PUT_DIGIT(0u, (year / 1000) % 10);
  PUT_DIGIT(1u, (year / 100) % 10);
  PUT_DIGIT(2u, (year / 10) % 10);
  PUT_DIGIT(3u, year % 10);
  output[4] = (uint16_t)'-';
  PUT_DIGIT(5u, month / 10);
  PUT_DIGIT(6u, month % 10);
  output[7] = (uint16_t)'-';
  PUT_DIGIT(8u, day / 10);
  PUT_DIGIT(9u, day % 10);
  output[10] = (uint16_t)' ';
  PUT_DIGIT(11u, hour / 10);
  PUT_DIGIT(12u, hour % 10);
  output[13] = (uint16_t)':';
  PUT_DIGIT(14u, minute / 10);
  PUT_DIGIT(15u, minute % 10);
  output[16] = (uint16_t)':';
  PUT_DIGIT(17u, second / 10);
  PUT_DIGIT(18u, second % 10);
  if (micros != 0) {
    output[19] = (uint16_t)'.';
    PUT_DIGIT(20u, micros / 100000);
    PUT_DIGIT(21u, (micros / 10000) % 10);
    PUT_DIGIT(22u, (micros / 1000) % 10);
    PUT_DIGIT(23u, (micros / 100) % 10);
    PUT_DIGIT(24u, (micros / 10) % 10);
    PUT_DIGIT(25u, micros % 10);
    length = 26u;
  }
#undef PUT_DIGIT
  *out_code_units = length;
}

static int begin_transaction(al_campfire_room_read_context_v1 *ctx,
                             uint32_t statement_index,
                             uint32_t *out_error_tag) {
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      statement_index];
  int rc = sqlite3_step(statement);
  if (rc != SQLITE_DONE) {
    *out_error_tag = sqlite_error_tag((sqlite3 *)ctx->opaque_db);
    return 0;
  }
  return 1;
}

static int end_transaction(al_campfire_room_read_context_v1 *ctx,
                           uint32_t statement_index,
                           uint32_t *out_error_tag) {
  sqlite3_stmt *statement =
      (sqlite3_stmt *)ctx->opaque_statements[statement_index];
  int rc = sqlite3_step(statement);
  if (rc != SQLITE_DONE) {
    *out_error_tag = sqlite_error_tag((sqlite3 *)ctx->opaque_db);
    return 0;
  }
  return 1;
}

static int rollback_transaction(al_campfire_room_read_context_v1 *ctx) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_ROLLBACK];
  int rc;
  int reset_rc;
  /* Draining/resetting active readers can itself end a transaction after a
   * SQLite error, so inspect autocommit only after this cleanup. */
  cleanup_statements(ctx);
  if (database == NULL) {
    poison_context(ctx);
    return 0;
  }
  if (sqlite3_get_autocommit(database) != 0)
    return 1;
  if (statement == NULL) {
    poison_context(ctx);
    return 0;
  }
  rc = sqlite3_step(statement);
  reset_rc = reset_statement(statement);
  if (rc != SQLITE_DONE || reset_rc != SQLITE_OK ||
      sqlite3_get_autocommit(database) == 0) {
    poison_context(ctx);
    return 0;
  }
  return 1;
}

static int require_autocommit(al_campfire_room_read_context_v1 *ctx) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  if (database != NULL && sqlite3_get_autocommit(database) != 0)
    return 1;
  (void)rollback_transaction(ctx);
  return 0;
}

static int session_find_by_token(al_campfire_room_read_context_v1 *ctx,
                                 const al_campfire_room_read_input *inputs,
                                 uint8_t *output, uint32_t capacity,
                                 uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_SESSION_FIND_TOKEN];
  al_room_read_session_row row;
  uint32_t row_extent;
  int rc;
  uint64_t code_units = load_u64_le(inputs[0].data);
  if (code_units > UINT32_MAX ||
      bind_text(statement, 1, inputs[0].data + 8u, (uint32_t)code_units) !=
          SQLITE_OK)
    return write_result_error(output, capacity,
                              sqlite_error_tag(database), out_extent);
  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE)
    return write_result_option_none(output, capacity, out_extent);
  if (rc != SQLITE_ROW)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  if (!load_session_row(database, statement, &row))
    return write_result_error(output, capacity,
                              AL_CAMPFIRE_DB_ERROR_UNAVAILABLE, out_extent);
  if (!session_output_extent(&row, &row_extent) ||
      (uint64_t)row_extent + 16u > capacity)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u); /* Result.Ok */
  store_u64_le(output, 8u, 0u); /* Option.Some */
  write_session_row(output, 16u, &row);
  *out_extent = row_extent + 16u;
  return 0;
}

static int parse_session_refresh_input(const al_owning_layout *layout,
                                       const al_campfire_room_read_input *input,
                                       al_room_read_refresh_input *refresh) {
  const al_owning_type_descriptor *record_type;
  uint32_t offsets[5];
  uint32_t child_types[5];
  uint32_t index;
  uint32_t option_extent;
  if (!get_type(layout, input->type_index, &record_type) ||
      record_type->kind != AL_OWNING_TYPE_RECORD ||
      record_type->field_count != 5u)
    return 0;
  for (index = 0u; index < 5u; ++index) {
    if (!record_field_location(layout, input->type_index, input->data,
                               input->extent_bytes, index, &offsets[index],
                               &child_types[index]))
      return 0;
  }
  if (!type_has_kind(layout, child_types[0], AL_OWNING_TYPE_I64) ||
      !type_has_kind(layout, child_types[1], AL_OWNING_TYPE_I64) ||
      !type_has_kind(layout, child_types[2], AL_OWNING_TYPE_I64) ||
      !option_payload_type(layout, child_types[3], AL_OWNING_TYPE_STRING,
                           &index) ||
      !option_payload_type(layout, child_types[4], AL_OWNING_TYPE_STRING,
                           &index))
    return 0;
  refresh->session_id = (int64_t)load_u64_le(input->data + offsets[0]);
  refresh->cutoff = (int64_t)load_u64_le(input->data + offsets[1]);
  refresh->now = (int64_t)load_u64_le(input->data + offsets[2]);
  refresh->ip_option = input->data + offsets[3];
  if (!measure_value(layout, child_types[3], refresh->ip_option,
                     input->extent_bytes - offsets[3], &option_extent, 0u))
    return 0;
  refresh->ip_option_extent = option_extent;
  refresh->agent_option = input->data + offsets[4];
  if (!measure_value(layout, child_types[4], refresh->agent_option,
                     input->extent_bytes - offsets[4], &option_extent, 0u))
    return 0;
  refresh->agent_option_extent = option_extent;
  return 1;
}

static int refresh_read_row(al_campfire_room_read_context_v1 *ctx,
                            int64_t session_id,
                            al_room_read_session_row *row,
                            uint32_t *out_error_tag, int *out_found) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_SESSION_FIND_ID];
  int rc;
  if (bind_int64(statement, 1, session_id) != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE) {
    *out_found = 0;
    return 1;
  }
  if (rc != SQLITE_ROW) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  if (!load_session_row(database, statement, row)) {
    *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    return 0;
  }
  *out_found = 1;
  return 1;
}

static int session_refresh_before(al_campfire_room_read_context_v1 *ctx,
                                  const al_owning_layout *layout,
                                  const al_campfire_room_read_input *inputs,
                                  uint8_t *output, uint32_t capacity,
                                  uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *update_statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_SESSION_REFRESH_UPDATE];
  al_room_read_refresh_input refresh;
  uint16_t now_utf16[26];
  uint32_t now_code_units;
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  uint32_t attempts;
  int transaction_open = 0;
  int result = HOST_BOUNDARY_FAILURE;

  if (!parse_session_refresh_input(layout, &inputs[0], &refresh))
    return HOST_BOUNDARY_FAILURE;
  if (refresh.cutoff < UtcInstant_MIN_MICROS ||
      refresh.cutoff > UtcInstant_MAX_MICROS ||
      refresh.now < UtcInstant_MIN_MICROS ||
      refresh.now > UtcInstant_MAX_MICROS || refresh.cutoff > refresh.now)
    return write_result_error(output, capacity,
                              AL_CAMPFIRE_DB_ERROR_INVALID_INPUT, out_extent);
  format_timestamp(refresh.now, now_utf16, &now_code_units);
  if (!begin_transaction(ctx, AL_CAMPFIRE_READ_STMT_BEGIN_IMMEDIATE,
                         &error_tag))
    return write_result_error(output, capacity, error_tag, out_extent);
  transaction_open = 1;

  for (attempts = 0u; attempts <= 2u; ++attempts) {
    al_room_read_session_row current;
    uint32_t row_extent;
    int found = 0;
    if (!refresh_read_row(ctx, refresh.session_id, &current, &error_tag,
                          &found))
      goto refresh_error;
    if (!found) {
      error_tag = AL_CAMPFIRE_DB_ERROR_NOT_FOUND;
      goto refresh_error;
    }

    if (current.last_active_at_micros < refresh.cutoff) {
      int rc;
      if (bind_option_text(update_statement, 1, refresh.ip_option,
                           refresh.ip_option_extent) != SQLITE_OK ||
          bind_option_text(update_statement, 2, refresh.agent_option,
                           refresh.agent_option_extent) != SQLITE_OK ||
          bind_text(update_statement, 3, (const uint8_t *)now_utf16,
                    now_code_units) != SQLITE_OK ||
          bind_int64(update_statement, 4, refresh.session_id) != SQLITE_OK ||
          current.observed_last_active_at == NULL ||
          current.observed_last_active_bytes < 0 ||
          sqlite3_bind_text16(update_statement, 5,
                              current.observed_last_active_at,
                              current.observed_last_active_bytes,
                              SQLITE_TRANSIENT) != SQLITE_OK) {
        error_tag = sqlite_error_tag(database);
        goto refresh_error;
      }
      /* SQLite owns a copy of the timestamp binding before the read is reset. */
      (void)reset_statement((sqlite3_stmt *)ctx->opaque_statements[
          AL_CAMPFIRE_READ_STMT_SESSION_FIND_ID]);
      rc = sqlite3_step(update_statement);
      if (rc != SQLITE_DONE) {
        error_tag = sqlite_error_tag(database);
        goto refresh_error;
      }
      if (sqlite3_changes(database) == 0) {
        (void)reset_statement(update_statement);
        if (attempts < 2u)
          continue;
        error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
        goto refresh_error;
      }
      (void)reset_statement(update_statement);
      if (!refresh_read_row(ctx, refresh.session_id, &current, &error_tag,
                            &found))
        goto refresh_error;
      if (!found) {
        error_tag = AL_CAMPFIRE_DB_ERROR_NOT_FOUND;
        goto refresh_error;
      }
    }

    if (!session_output_extent(&current, &row_extent) ||
        (uint64_t)row_extent + 8u > capacity)
      goto refresh_boundary;
    store_u64_le(output, 0u, 0u); /* Result.Ok */
    write_session_row(output, 8u, &current);
    /* Drain/reset the read statement before COMMIT. Output is still borrowed;
     * a commit failure replaces it with a complete typed Error value. */
    {
      sqlite3_stmt *read_statement = (sqlite3_stmt *)ctx->opaque_statements[
          AL_CAMPFIRE_READ_STMT_SESSION_FIND_ID];
      int rc = sqlite3_step(read_statement);
      if (rc != SQLITE_DONE) {
        error_tag = sqlite_error_tag(database);
        goto refresh_error;
      }
      (void)reset_statement(read_statement);
    }
    if (!end_transaction(ctx, AL_CAMPFIRE_READ_STMT_COMMIT, &error_tag))
      goto refresh_error;
    transaction_open = 0;
    *out_extent = row_extent + 8u;
    return 0;
  }

refresh_boundary:
  if (transaction_open)
    rollback_transaction(ctx);
  return HOST_BOUNDARY_FAILURE;

refresh_error:
  if (transaction_open)
    rollback_transaction(ctx);
  result = write_result_error(output, capacity, error_tag, out_extent);
  return result;
}

static int user_find_by_id(al_campfire_room_read_context_v1 *ctx,
                           int64_t user_id, uint8_t *output,
                           uint32_t capacity, uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_USER_FIND_ID];
  al_room_read_user_row row;
  uint32_t row_extent;
  int rc;
  if (bind_int64(statement, 1, user_id) != SQLITE_OK)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE)
    return write_result_option_none(output, capacity, out_extent);
  if (rc != SQLITE_ROW)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  if (!load_user_row(database, statement, &row))
    return write_result_error(output, capacity,
                              AL_CAMPFIRE_DB_ERROR_UNAVAILABLE, out_extent);
  if (!user_output_extent(&row, &row_extent) ||
      (uint64_t)row_extent + 16u > capacity)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u);
  store_u64_le(output, 8u, 0u);
  write_user_row(output, 16u, &row);
  *out_extent = row_extent + 16u;
  return 0;
}

static int split_bot_key(const al_room_read_text *key,
                         al_room_read_text *id,
                         al_room_read_text *token) {
  uint32_t index;
  uint32_t dash = UINT32_MAX;
  uint32_t second_dash = UINT32_MAX;
  for (index = 0u; index < key->code_units; ++index) {
    if (load_u16_le(key->bytes + index * 2u) == (uint16_t)'-') {
      if (dash == UINT32_MAX)
        dash = index;
      else {
        second_dash = index;
        break;
      }
    }
  }
  if (dash == UINT32_MAX)
    return 0;
  id->bytes = key->bytes;
  id->code_units = dash;
  token->bytes = key->bytes + (dash + 1u) * 2u;
  token->code_units = (second_dash == UINT32_MAX ? key->code_units
                                                 : second_dash) - dash - 1u;
  return 1;
}

static int user_authenticate_bot_key(al_campfire_room_read_context_v1 *ctx,
                                    const al_campfire_room_read_input *input,
                                    uint8_t *output, uint32_t capacity,
                                    uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_USER_BOT_KEY];
  al_room_read_text key;
  al_room_read_text id;
  al_room_read_text token;
  al_room_read_user_row row;
  uint32_t row_extent;
  uint64_t key_units = load_u64_le(input->data);
  int rc;
  key.bytes = input->data + 8u;
  key.code_units = (uint32_t)key_units;
  if (!split_bot_key(&key, &id, &token))
    return write_result_option_none(output, capacity, out_extent);
  if (bind_text(statement, 1, id.bytes, id.code_units) != SQLITE_OK ||
      bind_text(statement, 2, token.bytes, token.code_units) != SQLITE_OK)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE)
    return write_result_option_none(output, capacity, out_extent);
  if (rc != SQLITE_ROW)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  if (!load_user_row(database, statement, &row))
    return write_result_error(output, capacity,
                              AL_CAMPFIRE_DB_ERROR_UNAVAILABLE, out_extent);
  if (!user_output_extent(&row, &row_extent) ||
      (uint64_t)row_extent + 16u > capacity)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u);
  store_u64_le(output, 8u, 0u);
  write_user_row(output, 16u, &row);
  *out_extent = row_extent + 16u;
  return 0;
}

static int room_find_for_user(al_campfire_room_read_context_v1 *ctx,
                              int64_t user_id, int64_t room_id,
                              uint8_t *output, uint32_t capacity,
                              uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_ROOM_FIND_FOR_USER];
  al_room_read_room_row row;
  uint32_t row_extent;
  int rc;
  if (bind_int64(statement, 1, user_id) != SQLITE_OK ||
      bind_int64(statement, 2, room_id) != SQLITE_OK)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  rc = sqlite3_step(statement);
  if (rc == SQLITE_DONE)
    return write_result_option_none(output, capacity, out_extent);
  if (rc != SQLITE_ROW)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  if (!load_room_row(database, statement, &row))
    return write_result_error(output, capacity,
                              AL_CAMPFIRE_DB_ERROR_UNAVAILABLE, out_extent);
  if (!room_output_extent(&row, &row_extent) ||
      (uint64_t)row_extent + 16u > capacity)
    return HOST_BOUNDARY_FAILURE;
  store_u64_le(output, 0u, 0u);
  store_u64_le(output, 8u, 0u);
  write_room_row(output, 16u, &row);
  *out_extent = row_extent + 16u;
  return 0;
}

static int membership_exists(al_campfire_room_read_context_v1 *ctx,
                             int64_t user_id, int64_t room_id,
                             uint8_t *output, uint32_t capacity,
                             uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_MEMBERSHIP_EXISTS];
  int rc;
  if (bind_int64(statement, 1, user_id) != SQLITE_OK ||
      bind_int64(statement, 2, room_id) != SQLITE_OK)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  rc = sqlite3_step(statement);
  if (rc != SQLITE_ROW)
    return write_result_error(output, capacity, sqlite_error_tag(database),
                              out_extent);
  return write_result_bool(output, capacity,
                           sqlite3_column_int64(statement, 0) != 0,
                           out_extent);
}

static int room_exists(al_campfire_room_read_context_v1 *ctx, int64_t room_id,
                       int *out_exists, uint32_t *out_error_tag) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_ROOM_EXISTS];
  int rc;
  if (bind_int64(statement, 1, room_id) != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  rc = sqlite3_step(statement);
  if (rc != SQLITE_ROW) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  *out_exists = sqlite3_column_int64(statement, 0) != 0;
  (void)reset_statement(statement);
  return 1;
}

static int parse_membership_input(const al_owning_layout *layout,
                                  const al_campfire_room_read_input *input,
                                  int64_t *out_user_id,
                                  int64_t *out_room_id) {
  const al_owning_type_descriptor *record_type;
  uint32_t user_offset;
  uint32_t room_offset;
  uint32_t user_type;
  uint32_t room_type;
  if (!get_type(layout, input->type_index, &record_type) ||
      record_type->kind != AL_OWNING_TYPE_RECORD ||
      record_type->field_count != 2u ||
      !record_field_location(layout, input->type_index, input->data,
                             input->extent_bytes, 0u, &user_offset,
                             &user_type) ||
      !record_field_location(layout, input->type_index, input->data,
                             input->extent_bytes, 1u, &room_offset,
                             &room_type) ||
      !type_has_kind(layout, user_type, AL_OWNING_TYPE_I64) ||
      !type_has_kind(layout, room_type, AL_OWNING_TYPE_I64))
    return 0;
  *out_user_id = (int64_t)load_u64_le(input->data + user_offset);
  *out_room_id = (int64_t)load_u64_le(input->data + room_offset);
  return 1;
}

static int messages_last_page(al_campfire_room_read_context_v1 *ctx,
                              int64_t room_id, uint8_t *output,
                              uint32_t capacity, uint32_t *out_extent) {
  sqlite3 *database = (sqlite3 *)ctx->opaque_db;
  sqlite3_stmt *statement = (sqlite3_stmt *)ctx->opaque_statements[
      AL_CAMPFIRE_READ_STMT_MESSAGES_LAST_PAGE];
  al_room_read_message_extent rows[MAX_LAST_PAGE_ROWS];
  uint32_t count = 0u;
  uint32_t total_extent = 16u;
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  int exists = 0;
  int transaction_open = 0;
  int rc;
  uint32_t index;

  if (!begin_transaction(ctx, AL_CAMPFIRE_READ_STMT_BEGIN_DEFERRED,
                         &error_tag))
    return write_result_error(output, capacity, error_tag, out_extent);
  transaction_open = 1;
  if (!room_exists(ctx, room_id, &exists, &error_tag))
    goto last_page_error;
  if (!exists) {
    error_tag = AL_CAMPFIRE_DB_ERROR_NOT_FOUND;
    goto last_page_error;
  }
  if (bind_int64(statement, 1, room_id) != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto last_page_error;
  }
  for (;;) {
    al_room_read_message_row row;
    uint32_t row_extent;
    rc = sqlite3_step(statement);
    if (rc == SQLITE_DONE)
      break;
    if (rc != SQLITE_ROW) {
      error_tag = sqlite_error_tag(database);
      goto last_page_error;
    }
    if (count >= MAX_LAST_PAGE_ROWS || !load_message_row(database, statement,
                                                         &row) ||
        !message_output_extent(&row, &row_extent)) {
      error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
      goto last_page_error;
    }
    if ((uint64_t)total_extent + row_extent > AL_CAMPFIRE_ROOM_READ_OUTPUT_LIMIT) {
      transaction_open = 1;
      (void)reset_statement(statement);
      rollback_transaction(ctx);
      return HOST_BOUNDARY_FAILURE;
    }
    rows[count].extent = row_extent;
    rows[count].output_offset = 0u;
    rows[count].message_id = row.id;
    total_extent += row_extent;
    ++count;
  }
  if (total_extent > capacity)
    goto last_page_boundary;
  {
    uint32_t cursor = 16u;
    for (index = count; index > 0u; --index) {
      uint32_t descending_index = index - 1u;
      rows[descending_index].output_offset = cursor;
      cursor += rows[descending_index].extent;
    }
  }
  (void)reset_statement(statement);
  if (bind_int64(statement, 1, room_id) != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto last_page_error;
  }
  store_u64_le(output, 0u, 0u); /* Result.Ok */
  store_u64_le(output, 8u, count);
  for (index = 0u; index < count; ++index) {
    al_room_read_message_row row;
    uint32_t row_extent;
    rc = sqlite3_step(statement);
    if (rc != SQLITE_ROW) {
      error_tag = sqlite_error_tag(database);
      goto last_page_error;
    }
    if (!load_message_row(database, statement, &row) ||
        !message_output_extent(&row, &row_extent) ||
        row_extent != rows[index].extent ||
        row.id != rows[index].message_id ||
        rows[index].output_offset > capacity ||
        row_extent > capacity - rows[index].output_offset) {
      error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
      goto last_page_error;
    }
    write_message_row(output, rows[index].output_offset, &row);
  }
  rc = sqlite3_step(statement);
  if (rc != SQLITE_DONE) {
    error_tag = sqlite_error_tag(database);
    goto last_page_error;
  }
  (void)reset_statement(statement);
  if (!end_transaction(ctx, AL_CAMPFIRE_READ_STMT_COMMIT, &error_tag))
    goto last_page_error;
  transaction_open = 0;
  *out_extent = total_extent;
  return 0;

last_page_boundary:
  if (transaction_open)
    rollback_transaction(ctx);
  return HOST_BOUNDARY_FAILURE;

last_page_error:
  if (transaction_open)
    rollback_transaction(ctx);
  return write_result_error(output, capacity, error_tag, out_extent);
}

static int32_t prepare_query(sqlite3 *database, uint32_t index,
                             sqlite3_stmt **out_statement) {
  const char *tail = NULL;
  int rc = sqlite3_prepare_v2(database, room_read_queries[index], -1,
                              out_statement, &tail);
  if (rc != SQLITE_OK || *out_statement == NULL || tail == NULL ||
      *tail != '\0')
    return HOST_BOUNDARY_FAILURE;
  return 0;
}

static int prepare_fixed_pragma(sqlite3 *database, const char *sql,
                                sqlite3_stmt **out_statement,
                                uint32_t *out_error_tag) {
  const char *tail = NULL;
  int rc = sqlite3_prepare_v2(database, sql, -1, out_statement, &tail);
  if (rc != SQLITE_OK || *out_statement == NULL || tail == NULL ||
      *tail != '\0') {
    *out_error_tag = sqlite_error_tag(database);
    if (*out_statement != NULL) {
      (void)sqlite3_finalize(*out_statement);
      *out_statement = NULL;
    }
    return 0;
  }
  return 1;
}

static int execute_fixed_pragma(sqlite3 *database, const char *sql,
                                uint32_t *out_error_tag) {
  sqlite3_stmt *statement = NULL;
  int rc;
  if (!prepare_fixed_pragma(database, sql, &statement, out_error_tag))
    return 0;
  do {
    rc = sqlite3_step(statement);
  } while (rc == SQLITE_ROW);
  if (rc != SQLITE_DONE) {
    *out_error_tag = sqlite_error_tag(database);
    (void)sqlite3_finalize(statement);
    return 0;
  }
  rc = sqlite3_finalize(statement);
  if (rc != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  return 1;
}

static int pragma_text_equals(sqlite3 *database, const char *sql,
                              const char *expected,
                              uint32_t expected_code_units,
                              uint32_t *out_error_tag) {
  sqlite3_stmt *statement = NULL;
  const uint16_t *value;
  int byte_count;
  uint32_t index;
  int rc;
  if (!prepare_fixed_pragma(database, sql, &statement, out_error_tag))
    return 0;
  rc = sqlite3_step(statement);
  if (rc != SQLITE_ROW ||
      sqlite3_column_type(statement, 0) != SQLITE_TEXT) {
    *out_error_tag = sqlite_error_tag(database);
    (void)sqlite3_finalize(statement);
    return 0;
  }
  value = (const uint16_t *)sqlite3_column_text16(statement, 0);
  byte_count = sqlite3_column_bytes16(statement, 0);
  if (value == NULL || byte_count < 0 || (byte_count & 1) != 0 ||
      (uint32_t)byte_count / 2u != expected_code_units) {
    *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    (void)sqlite3_finalize(statement);
    return 0;
  }
  for (index = 0u; index < expected_code_units; ++index) {
    uint16_t actual = load_u16_le((const uint8_t *)value + index * 2u);
    uint16_t wanted = (uint16_t)expected[index];
    if (actual >= (uint16_t)'A' && actual <= (uint16_t)'Z')
      actual = (uint16_t)(actual + ((uint16_t)'a' - (uint16_t)'A'));
    if (wanted >= (uint16_t)'A' && wanted <= (uint16_t)'Z')
      wanted = (uint16_t)(wanted + ((uint16_t)'a' - (uint16_t)'A'));
    if (actual != wanted) {
      *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
      (void)sqlite3_finalize(statement);
      return 0;
    }
  }
  rc = sqlite3_step(statement);
  if (rc != SQLITE_DONE) {
    *out_error_tag = sqlite_error_tag(database);
    (void)sqlite3_finalize(statement);
    return 0;
  }
  rc = sqlite3_finalize(statement);
  if (rc != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  return 1;
}

static int pragma_integer_equals(sqlite3 *database, const char *sql,
                                 int64_t expected,
                                 uint32_t *out_error_tag) {
  sqlite3_stmt *statement = NULL;
  int64_t actual;
  int rc;
  if (!prepare_fixed_pragma(database, sql, &statement, out_error_tag))
    return 0;
  rc = sqlite3_step(statement);
  if (rc != SQLITE_ROW ||
      sqlite3_column_type(statement, 0) != SQLITE_INTEGER) {
    *out_error_tag = sqlite_error_tag(database);
    (void)sqlite3_finalize(statement);
    return 0;
  }
  actual = (int64_t)sqlite3_column_int64(statement, 0);
  if (actual != expected) {
    *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    (void)sqlite3_finalize(statement);
    return 0;
  }
  rc = sqlite3_step(statement);
  if (rc != SQLITE_DONE) {
    *out_error_tag = sqlite_error_tag(database);
    (void)sqlite3_finalize(statement);
    return 0;
  }
  rc = sqlite3_finalize(statement);
  if (rc != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  return 1;
}

static int configure_connection_base(sqlite3 *database,
                                     uint32_t *out_error_tag) {
  int foreign_keys_enabled = 0;
  if (sqlite3_extended_result_codes(database, 1) != SQLITE_OK ||
      sqlite3_busy_timeout(database, SQLITE_BUSY_TIMEOUT_MS) != SQLITE_OK) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  if (sqlite3_db_config(database, SQLITE_DBCONFIG_ENABLE_FKEY, 1,
                        &foreign_keys_enabled) != SQLITE_OK ||
      foreign_keys_enabled != 1) {
    *out_error_tag = sqlite_error_tag(database);
    return 0;
  }
  if (!pragma_integer_equals(database, "PRAGMA foreign_keys", 1,
                             out_error_tag) ||
      !execute_fixed_pragma(database, "PRAGMA read_uncommitted=0",
                            out_error_tag) ||
      !pragma_integer_equals(database, "PRAGMA read_uncommitted", 0,
                             out_error_tag))
    return 0;
  return 1;
}

static int verify_connection_pragmas(sqlite3 *database,
                                     uint32_t *out_error_tag) {
  if (!pragma_text_equals(database, "PRAGMA journal_mode", "wal", 3u,
                          out_error_tag) ||
      !pragma_integer_equals(database, "PRAGMA synchronous",
                             SQLITE_SYNCHRONOUS_FULL, out_error_tag) ||
      !pragma_integer_equals(database, "PRAGMA read_uncommitted", 0,
                             out_error_tag) ||
      !pragma_integer_equals(database, "PRAGMA wal_autocheckpoint",
                             WAL_AUTO_CHECKPOINT_PAGES, out_error_tag))
    return 0;
  if (sqlite3_get_autocommit(database) == 0) {
    *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    return 0;
  }
  return 1;
}

static int configure_worker_connection(sqlite3 *database,
                                       uint32_t *out_error_tag) {
  if (sqlite3_libversion_number() < SQLITE_MINIMUM_VERSION_NUMBER) {
    *out_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    return 0;
  }
  if (!configure_connection_base(database, out_error_tag) ||
      !pragma_text_equals(database, "PRAGMA journal_mode", "wal", 3u,
                          out_error_tag) ||
      !execute_fixed_pragma(database, "PRAGMA synchronous=FULL",
                            out_error_tag) ||
      !verify_connection_pragmas(database, out_error_tag))
    return 0;
  return 1;
}

AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_provision_wal_v1(
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    uint32_t *out_db_error_tag) {
  sqlite3 *database = NULL;
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  int result = HOST_BOUNDARY_FAILURE;
  int rc;
  if (out_db_error_tag == NULL)
    return HOST_BOUNDARY_FAILURE;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  if (!valid_utf8_path(canonical_db_path_utf8, path_bytes)) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
    return HOST_BOUNDARY_FAILURE;
  }
  if (sqlite3_libversion_number() < SQLITE_MINIMUM_VERSION_NUMBER) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    return HOST_BOUNDARY_FAILURE;
  }
  rc = sqlite3_open_v2((const char *)canonical_db_path_utf8, &database,
                       SQLITE_OPEN_FLAGS, NULL);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto provision_failed;
  }
  if (!configure_connection_base(database, &error_tag) ||
      !execute_fixed_pragma(database, "PRAGMA synchronous=EXTRA",
                            &error_tag) ||
      !pragma_integer_equals(database, "PRAGMA synchronous",
                             SQLITE_SYNCHRONOUS_EXTRA, &error_tag) ||
      !pragma_text_equals(database, "PRAGMA journal_mode=WAL", "wal", 3u,
                          &error_tag) ||
      !execute_fixed_pragma(database, "PRAGMA synchronous=FULL",
                            &error_tag) ||
      !verify_connection_pragmas(database, &error_tag))
    goto provision_failed;
  rc = sqlite3_close(database);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto provision_failed;
  }
  database = NULL;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  return 0;

provision_failed:
  if (database != NULL)
    (void)sqlite3_close(database);
  *out_db_error_tag = error_tag;
  return result;
}

AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_context_init_v1(
    al_campfire_room_read_context_v1 *ctx,
    const uint8_t *canonical_db_path_utf8, uint32_t path_bytes,
    void *pure_context, uint32_t *out_db_error_tag) {
  sqlite3 *database = NULL;
  sqlite3_stmt *statements[AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT] = {0};
  int rc;
  uint32_t index;
  uint32_t error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  if (out_db_error_tag == NULL)
    return HOST_BOUNDARY_FAILURE;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  if (ctx == NULL)
    return HOST_BOUNDARY_FAILURE;
  if (ctx->cookie == ROOM_READ_CONTEXT_COOKIE) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
    return HOST_BOUNDARY_FAILURE;
  }
  clear_bytes(ctx, sizeof(*ctx));
  if (!valid_utf8_path(canonical_db_path_utf8, path_bytes)) {
    *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_INVALID_INPUT;
    return HOST_BOUNDARY_FAILURE;
  }
  rc = sqlite3_open_v2((const char *)canonical_db_path_utf8, &database,
                       SQLITE_OPEN_FLAGS, NULL);
  if (rc != SQLITE_OK) {
    error_tag = sqlite_error_tag(database);
    goto failed;
  }
  if (!configure_worker_connection(database, &error_tag))
    goto failed;
  for (index = 0u; index < AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT;
       ++index) {
    if (prepare_query(database, index, &statements[index]) != 0) {
      error_tag = sqlite_error_tag(database);
      goto failed;
    }
  }
  if (sqlite3_get_autocommit(database) == 0) {
    error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
    goto failed;
  }
  ctx->opaque_db = database;
  for (index = 0u; index < AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT;
       ++index)
    ctx->opaque_statements[index] = statements[index];
  ctx->pure_context = pure_context;
  ctx->cookie = ROOM_READ_CONTEXT_COOKIE;
  ctx->owner_thread_id = GetCurrentThreadId();
  ctx->active = 0u;
  *out_db_error_tag = AL_CAMPFIRE_DB_ERROR_UNAVAILABLE;
  return 0;

failed:
  for (index = 0u; index < AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT;
       ++index) {
    if (statements[index] != NULL)
      (void)sqlite3_finalize(statements[index]);
  }
  if (database != NULL)
    (void)sqlite3_close(database);
  clear_bytes(ctx, sizeof(*ctx));
  *out_db_error_tag = error_tag;
  return HOST_BOUNDARY_FAILURE;
}

AL_CAMPFIRE_EXPORT int32_t al_campfire_room_read_context_destroy_v1(
    al_campfire_room_read_context_v1 *ctx) {
  sqlite3 *database;
  uint32_t state;
  uint32_t index;
  int rc;
  if (ctx == NULL)
    return HOST_BOUNDARY_FAILURE;
  if (ctx->cookie == 0u && ctx->opaque_db == NULL && ctx->pure_context == NULL &&
      ctx->owner_thread_id == 0u && ctx->active == 0u)
    return 0;
  state = __atomic_load_n(&ctx->active, __ATOMIC_ACQUIRE);
  if (ctx->cookie != ROOM_READ_CONTEXT_COOKIE ||
      ctx->owner_thread_id != GetCurrentThreadId() ||
      (state & ROOM_READ_CONTEXT_STATE_ACTIVE) != 0u)
    return HOST_BOUNDARY_FAILURE;
  database = (sqlite3 *)ctx->opaque_db;
  if (database != NULL && sqlite3_get_autocommit(database) == 0) {
    cleanup_statements(ctx);
    (void)rollback_transaction(ctx);
  }
  for (index = 0u; index < AL_CAMPFIRE_ROOM_READ_SQLITE_STATEMENT_COUNT;
       ++index) {
    sqlite3_stmt *statement =
        (sqlite3_stmt *)ctx->opaque_statements[index];
    if (statement != NULL) {
      (void)sqlite3_finalize(statement);
      ctx->opaque_statements[index] = NULL;
    }
  }
  rc = database == NULL ? SQLITE_OK : sqlite3_close(database);
  if (rc != SQLITE_OK)
    return HOST_BOUNDARY_FAILURE;
  clear_bytes(ctx, sizeof(*ctx));
  return 0;
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdll-attribute-on-redeclaration"
AL_CAMPFIRE_EXPORT int32_t al_host_campfire_room_read_v1(
    void *provider_context, uint32_t operation,
    const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_room_read_context_v1 *ctx =
      (al_campfire_room_read_context_v1 *)provider_context;
  uint32_t effective_capacity;
  uint32_t minimum_output_extent;
  uint32_t expected_count;
  uint32_t expected_input_kind = AL_OWNING_TYPE_I64;
  uint32_t expected_active = 0u;
  int64_t membership_user_id = 0;
  int64_t membership_room_id = 0;
  al_room_read_refresh_input refresh_validation;
  int32_t result;

  if (output_extent_bytes == NULL)
    return HOST_BOUNDARY_FAILURE;
  *output_extent_bytes = 0u;
  if (ctx == NULL || ctx->cookie != ROOM_READ_CONTEXT_COOKIE ||
      ctx->opaque_db == NULL ||
      ctx->owner_thread_id != GetCurrentThreadId())
    return HOST_BOUNDARY_FAILURE;
  if (!__atomic_compare_exchange_n(&ctx->active, &expected_active, 1u, 0,
                                   __ATOMIC_ACQUIRE, __ATOMIC_RELAXED))
    return HOST_BOUNDARY_FAILURE;
  if (!require_autocommit(ctx)) {
    release_context_call(ctx);
    return HOST_BOUNDARY_FAILURE;
  }
  if (output == NULL || !valid_layout(layout) ||
      output_type_index >= layout->type_count ||
      (input_count != 0u && inputs == NULL)) {
    result = HOST_BOUNDARY_FAILURE;
    goto callback_complete;
  }
  minimum_output_extent = layout->types[output_type_index].minimum_extent_bytes;
  if (minimum_output_extent < 8u ||
      minimum_output_extent > AL_CAMPFIRE_ROOM_READ_OUTPUT_LIMIT) {
    result = HOST_BOUNDARY_FAILURE;
    goto callback_complete;
  }
  effective_capacity = output_capacity_bytes;
  if (effective_capacity > AL_CAMPFIRE_ROOM_READ_OUTPUT_LIMIT)
    effective_capacity = AL_CAMPFIRE_ROOM_READ_OUTPUT_LIMIT;
  if (effective_capacity < minimum_output_extent) {
    result = HOST_BOUNDARY_FAILURE;
    goto callback_complete;
  }

  if (operation == AL_CAMPFIRE_COOKIE_VERIFY ||
      operation == AL_CAMPFIRE_BROWSER_VERSION) {
    result = operation == AL_CAMPFIRE_COOKIE_VERIFY
                 ? al_campfire_verify_cookie_v1(
                       ctx->pure_context, layout, inputs, input_count,
                       output_type_index, output, effective_capacity,
                       output_extent_bytes)
                 : al_campfire_browser_version_v1(
                       ctx->pure_context, layout, inputs, input_count,
                       output_type_index, output, effective_capacity,
                       output_extent_bytes);
    goto callback_complete;
  }

  switch (operation) {
  case AL_CAMPFIRE_SESSION_FIND:
  case AL_CAMPFIRE_USER_BOT_KEY:
    expected_count = 1u;
    expected_input_kind = AL_OWNING_TYPE_STRING;
    break;
  case AL_CAMPFIRE_SESSION_REFRESH:
    expected_count = 1u;
    expected_input_kind = AL_OWNING_TYPE_RECORD;
    break;
  case AL_CAMPFIRE_USER_FIND:
    expected_count = 1u;
    break;
  case AL_CAMPFIRE_ROOM_FIND:
    expected_count = 2u;
    break;
  case AL_CAMPFIRE_MEMBERSHIP_EXISTS:
    expected_count = 1u;
    expected_input_kind = AL_OWNING_TYPE_RECORD;
    break;
  case AL_CAMPFIRE_MESSAGES_LAST_PAGE:
    expected_count = 1u;
    break;
  default:
    result = HOST_BOUNDARY_FAILURE;
    goto callback_complete;
  }
  if (input_count != expected_count ||
      !validate_sql_output_type(layout, operation, output_type_index)) {
    result = HOST_BOUNDARY_FAILURE;
    goto callback_complete;
  }

  if (operation == AL_CAMPFIRE_SESSION_FIND ||
      operation == AL_CAMPFIRE_USER_BOT_KEY) {
    if (!input_matches(layout, &inputs[0], expected_input_kind)) {
      result = HOST_BOUNDARY_FAILURE;
      goto callback_complete;
    }
  } else if (operation == AL_CAMPFIRE_SESSION_REFRESH) {
    if (!input_matches(layout, &inputs[0], AL_OWNING_TYPE_RECORD) ||
        !parse_session_refresh_input(layout, &inputs[0],
                                     &refresh_validation)) {
      result = HOST_BOUNDARY_FAILURE;
      goto callback_complete;
    }
  } else if (operation == AL_CAMPFIRE_ROOM_FIND) {
    if (!input_matches(layout, &inputs[0], AL_OWNING_TYPE_I64) ||
        !input_matches(layout, &inputs[1], AL_OWNING_TYPE_I64)) {
      result = HOST_BOUNDARY_FAILURE;
      goto callback_complete;
    }
  } else if (operation == AL_CAMPFIRE_MEMBERSHIP_EXISTS) {
    if (!input_matches(layout, &inputs[0], AL_OWNING_TYPE_RECORD) ||
        !parse_membership_input(layout, &inputs[0], &membership_user_id,
                                &membership_room_id)) {
      result = HOST_BOUNDARY_FAILURE;
      goto callback_complete;
    }
  } else if (operation == AL_CAMPFIRE_USER_FIND ||
             operation == AL_CAMPFIRE_MESSAGES_LAST_PAGE) {
    if (!input_matches(layout, &inputs[0], AL_OWNING_TYPE_I64)) {
      result = HOST_BOUNDARY_FAILURE;
      goto callback_complete;
    }
  }
  switch (operation) {
  case AL_CAMPFIRE_SESSION_FIND:
    result = session_find_by_token(ctx, inputs, output,
                                   effective_capacity, output_extent_bytes);
    break;
  case AL_CAMPFIRE_SESSION_REFRESH:
    result = session_refresh_before(ctx, layout, inputs, output,
                                    effective_capacity, output_extent_bytes);
    break;
  case AL_CAMPFIRE_USER_FIND:
    result = user_find_by_id(ctx, (int64_t)load_u64_le(inputs[0].data), output,
                             effective_capacity, output_extent_bytes);
    break;
  case AL_CAMPFIRE_USER_BOT_KEY:
    result = user_authenticate_bot_key(ctx, &inputs[0], output,
                                      effective_capacity, output_extent_bytes);
    break;
  case AL_CAMPFIRE_ROOM_FIND:
    result = room_find_for_user(
        ctx, (int64_t)load_u64_le(inputs[0].data),
        (int64_t)load_u64_le(inputs[1].data), output, effective_capacity,
        output_extent_bytes);
    break;
  case AL_CAMPFIRE_MEMBERSHIP_EXISTS:
    result = membership_exists(ctx, membership_user_id, membership_room_id,
                                output, effective_capacity,
                                output_extent_bytes);
    break;
  case AL_CAMPFIRE_MESSAGES_LAST_PAGE:
    result = messages_last_page(ctx, (int64_t)load_u64_le(inputs[0].data),
                                output, effective_capacity,
                                output_extent_bytes);
    break;
  default:
    result = HOST_BOUNDARY_FAILURE;
    break;
  }
callback_complete:
  cleanup_statements(ctx);
  if (sqlite3_get_autocommit((sqlite3 *)ctx->opaque_db) == 0) {
    (void)rollback_transaction(ctx);
    result = HOST_BOUNDARY_FAILURE;
  }
  if (result == 0 &&
      (*output_extent_bytes < minimum_output_extent ||
       *output_extent_bytes > effective_capacity))
    result = HOST_BOUNDARY_FAILURE;
  if ((__atomic_load_n(&ctx->active, __ATOMIC_ACQUIRE) &
       ROOM_READ_CONTEXT_STATE_POISONED) != 0u)
    result = HOST_BOUNDARY_FAILURE;
  release_context_call(ctx);
  if (result != 0)
    *output_extent_bytes = 0u;
  return result;
}
#pragma clang diagnostic pop

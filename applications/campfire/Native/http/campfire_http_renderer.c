#include "campfire_http_renderer.h"


#include <stddef.h>
#include <stdint.h>

_Static_assert(sizeof(void *) == 8u, "HTTP renderer prototype requires x64");
_Static_assert(sizeof(al_campfire_http_utf16_span_v1) == 16u,
               "HTTP renderer UTF-16 span ABI changed");
_Static_assert(offsetof(al_campfire_http_utf16_span_v1, code_units) == 8u,
               "HTTP renderer UTF-16 span length offset changed");
_Static_assert(offsetof(al_campfire_http_utf16_span_v1, present) == 12u,
               "HTTP renderer UTF-16 span presence offset changed");
_Static_assert(sizeof(al_campfire_http_render_metadata_v1) == 96u,
               "HTTP renderer metadata ABI size changed");
_Static_assert(_Alignof(al_campfire_http_render_metadata_v1) == 8u,
               "HTTP renderer metadata alignment changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, content_type_utf8) == 8u,
               "HTTP renderer content-type pointer offset changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, location_utf8) == 24u,
               "HTTP renderer location pointer offset changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, refresh_session_token) == 40u,
               "HTTP renderer refresh-token span offset changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, last_room) == 64u,
               "HTTP renderer last-room offset changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, alert) == 72u,
               "HTTP renderer alert span offset changed");
_Static_assert(offsetof(al_campfire_http_render_metadata_v1, complete_login) == 88u,
               "HTTP renderer completion flags offset changed");

enum al_http_view_ordinal {
  AL_HTTP_VIEW_EMPTY = 0u,
  AL_HTTP_VIEW_ROOM = 1u,
  AL_HTTP_VIEW_APPEND_MESSAGE = 2u,
  AL_HTTP_VIEW_REDIRECT_ROOT = 3u,
  AL_HTTP_VIEW_REDIRECT_SESSION = 4u,
  AL_HTTP_VIEW_INCOMPATIBLE_BROWSER = 5u,
  AL_HTTP_VIEW_ROOM_NOT_FOUND = 6u,
  AL_HTTP_VIEW_LOGIN_PAGE = 7u,
  AL_HTTP_VIEW_REDIRECT_AUTHENTICATED = 8u,
  AL_HTTP_VIEW_REDIRECT_FIRST_RUN = 9u,
  AL_HTTP_VIEW_REDIRECT_SIGNED_OUT = 10u
};

enum { AL_HTTP_ROOM_KIND_CASE_COUNT = 3u, AL_HTTP_VIEW_CASE_COUNT = 11u };

typedef struct al_http_cursor {
  const uint8_t *bytes;
  uint32_t extent;
  uint32_t offset;
} al_http_cursor;

typedef struct al_http_text {
  const uint8_t *utf16le;
  uint32_t code_units;
} al_http_text;

typedef struct al_http_optional_text {
  al_http_text value;
  uint32_t present;
} al_http_optional_text;

typedef struct al_http_room_row {
  int64_t id;
  al_http_optional_text name;
  uint32_t kind;
} al_http_room_row;

typedef struct al_http_message_row {
  int64_t id;
  int64_t room_id;
  al_http_text client_message_id;
  al_http_optional_text body;
} al_http_message_row;

typedef struct al_http_parsed_response {
  int64_t status;
  uint32_t view;
  al_http_text text;
  al_http_room_row room;
  uint32_t has_room;
  uint32_t messages_count;
  uint32_t messages_items_offset;
  uint32_t has_message;
  al_http_optional_text refresh_token;
  uint32_t remember_return_to;
  uint32_t has_last_room;
  int64_t last_room;
  al_http_text body;
} al_http_parsed_response;

typedef struct al_http_body_sink {
  uint8_t *bytes;
  uint32_t capacity;
  uint32_t used;
  uint32_t writing;
} al_http_body_sink;

static const uint8_t al_http_html_content_type[] =
    "text/html; charset=utf-8";
static const uint8_t al_http_plain_content_type[] =
    "text/plain; charset=utf-8";
static const uint8_t al_http_location_root[] = "/";
static const uint8_t al_http_location_session[] = "/session/new";

static int al_http_range_valid(const al_http_cursor *cursor,
                               uint32_t byte_count) {
  return cursor->offset <= cursor->extent &&
         byte_count <= cursor->extent - cursor->offset;
}

static int al_http_read_u32(al_http_cursor *cursor, uint32_t *out_value) {
  uint32_t value;
  uint32_t index;
  if (!al_http_range_valid(cursor, 4u))
    return 0;
  value = 0u;
  for (index = 0u; index < 4u; ++index)
    value |= (uint32_t)cursor->bytes[cursor->offset + index] << (index * 8u);
  cursor->offset += 4u;
  *out_value = value;
  return 1;
}

static int al_http_read_u64(al_http_cursor *cursor, uint64_t *out_value) {
  uint64_t value;
  uint32_t index;
  if (!al_http_range_valid(cursor, 8u))
    return 0;
  value = 0u;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)cursor->bytes[cursor->offset + index] << (index * 8u);
  cursor->offset += 8u;
  *out_value = value;
  return 1;
}

static int64_t al_http_signed_from_bits(uint64_t bits) {
  if (bits <= (uint64_t)INT64_MAX)
    return (int64_t)bits;
  {
    const uint64_t magnitude = (~bits) + 1u;
    if (magnitude == (UINT64_C(1) << 63u))
      return INT64_MIN;
    return -(int64_t)magnitude;
  }
}

static int al_http_read_i64(al_http_cursor *cursor, int64_t *out_value) {
  uint64_t bits;
  if (!al_http_read_u64(cursor, &bits))
    return 0;
  *out_value = al_http_signed_from_bits(bits);
  return 1;
}

static int al_http_read_bool(al_http_cursor *cursor, uint32_t *out_value) {
  uint64_t bits;
  if (!al_http_read_u64(cursor, &bits) || bits > 1u)
    return 0;
  *out_value = (uint32_t)bits;
  return 1;
}

static int al_http_read_text(al_http_cursor *cursor, al_http_text *out_text) {
  const uint32_t start = cursor->offset;
  uint32_t count;
  uint32_t reserved;
  uint64_t payload_bytes;
  uint64_t padded_bytes;
  uint64_t index;

  if (!al_http_range_valid(cursor, 8u))
    return 0;
  count = (uint32_t)cursor->bytes[start] |
          ((uint32_t)cursor->bytes[start + 1u] << 8u) |
          ((uint32_t)cursor->bytes[start + 2u] << 16u) |
          ((uint32_t)cursor->bytes[start + 3u] << 24u);
  reserved = (uint32_t)cursor->bytes[start + 4u] |
             ((uint32_t)cursor->bytes[start + 5u] << 8u) |
             ((uint32_t)cursor->bytes[start + 6u] << 16u) |
             ((uint32_t)cursor->bytes[start + 7u] << 24u);
  if (reserved != 0u)
    return 0;

  payload_bytes = 8u + (uint64_t)count;
  padded_bytes = (payload_bytes + 7u) & ~UINT64_C(7);
  if (padded_bytes > UINT32_MAX ||
      !al_http_range_valid(cursor, (uint32_t)padded_bytes))
    return 0;
  for (index = payload_bytes; index < padded_bytes; ++index) {
    if (cursor->bytes[start + (uint32_t)index] != 0u)
      return 0;
  }

  out_text->utf16le = cursor->bytes + start + 8u;
  out_text->code_units = count;
  cursor->offset += (uint32_t)padded_bytes;
  return 1;
}

static int al_http_read_optional_text(al_http_cursor *cursor,
                                      al_http_optional_text *out_text) {
  uint64_t tag;
  if (!al_http_read_u64(cursor, &tag))
    return 0;
  if (tag == 1u) {
    out_text->present = 0u;
    out_text->value.utf16le = NULL;
    out_text->value.code_units = 0u;
    return 1;
  }
  if (tag != 0u || !al_http_read_text(cursor, &out_text->value))
    return 0;
  out_text->present = 1u;
  return 1;
}

static int al_http_read_optional_i64(al_http_cursor *cursor,
                                     uint32_t *out_present,
                                     int64_t *out_value) {
  uint64_t tag;
  if (!al_http_read_u64(cursor, &tag))
    return 0;
  if (tag == 1u) {
    *out_present = 0u;
    *out_value = 0;
    return 1;
  }
  if (tag != 0u || !al_http_read_i64(cursor, out_value))
    return 0;
  *out_present = 1u;
  return 1;
}

static int al_http_parse_room_row(al_http_cursor *cursor,
                                  al_http_room_row *out_room) {
  int64_t creator_id;
  int64_t kind;
  al_http_text created_at;
  al_http_text updated_at;
  if (!al_http_read_i64(cursor, &out_room->id) ||
      !al_http_read_optional_text(cursor, &out_room->name) ||
      !al_http_read_i64(cursor, &kind) || kind < 0 ||
      kind >= (int64_t)AL_HTTP_ROOM_KIND_CASE_COUNT ||
      !al_http_read_i64(cursor, &creator_id) ||
      !al_http_read_text(cursor, &created_at) ||
      !al_http_read_text(cursor, &updated_at))
    return 0;
  out_room->kind = (uint32_t)kind;
  (void)creator_id;
  (void)created_at;
  (void)updated_at;
  return 1;
}

static int al_http_parse_message_row(al_http_cursor *cursor,
                                     al_http_message_row *out_message) {
  int64_t creator_id;
  al_http_text created_at;
  al_http_text updated_at;
  if (!al_http_read_i64(cursor, &out_message->id) ||
      !al_http_read_i64(cursor, &out_message->room_id) ||
      !al_http_read_i64(cursor, &creator_id) ||
      !al_http_read_text(cursor, &out_message->client_message_id) ||
      !al_http_read_optional_text(cursor, &out_message->body) ||
      !al_http_read_text(cursor, &created_at) ||
      !al_http_read_text(cursor, &updated_at))
    return 0;
  (void)creator_id;
  (void)created_at;
  (void)updated_at;
  return 1;
}

static int al_http_parse_response(const uint8_t *bytes, uint32_t extent,
                                  al_http_parsed_response *out_response) {
  al_http_cursor cursor;
  uint64_t view;
  uint64_t room_tag;
  uint32_t list_count;
  uint32_t list_reserved;
  uint32_t index;
  al_http_message_row message_row;

  cursor.bytes = bytes;
  cursor.extent = extent;
  cursor.offset = 0u;

  if (!al_http_read_i64(&cursor, &out_response->status) ||
      out_response->status < 100 || out_response->status > 599 ||
      !al_http_read_u64(&cursor, &view) || view >= AL_HTTP_VIEW_CASE_COUNT ||
      !al_http_read_text(&cursor, &out_response->text))
    return 0;
  out_response->view = (uint32_t)view;

  if (!al_http_read_u64(&cursor, &room_tag))
    return 0;
  if (room_tag == 0u) {
    if (!al_http_parse_room_row(&cursor, &out_response->room))
      return 0;
    out_response->has_room = 1u;
  } else if (room_tag == 1u) {
    out_response->has_room = 0u;
  } else {
    return 0;
  }

  if (!al_http_read_u32(&cursor, &list_count) ||
      !al_http_read_u32(&cursor, &list_reserved) || list_reserved != 0u ||
      list_count > (cursor.extent - cursor.offset) / 56u)
    return 0;
  out_response->messages_count = list_count;
  out_response->messages_items_offset = cursor.offset;
  for (index = 0u; index < list_count; ++index) {
    if (!al_http_parse_message_row(&cursor, &message_row))
      return 0;
  }

  if (!al_http_read_u64(&cursor, &room_tag))
    return 0;
  if (room_tag == 0u) {
    if (!al_http_parse_message_row(&cursor, &message_row))
      return 0;
    out_response->has_message = 1u;
  } else if (room_tag == 1u) {
    out_response->has_message = 0u;
  } else {
    return 0;
  }

  if (!al_http_read_optional_text(&cursor, &out_response->refresh_token) ||
      !al_http_read_bool(&cursor, &out_response->remember_return_to) ||
      !al_http_read_optional_i64(&cursor, &out_response->has_last_room,
                                 &out_response->last_room) ||
      !al_http_read_text(&cursor, &out_response->body) ||
      cursor.offset != cursor.extent)
    return 0;

  return 1;
}

static int al_http_text_is_empty(al_http_text text) {
  return text.code_units == 0u;
}

static int al_http_no_route_data(const al_http_parsed_response *response) {
  return response->has_room == 0u && response->has_message == 0u &&
         response->messages_count == 0u;
}

static int al_http_room_messages_match(const uint8_t *bytes,
                                       uint32_t extent,
                                       const al_http_parsed_response *response) {
  al_http_cursor cursor;
  uint32_t index;
  al_http_message_row message;
  cursor.bytes = bytes;
  cursor.extent = extent;
  cursor.offset = response->messages_items_offset;
  for (index = 0u; index < response->messages_count; ++index) {
    if (!al_http_parse_message_row(&cursor, &message) ||
        message.room_id != response->room.id)
      return 0;
  }
  return 1;
}

static int al_http_view_supported(uint32_t view) {
  return view == AL_HTTP_VIEW_EMPTY || view == AL_HTTP_VIEW_ROOM ||
         view == AL_HTTP_VIEW_REDIRECT_ROOT ||
         view == AL_HTTP_VIEW_REDIRECT_SESSION ||
         view == AL_HTTP_VIEW_INCOMPATIBLE_BROWSER ||
         view == AL_HTTP_VIEW_ROOM_NOT_FOUND;
}

static int al_http_contract_valid(const uint8_t *bytes, uint32_t extent,
                                  const al_http_parsed_response *response) {
  const int no_route_data = al_http_no_route_data(response);
  const int text_empty = al_http_text_is_empty(response->text);

  switch (response->view) {
  case AL_HTTP_VIEW_EMPTY:
    return no_route_data && text_empty &&
           response->remember_return_to == 0u &&
           response->has_last_room == 0u;
  case AL_HTTP_VIEW_ROOM:
    return response->status == 200 && response->has_room != 0u &&
           response->has_message == 0u &&
           response->remember_return_to == 0u && text_empty &&
           (response->has_last_room == 0u ||
            response->last_room == response->room.id) &&
           al_http_room_messages_match(bytes, extent, response);
  case AL_HTTP_VIEW_REDIRECT_ROOT:
    return response->status == 302 && no_route_data &&
           response->remember_return_to == 0u &&
           response->has_last_room == 0u;
  case AL_HTTP_VIEW_REDIRECT_SESSION:
    return response->status == 302 && no_route_data && text_empty &&
           response->remember_return_to != 0u &&
           response->refresh_token.present == 0u &&
           response->has_last_room == 0u;
  case AL_HTTP_VIEW_INCOMPATIBLE_BROWSER:
    return response->status == 200 && no_route_data &&
           response->remember_return_to == 0u &&
           response->has_last_room == 0u && text_empty;
  case AL_HTTP_VIEW_ROOM_NOT_FOUND:
    return response->status == 200 && no_route_data &&
           response->remember_return_to == 0u &&
           response->has_last_room == 0u;
  default:
    return 0;
  }
}

static int al_http_sink_append(al_http_body_sink *sink, const uint8_t *bytes,
                               uint32_t length) {
  uint32_t index;
  if (length > UINT32_MAX - sink->used)
    return 0;
  if (sink->writing != 0u) {
    if (sink->used > sink->capacity || length > sink->capacity - sink->used)
      return 0;
    for (index = 0u; index < length; ++index)
      sink->bytes[sink->used + index] = bytes[index];
  }
  sink->used += length;
  return 1;
}

/* The body is rendered by the Flow application (applications/campfire/
 * views.flow) and arrives as the response's last field, already UTF-8. HTML
 * views send it as-is; every other view must carry an empty body. */
static int al_http_emit_body(al_http_body_sink *sink,
                             const al_http_parsed_response *response) {
  switch (response->view) {
  case AL_HTTP_VIEW_EMPTY:
  case AL_HTTP_VIEW_REDIRECT_ROOT:
  case AL_HTTP_VIEW_REDIRECT_SESSION:
    return al_http_text_is_empty(response->body);
  case AL_HTTP_VIEW_INCOMPATIBLE_BROWSER:
  case AL_HTTP_VIEW_ROOM_NOT_FOUND:
  case AL_HTTP_VIEW_ROOM:
    return al_http_sink_append(sink, response->body.utf16le,
                               response->body.code_units);
  default:
    return 0;
  }
}

static void al_http_clear_metadata(
    al_campfire_http_render_metadata_v1 *metadata) {
  uint8_t *bytes = (uint8_t *)(void *)metadata;
  uint32_t index;
  for (index = 0u; index < (uint32_t)sizeof(*metadata); ++index)
    bytes[index] = 0u;
}

static void al_http_set_metadata(
    const al_http_parsed_response *response,
    al_campfire_http_render_metadata_v1 *metadata) {
  metadata->status = (int32_t)response->status;
  metadata->reserved0 = 0u;
  if (response->view == AL_HTTP_VIEW_EMPTY) {
    metadata->content_type_utf8 = al_http_plain_content_type;
    metadata->content_type_bytes =
        (uint32_t)(sizeof(al_http_plain_content_type) - 1u);
  } else {
    metadata->content_type_utf8 = al_http_html_content_type;
    metadata->content_type_bytes =
        (uint32_t)(sizeof(al_http_html_content_type) - 1u);
  }

  metadata->has_location = 0u;
  metadata->location_utf8 = NULL;
  metadata->location_bytes = 0u;
  if (response->view == AL_HTTP_VIEW_REDIRECT_ROOT) {
    metadata->has_location = 1u;
    metadata->location_utf8 = al_http_location_root;
    metadata->location_bytes =
        (uint32_t)(sizeof(al_http_location_root) - 1u);
  } else if (response->view == AL_HTTP_VIEW_REDIRECT_SESSION) {
    metadata->has_location = 1u;
    metadata->location_utf8 = al_http_location_session;
    metadata->location_bytes =
        (uint32_t)(sizeof(al_http_location_session) - 1u);
  }

  metadata->refresh_session_token.data_utf16le =
      response->refresh_token.present != 0u
          ? response->refresh_token.value.utf16le
          : NULL;
  metadata->refresh_session_token.code_units =
      response->refresh_token.present != 0u
          ? response->refresh_token.value.code_units
          : 0u;
  metadata->refresh_session_token.present = response->refresh_token.present;
  metadata->remember_return_to = response->remember_return_to;
  metadata->has_last_room = response->has_last_room;
  metadata->last_room = response->has_last_room != 0u ? response->last_room : 0;

  if (response->view == AL_HTTP_VIEW_REDIRECT_ROOT) {
    metadata->alert.data_utf16le = response->text.utf16le;
    metadata->alert.code_units = response->text.code_units;
    metadata->alert.present = 1u;
  } else {
    metadata->alert.data_utf16le = NULL;
    metadata->alert.code_units = 0u;
    metadata->alert.present = 0u;
  }
  metadata->complete_login = 0u;
  metadata->complete_logout = 0u;
}

int32_t al_campfire_http_render_v1(
    uint32_t manifest_format_version, uint32_t layout_schema_version,
    uint32_t layout_abi_version, const uint8_t *response_abi5,
    uint32_t response_extent_bytes, uint8_t *body_utf8,
    uint32_t body_capacity_bytes, uint32_t *out_body_required_bytes,
    al_campfire_http_render_metadata_v1 *out_metadata) {
  al_http_parsed_response response;
  al_http_body_sink measure;
  al_http_body_sink write;

  if (out_metadata != NULL)
    al_http_clear_metadata(out_metadata);
  if (out_body_required_bytes == NULL)
    return AL_CAMPFIRE_HTTP_RENDER_INVALID_ARGUMENT;
  *out_body_required_bytes = 0u;
  if (out_metadata == NULL ||
      (response_extent_bytes != 0u && response_abi5 == NULL) ||
      (body_capacity_bytes != 0u && body_utf8 == NULL))
    return AL_CAMPFIRE_HTTP_RENDER_INVALID_ARGUMENT;

  if (manifest_format_version !=
          AL_CAMPFIRE_HTTP_RENDERER_MANIFEST_FORMAT_VERSION ||
      layout_schema_version !=
          AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_SCHEMA_VERSION ||
      layout_abi_version != AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_ABI_VERSION)
    return AL_CAMPFIRE_HTTP_RENDER_MANIFEST_MISMATCH;
  if (response_abi5 == NULL || response_extent_bytes == 0u ||
      response_extent_bytes > AL_CAMPFIRE_HTTP_RENDERER_MAX_RESPONSE_BYTES ||
      (((uintptr_t)response_abi5) & (uintptr_t)1u) != 0u)
    return AL_CAMPFIRE_HTTP_RENDER_MALFORMED_RESPONSE;

  if (!al_http_parse_response(response_abi5, response_extent_bytes,
                              &response))
    return AL_CAMPFIRE_HTTP_RENDER_MALFORMED_RESPONSE;
  if (!al_http_view_supported(response.view))
    return AL_CAMPFIRE_HTTP_RENDER_UNSUPPORTED_VIEW;
  if (!al_http_contract_valid(response_abi5, response_extent_bytes,
                              &response))
    return AL_CAMPFIRE_HTTP_RENDER_MALFORMED_RESPONSE;

  measure.bytes = NULL;
  measure.capacity = UINT32_MAX;
  measure.used = 0u;
  measure.writing = 0u;
  if (!al_http_emit_body(&measure, &response))
    return AL_CAMPFIRE_HTTP_RENDER_LENGTH_OVERFLOW;
  *out_body_required_bytes = measure.used;
  al_http_set_metadata(&response, out_metadata);

  if (measure.used > body_capacity_bytes)
    return AL_CAMPFIRE_HTTP_RENDER_OUTPUT_TOO_SMALL;
  if (measure.used != 0u && body_utf8 == NULL)
    return AL_CAMPFIRE_HTTP_RENDER_INVALID_ARGUMENT;
  if (measure.used == 0u)
    return AL_CAMPFIRE_HTTP_RENDER_OK;

  write.bytes = body_utf8;
  write.capacity = body_capacity_bytes;
  write.used = 0u;
  write.writing = 1u;
  if (!al_http_emit_body(&write, &response) ||
      write.used != measure.used) {
    *out_body_required_bytes = 0u;
    al_http_clear_metadata(out_metadata);
    return AL_CAMPFIRE_HTTP_RENDER_MALFORMED_RESPONSE;
  }
  return AL_CAMPFIRE_HTTP_RENDER_OK;
}

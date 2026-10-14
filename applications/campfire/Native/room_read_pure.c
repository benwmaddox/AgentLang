#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>

#include <stdint.h>
#include <stddef.h>
#include <limits.h>
#include <string.h>

#include "room_read_pure.h"

#pragma comment(lib, "bcrypt.lib")

#define AL_CAMPFIRE_PURE_CONTEXT_MAGIC UINT32_C(0x43505231)
#define AL_CAMPFIRE_COOKIE_MAX_BYTES 4096u
#define AL_CAMPFIRE_COOKIE_NAME_BYTES 13u
#define AL_CAMPFIRE_SCRATCH_BYTES 4096u
#define AL_CAMPFIRE_TOKEN_UNITS 4096u
#define AL_CAMPFIRE_SIGNING_KEY_BYTES 64u
#define AL_CAMPFIRE_SHA1_BYTES 20u
#define AL_CAMPFIRE_HEX_SHA1_BYTES 40u
#define AL_CAMPFIRE_HMAC_OBJECT_CAPACITY 512u
#define AL_CAMPFIRE_JSON_MAX_DEPTH 32u

typedef struct al_campfire_pure_context {
  uint32_t magic;
  SRWLOCK lock;
  BCRYPT_ALG_HANDLE hmac_sha1_algorithm;
  ULONG hmac_object_bytes;
  uint8_t signing_key[AL_CAMPFIRE_SIGNING_KEY_BYTES];
  uint8_t hmac_object[AL_CAMPFIRE_HMAC_OBJECT_CAPACITY];
  BCRYPT_ALG_HANDLE hmac_sha256_algorithm;
  ULONG hmac256_object_bytes;
  uint8_t avatar_key[AL_CAMPFIRE_SIGNING_KEY_BYTES];
  uint8_t hmac256_object[AL_CAMPFIRE_HMAC_OBJECT_CAPACITY];
  uint8_t raw_ascii[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t payload[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t message[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t inner_json[AL_CAMPFIRE_SCRATCH_BYTES];
  uint16_t token[AL_CAMPFIRE_TOKEN_UNITS];
} al_campfire_pure_context;

typedef struct al_campfire_string_view {
  const uint8_t *data;
  uint32_t code_units;
  uint32_t extent_bytes;
} al_campfire_string_view;

typedef struct al_campfire_json_parser {
  const uint8_t *bytes;
  uint32_t length;
  uint32_t index;
} al_campfire_json_parser;

typedef struct al_campfire_rails_fields {
  uint8_t message_is_string;
  uint8_t message_is_ascii;
  uint32_t message_length;
  uint8_t purpose_matches;
  uint8_t expiry_present;
  uint8_t expiry_valid;
  int64_t expiry_utc_ticks;
} al_campfire_rails_fields;

typedef enum al_campfire_segment_kind {
  AL_CAMPFIRE_SEGMENT_INTEGER = 1,
  AL_CAMPFIRE_SEGMENT_TEXT = 2
} al_campfire_segment_kind;

typedef struct al_campfire_version_segment {
  al_campfire_segment_kind kind;
  const al_campfire_string_view *owner;
  uint32_t start_unit;
  uint32_t length_units;
  uint8_t is_zero_literal;
} al_campfire_version_segment;

static uint32_t al_campfire_read_u32_le(const uint8_t *bytes) {
  return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8u) |
         ((uint32_t)bytes[2] << 16u) | ((uint32_t)bytes[3] << 24u);
}

static uint64_t al_campfire_read_u64_le(const uint8_t *bytes) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[index] << (index * 8u);
  return value;
}

static void al_campfire_write_u32_le(uint8_t *bytes, uint32_t value) {
  uint32_t index;
  for (index = 0u; index < 4u; ++index)
    bytes[index] = (uint8_t)(value >> (index * 8u));
}

static void al_campfire_write_u64_le(uint8_t *bytes, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bytes[index] = (uint8_t)(value >> (index * 8u));
}

static int32_t al_campfire_layout_valid(const al_owning_layout *layout) {
  return layout != NULL &&
         layout->abi_version == AL_OWNING_LAYOUT_ABI_VERSION &&
         layout->types != NULL && layout->type_count > 0u &&
         layout->type_count <= AL_OWNING_LAYOUT_MAX_TYPES &&
         (layout->field_count == 0u || layout->fields != NULL) &&
         layout->field_count <= AL_OWNING_LAYOUT_MAX_FIELDS;
}

static int32_t al_campfire_read_input_string(
    const al_owning_layout *layout,
    const al_campfire_room_read_input *input,
    al_campfire_string_view *out_view) {
  const al_owning_type_descriptor *type;
  uint32_t code_units;
  uint64_t payload;
  uint64_t extent;
  uint32_t index;
  if (layout == NULL || input == NULL || out_view == NULL ||
      input->data == NULL || input->type_index >= layout->type_count)
    return 0;
  type = &layout->types[input->type_index];
  if (type->kind != AL_OWNING_TYPE_STRING || input->extent_bytes < 8u)
    return 0;
  code_units = al_campfire_read_u32_le(input->data);
  if (input->data[4] != 0u || input->data[5] != 0u ||
      input->data[6] != 0u || input->data[7] != 0u)
    return 0;
  payload = 8u + (uint64_t)code_units;
  extent = (payload + 7u) & ~UINT64_C(7);
  if (extent > UINT32_MAX || extent != input->extent_bytes)
    return 0;
  for (index = (uint32_t)payload; index < input->extent_bytes; ++index) {
    if (input->data[index] != 0u)
      return 0;
  }
  out_view->data = input->data + 8u;
  out_view->code_units = code_units;
  out_view->extent_bytes = input->extent_bytes;
  return 1;
}

static int32_t al_campfire_read_input_i64(
    const al_owning_layout *layout,
    const al_campfire_room_read_input *input, int64_t *out_value) {
  if (layout == NULL || input == NULL || out_value == NULL ||
      input->data == NULL || input->type_index >= layout->type_count ||
      input->extent_bytes != 8u ||
      layout->types[input->type_index].kind != AL_OWNING_TYPE_I64)
    return 0;
  *out_value = (int64_t)al_campfire_read_u64_le(input->data);
  return 1;
}

/* One UTF-8 byte; callers compare ASCII values only. */
static uint16_t al_campfire_string_unit(const al_campfire_string_view *view,
                                       uint32_t index) {
  return view->data[index];
}

static int32_t al_campfire_string_is_ascii(
    const al_campfire_string_view *view) {
  uint32_t index;
  for (index = 0u; index < view->code_units; ++index) {
    if (al_campfire_string_unit(view, index) > 0x7fu)
      return 0;
  }
  return 1;
}

static void al_campfire_clear_scratch(al_campfire_pure_context *context) {
  SecureZeroMemory(context->hmac_object, sizeof(context->hmac_object));
  SecureZeroMemory(context->raw_ascii, sizeof(context->raw_ascii));
  SecureZeroMemory(context->payload, sizeof(context->payload));
  SecureZeroMemory(context->message, sizeof(context->message));
  SecureZeroMemory(context->inner_json, sizeof(context->inner_json));
  SecureZeroMemory(context->token, sizeof(context->token));
}

static int32_t al_campfire_utf8_byte_count(
    const al_campfire_string_view *view, uint32_t maximum,
    uint32_t *out_bytes) {
  if (view == NULL || out_bytes == NULL || view->code_units > maximum)
    return 0;
  *out_bytes = view->code_units;
  return 1;
}

static int32_t al_campfire_base64_value(uint8_t value) {
  if (value >= (uint8_t)'A' && value <= (uint8_t)'Z')
    return (int32_t)(value - (uint8_t)'A');
  if (value >= (uint8_t)'a' && value <= (uint8_t)'z')
    return 26 + (int32_t)(value - (uint8_t)'a');
  if (value >= (uint8_t)'0' && value <= (uint8_t)'9')
    return 52 + (int32_t)(value - (uint8_t)'0');
  if (value == (uint8_t)'+' || value == (uint8_t)'-')
    return 62;
  if (value == (uint8_t)'/' || value == (uint8_t)'_')
    return 63;
  return -1;
}

/* Matches RailsCookieFormat.urlSafeBase64Decode, including canonical pad bits. */
static int32_t al_campfire_decode_base64_urlsafe(
    const uint8_t *encoded, uint32_t encoded_length, uint8_t *decoded,
    uint32_t decoded_capacity, uint32_t *out_decoded_length) {
  uint32_t first_padding = encoded_length;
  uint32_t padding_count = 0u;
  uint32_t normalized_length;
  uint32_t input_index;
  uint32_t output_index = 0u;
  if (encoded == NULL || decoded == NULL || out_decoded_length == NULL ||
      encoded_length == 0u)
    return 0;
  for (input_index = 0u; input_index < encoded_length; ++input_index) {
    const uint8_t value = encoded[input_index];
    if (value == (uint8_t)'=') {
      if (first_padding == encoded_length)
        first_padding = input_index;
      ++padding_count;
    } else if (al_campfire_base64_value(value) < 0 ||
               first_padding != encoded_length) {
      return 0;
    }
  }
  if (padding_count > 2u ||
      (padding_count > 0u &&
       (encoded_length < 2u || first_padding < encoded_length - 2u ||
        encoded_length % 4u != 0u)))
    return 0;
  if (padding_count == 0u && encoded_length % 4u == 1u)
    return 0;
  normalized_length = encoded_length;
  if (padding_count == 0u)
    normalized_length += (4u - (normalized_length % 4u)) % 4u;
  if (normalized_length == 0u || normalized_length % 4u != 0u)
    return 0;
  if ((uint64_t)(normalized_length / 4u) * 3u < padding_count)
    return 0;
  if (((uint64_t)(normalized_length / 4u) * 3u) - padding_count >
      decoded_capacity)
    return 0;

  for (input_index = 0u; input_index < normalized_length; input_index += 4u) {
    int32_t values[4];
    uint32_t local;
    uint32_t actual_padding = 0u;
    for (local = 0u; local < 4u; ++local) {
      const uint32_t position = input_index + local;
      const uint8_t value = position < encoded_length ? encoded[position]
                                                       : (uint8_t)'=';
      if (value == (uint8_t)'=') {
        values[local] = 0;
        ++actual_padding;
      } else {
        values[local] = al_campfire_base64_value(value);
        if (values[local] < 0)
          return 0;
      }
    }
    if (actual_padding > 0u && input_index + 4u != normalized_length)
      return 0;
    if (actual_padding > 2u ||
        (actual_padding == 2u &&
         (input_index + 4u != normalized_length ||
          (input_index + 2u < encoded_length &&
           encoded[input_index + 2u] != (uint8_t)'='))) ||
        (actual_padding == 1u &&
         (input_index + 4u != normalized_length ||
          (input_index + 3u < encoded_length &&
           encoded[input_index + 3u] != (uint8_t)'='))))
      return 0;
    if ((actual_padding == 2u && (values[1] & 0x0f) != 0) ||
        (actual_padding == 1u && (values[2] & 0x03) != 0))
      return 0;
    decoded[output_index++] =
        (uint8_t)((values[0] << 2) | ((values[1] >> 4) & 0x03));
    if (actual_padding < 2u)
      decoded[output_index++] =
          (uint8_t)(((values[1] & 0x0f) << 4) | ((values[2] >> 2) & 0x0f));
    if (actual_padding == 0u)
      decoded[output_index++] =
          (uint8_t)(((values[2] & 0x03) << 6) | values[3]);
  }
  if (output_index != (uint32_t)(((uint64_t)(normalized_length / 4u) * 3u) -
                                 padding_count))
    return 0;
  *out_decoded_length = output_index;
  return 1;
}

static int32_t al_campfire_is_json_whitespace(uint8_t value) {
  return value == (uint8_t)' ' || value == (uint8_t)'\t' ||
         value == (uint8_t)'\r' || value == (uint8_t)'\n';
}

static void al_campfire_json_skip_whitespace(al_campfire_json_parser *parser) {
  while (parser->index < parser->length &&
         al_campfire_is_json_whitespace(parser->bytes[parser->index]))
    ++parser->index;
}

static int32_t al_campfire_hex_value(uint8_t value) {
  if (value >= (uint8_t)'0' && value <= (uint8_t)'9')
    return (int32_t)(value - (uint8_t)'0');
  if (value >= (uint8_t)'a' && value <= (uint8_t)'f')
    return 10 + (int32_t)(value - (uint8_t)'a');
  if (value >= (uint8_t)'A' && value <= (uint8_t)'F')
    return 10 + (int32_t)(value - (uint8_t)'A');
  return -1;
}

static int32_t al_campfire_read_utf8_scalar(
    const uint8_t *bytes, uint32_t length, uint32_t *index,
    uint32_t *out_scalar) {
  const uint8_t first = bytes[*index];
  uint32_t scalar;
  uint32_t count;
  uint32_t local;
  if (first <= 0x7fu) {
    *out_scalar = first;
    ++*index;
    return 1;
  }
  if (first >= 0xc2u && first <= 0xdfu) {
    scalar = (uint32_t)(first & 0x1fu);
    count = 1u;
  } else if (first >= 0xe0u && first <= 0xefu) {
    scalar = (uint32_t)(first & 0x0fu);
    count = 2u;
  } else if (first >= 0xf0u && first <= 0xf4u) {
    scalar = (uint32_t)(first & 0x07u);
    count = 3u;
  } else {
    return 0;
  }
  if (count > length - *index - 1u)
    return 0;
  for (local = 1u; local <= count; ++local) {
    const uint8_t next = bytes[*index + local];
    if ((next & 0xc0u) != 0x80u)
      return 0;
    scalar = (scalar << 6u) | (uint32_t)(next & 0x3fu);
  }
  if ((count == 1u && scalar < 0x80u) ||
      (count == 2u && scalar < 0x800u) ||
      (count == 3u && scalar < 0x10000u) || scalar > 0x10ffffu ||
      (scalar >= 0xd800u && scalar <= 0xdfffu))
    return 0;
  *index += count + 1u;
  *out_scalar = scalar;
  return 1;
}

static int32_t al_campfire_json_read_u_escape(
    al_campfire_json_parser *parser, uint16_t *out_unit) {
  uint32_t index;
  uint32_t value = 0u;
  if (parser->length - parser->index < 4u)
    return 0;
  for (index = 0u; index < 4u; ++index) {
    const int32_t digit =
        al_campfire_hex_value(parser->bytes[parser->index + index]);
    if (digit < 0)
      return 0;
    value = (value << 4u) | (uint32_t)digit;
  }
  parser->index += 4u;
  *out_unit = (uint16_t)value;
  return 1;
}

/* Parse a JSON string while optionally collecting UTF-16, ASCII, or comparing
 * its decoded contents with an ASCII property/value. System.Text.Json accepts
 * escaped unpaired surrogates; preserve those for token strings and make them
 * non-matching/non-base64 for cookie metadata. */
static int32_t al_campfire_json_parse_string(
    al_campfire_json_parser *parser, uint16_t *utf16_output,
    uint32_t utf16_capacity, uint32_t *out_utf16_units,
    uint8_t *ascii_output, uint32_t ascii_capacity,
    uint32_t *out_ascii_length, int32_t *out_all_ascii,
    const uint8_t *ascii_match, uint32_t ascii_match_length,
    int32_t *out_matches) {
  uint32_t utf16_length = 0u;
  uint32_t ascii_length = 0u;
  uint32_t match_index = 0u;
  int32_t all_ascii = 1;
  int32_t matches = ascii_match != NULL;
  if (parser == NULL || parser->index >= parser->length ||
      parser->bytes[parser->index] != (uint8_t)'"')
    return 0;
  ++parser->index;
  while (parser->index < parser->length) {
    uint32_t scalar;
    uint32_t units_to_emit = 1u;
    uint16_t first_unit;
    uint16_t second_unit = 0u;
    const uint8_t value = parser->bytes[parser->index];
    if (value == (uint8_t)'"') {
      ++parser->index;
      if (utf16_output != NULL && out_utf16_units != NULL)
        *out_utf16_units = utf16_length;
      if (ascii_output != NULL && out_ascii_length != NULL)
        *out_ascii_length = ascii_length;
      if (out_all_ascii != NULL)
        *out_all_ascii = all_ascii;
      if (out_matches != NULL)
        *out_matches = matches && match_index == ascii_match_length;
      return 1;
    }
    if (value < 0x20u)
      return 0;
    if (value == (uint8_t)'\\') {
      uint8_t escaped;
      ++parser->index;
      if (parser->index >= parser->length)
        return 0;
      escaped = parser->bytes[parser->index++];
      if (escaped == (uint8_t)'"' || escaped == (uint8_t)'\\' ||
          escaped == (uint8_t)'/') {
        scalar = escaped;
      } else if (escaped == (uint8_t)'b') {
        scalar = 0x08u;
      } else if (escaped == (uint8_t)'f') {
        scalar = 0x0cu;
      } else if (escaped == (uint8_t)'n') {
        scalar = 0x0au;
      } else if (escaped == (uint8_t)'r') {
        scalar = 0x0du;
      } else if (escaped == (uint8_t)'t') {
        scalar = 0x09u;
      } else if (escaped == (uint8_t)'u') {
        uint16_t escaped_unit;
        if (!al_campfire_json_read_u_escape(parser, &escaped_unit))
          return 0;
        scalar = escaped_unit;
        if (escaped_unit >= 0xd800u && escaped_unit <= 0xdbffu &&
            parser->length - parser->index >= 6u &&
            parser->bytes[parser->index] == (uint8_t)'\\' &&
            parser->bytes[parser->index + 1u] == (uint8_t)'u') {
          uint32_t saved_index = parser->index;
          uint16_t low_unit;
          parser->index += 2u;
          if (!al_campfire_json_read_u_escape(parser, &low_unit))
            return 0;
          if (low_unit >= 0xdc00u && low_unit <= 0xdfffu) {
            scalar = 0x10000u +
                     ((((uint32_t)escaped_unit - 0xd800u) << 10u) |
                      ((uint32_t)low_unit - 0xdc00u));
          } else {
            parser->index = saved_index;
          }
        }
      } else {
        return 0;
      }
    } else {
      if (!al_campfire_read_utf8_scalar(parser->bytes, parser->length,
                                        &parser->index, &scalar))
        return 0;
    }

    if (scalar <= 0xffffu) {
      first_unit = (uint16_t)scalar;
    } else {
      const uint32_t adjusted = scalar - 0x10000u;
      first_unit = (uint16_t)(0xd800u + (adjusted >> 10u));
      second_unit = (uint16_t)(0xdc00u + (adjusted & 0x3ffu));
      units_to_emit = 2u;
    }
    if (utf16_length > UINT32_MAX - units_to_emit ||
        (utf16_output != NULL &&
         units_to_emit > utf16_capacity - utf16_length))
      return 0;
    if (utf16_output != NULL) {
      utf16_output[utf16_length] = first_unit;
      if (units_to_emit == 2u)
        utf16_output[utf16_length + 1u] = second_unit;
    }
    utf16_length += units_to_emit;

    if (scalar <= 0x7fu) {
      const uint8_t ascii_value = (uint8_t)scalar;
      if (ascii_output != NULL) {
        if (ascii_length >= ascii_capacity)
          return 0;
        ascii_output[ascii_length] = ascii_value;
      }
      if (ascii_match != NULL) {
        if (match_index >= ascii_match_length ||
            ascii_match[match_index] != ascii_value)
          matches = 0;
      }
      ++ascii_length;
      ++match_index;
    } else {
      all_ascii = 0;
      matches = 0;
      if (scalar <= 0x7ffu)
        ascii_length += 2u;
      else if (scalar <= 0xffffu)
        ascii_length += 3u;
      else
        ascii_length += 4u;
      match_index += scalar <= 0x7ffu ? 2u : scalar <= 0xffffu ? 3u : 4u;
      if (ascii_output != NULL && ascii_length > ascii_capacity)
        return 0;
    }
  }
  return 0;
}

static int32_t al_campfire_json_parse_number(al_campfire_json_parser *parser) {
  uint32_t index = parser->index;
  if (index < parser->length && parser->bytes[index] == (uint8_t)'-')
    ++index;
  if (index >= parser->length)
    return 0;
  if (parser->bytes[index] == (uint8_t)'0') {
    ++index;
    if (index < parser->length && parser->bytes[index] >= (uint8_t)'0' &&
        parser->bytes[index] <= (uint8_t)'9')
      return 0;
  } else if (parser->bytes[index] >= (uint8_t)'1' &&
             parser->bytes[index] <= (uint8_t)'9') {
    do {
      ++index;
    } while (index < parser->length &&
             parser->bytes[index] >= (uint8_t)'0' &&
             parser->bytes[index] <= (uint8_t)'9');
  } else {
    return 0;
  }
  if (index < parser->length && parser->bytes[index] == (uint8_t)'.') {
    ++index;
    if (index >= parser->length || parser->bytes[index] < (uint8_t)'0' ||
        parser->bytes[index] > (uint8_t)'9')
      return 0;
    do {
      ++index;
    } while (index < parser->length &&
             parser->bytes[index] >= (uint8_t)'0' &&
             parser->bytes[index] <= (uint8_t)'9');
  }
  if (index < parser->length &&
      (parser->bytes[index] == (uint8_t)'e' ||
       parser->bytes[index] == (uint8_t)'E')) {
    ++index;
    if (index < parser->length &&
        (parser->bytes[index] == (uint8_t)'+' ||
         parser->bytes[index] == (uint8_t)'-'))
      ++index;
    if (index >= parser->length || parser->bytes[index] < (uint8_t)'0' ||
        parser->bytes[index] > (uint8_t)'9')
      return 0;
    do {
      ++index;
    } while (index < parser->length &&
             parser->bytes[index] >= (uint8_t)'0' &&
             parser->bytes[index] <= (uint8_t)'9');
  }
  parser->index = index;
  return 1;
}

static int32_t al_campfire_json_parse_value(al_campfire_json_parser *parser,
                                             uint32_t depth);

static int32_t al_campfire_json_parse_object(al_campfire_json_parser *parser,
                                              uint32_t depth) {
  if (depth >= AL_CAMPFIRE_JSON_MAX_DEPTH ||
      parser->bytes[parser->index] != (uint8_t)'{')
    return 0;
  ++parser->index;
  al_campfire_json_skip_whitespace(parser);
  if (parser->index < parser->length &&
      parser->bytes[parser->index] == (uint8_t)'}') {
    ++parser->index;
    return 1;
  }
  while (parser->index < parser->length) {
    if (!al_campfire_json_parse_string(parser, NULL, 0u, NULL, NULL, 0u,
                                       NULL, NULL, NULL, 0u, NULL))
      return 0;
    al_campfire_json_skip_whitespace(parser);
    if (parser->index >= parser->length ||
        parser->bytes[parser->index] != (uint8_t)':')
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
    if (!al_campfire_json_parse_value(parser, depth + 1u))
      return 0;
    al_campfire_json_skip_whitespace(parser);
    if (parser->index < parser->length &&
        parser->bytes[parser->index] == (uint8_t)'}') {
      ++parser->index;
      return 1;
    }
    if (parser->index >= parser->length ||
        parser->bytes[parser->index] != (uint8_t)',')
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
  }
  return 0;
}

static int32_t al_campfire_json_parse_array(al_campfire_json_parser *parser,
                                             uint32_t depth) {
  if (depth >= AL_CAMPFIRE_JSON_MAX_DEPTH ||
      parser->bytes[parser->index] != (uint8_t)'[')
    return 0;
  ++parser->index;
  al_campfire_json_skip_whitespace(parser);
  if (parser->index < parser->length &&
      parser->bytes[parser->index] == (uint8_t)']') {
    ++parser->index;
    return 1;
  }
  while (parser->index < parser->length) {
    if (!al_campfire_json_parse_value(parser, depth + 1u))
      return 0;
    al_campfire_json_skip_whitespace(parser);
    if (parser->index < parser->length &&
        parser->bytes[parser->index] == (uint8_t)']') {
      ++parser->index;
      return 1;
    }
    if (parser->index >= parser->length ||
        parser->bytes[parser->index] != (uint8_t)',')
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
  }
  return 0;
}

static int32_t al_campfire_json_parse_value(al_campfire_json_parser *parser,
                                             uint32_t depth) {
  if (parser == NULL || parser->index >= parser->length)
    return 0;
  switch (parser->bytes[parser->index]) {
  case (uint8_t)'"':
    return al_campfire_json_parse_string(parser, NULL, 0u, NULL, NULL, 0u,
                                         NULL, NULL, NULL, 0u, NULL);
  case (uint8_t)'{':
    return al_campfire_json_parse_object(parser, depth);
  case (uint8_t)'[':
    return al_campfire_json_parse_array(parser, depth);
  case (uint8_t)'t':
    if (parser->length - parser->index < 4u ||
        parser->bytes[parser->index + 1u] != (uint8_t)'r' ||
        parser->bytes[parser->index + 2u] != (uint8_t)'u' ||
        parser->bytes[parser->index + 3u] != (uint8_t)'e')
      return 0;
    parser->index += 4u;
    return 1;
  case (uint8_t)'f':
    if (parser->length - parser->index < 5u ||
        parser->bytes[parser->index + 1u] != (uint8_t)'a' ||
        parser->bytes[parser->index + 2u] != (uint8_t)'l' ||
        parser->bytes[parser->index + 3u] != (uint8_t)'s' ||
        parser->bytes[parser->index + 4u] != (uint8_t)'e')
      return 0;
    parser->index += 5u;
    return 1;
  case (uint8_t)'n':
    if (parser->length - parser->index < 4u ||
        parser->bytes[parser->index + 1u] != (uint8_t)'u' ||
        parser->bytes[parser->index + 2u] != (uint8_t)'l' ||
        parser->bytes[parser->index + 3u] != (uint8_t)'l')
      return 0;
    parser->index += 4u;
    return 1;
  default:
    return al_campfire_json_parse_number(parser);
  }
}

static int32_t al_campfire_json_parse_null(al_campfire_json_parser *parser) {
  if (parser->length - parser->index < 4u ||
      parser->bytes[parser->index] != (uint8_t)'n' ||
      parser->bytes[parser->index + 1u] != (uint8_t)'u' ||
      parser->bytes[parser->index + 2u] != (uint8_t)'l' ||
      parser->bytes[parser->index + 3u] != (uint8_t)'l')
    return 0;
  parser->index += 4u;
  return 1;
}

static int32_t al_campfire_is_ascii_digit(uint8_t value) {
  return value >= (uint8_t)'0' && value <= (uint8_t)'9';
}

static uint32_t al_campfire_decimal2(const uint8_t *bytes) {
  return (uint32_t)(bytes[0] - (uint8_t)'0') * 10u +
         (uint32_t)(bytes[1] - (uint8_t)'0');
}

static uint32_t al_campfire_decimal4(const uint8_t *bytes) {
  return (uint32_t)(bytes[0] - (uint8_t)'0') * 1000u +
         (uint32_t)(bytes[1] - (uint8_t)'0') * 100u +
         (uint32_t)(bytes[2] - (uint8_t)'0') * 10u +
         (uint32_t)(bytes[3] - (uint8_t)'0');
}

static int32_t al_campfire_is_leap_year(uint32_t year) {
  return year % 4u == 0u && (year % 100u != 0u || year % 400u == 0u);
}

static uint32_t al_campfire_days_before_year(uint32_t year) {
  const uint64_t prior = (uint64_t)year - 1u;
  return (uint32_t)(prior * 365u + prior / 4u - prior / 100u + prior / 400u);
}

static int32_t al_campfire_parse_rails_expiry(
    const uint8_t *text, uint32_t length, int64_t *out_utc_ticks) {
  static const uint8_t days_in_month[12] = {31u, 28u, 31u, 30u, 31u, 30u,
                                            31u, 31u, 30u, 31u, 30u, 31u};
  uint32_t zone_index = UINT32_MAX;
  uint32_t date_time_length;
  uint32_t fractional_digits = 0u;
  uint32_t fractional_ticks = 0u;
  uint32_t year;
  uint32_t month;
  uint32_t day;
  uint32_t hour;
  uint32_t minute;
  uint32_t second;
  uint32_t days = 0u;
  uint32_t month_index;
  int32_t offset_sign = 0;
  uint32_t offset_minutes = 0u;
  uint64_t local_ticks;
  int64_t utc_ticks;
  uint32_t index;
  if (text == NULL || out_utc_ticks == NULL || length < 20u)
    return 0;
  if (text[length - 1u] == (uint8_t)'Z' ||
      text[length - 1u] == (uint8_t)'z') {
    zone_index = length - 1u;
    date_time_length = zone_index;
  } else {
    uint32_t last_plus = UINT32_MAX;
    uint32_t last_minus = UINT32_MAX;
    for (index = 0u; index < length; ++index) {
      if (text[index] == (uint8_t)'+')
        last_plus = index;
      else if (text[index] == (uint8_t)'-')
        last_minus = index;
    }
    if (last_plus == UINT32_MAX && last_minus == UINT32_MAX)
      return 0;
    if (last_plus == UINT32_MAX)
      zone_index = last_minus;
    else if (last_minus == UINT32_MAX)
      zone_index = last_plus;
    else
      zone_index = last_plus > last_minus ? last_plus : last_minus;
    if (zone_index < 19u || length - zone_index != 6u ||
        text[zone_index + 3u] != (uint8_t)':')
      return 0;
    offset_sign = text[zone_index] == (uint8_t)'+' ? 1 : -1;
    if (!al_campfire_is_ascii_digit(text[zone_index + 1u]) ||
        !al_campfire_is_ascii_digit(text[zone_index + 2u]) ||
        !al_campfire_is_ascii_digit(text[zone_index + 4u]) ||
        !al_campfire_is_ascii_digit(text[zone_index + 5u]))
      return 0;
    offset_minutes = al_campfire_decimal2(text + zone_index + 1u) * 60u +
                     al_campfire_decimal2(text + zone_index + 4u);
    if (al_campfire_decimal2(text + zone_index + 4u) > 59u ||
        al_campfire_decimal2(text + zone_index + 1u) > 14u ||
        (al_campfire_decimal2(text + zone_index + 1u) == 14u &&
         al_campfire_decimal2(text + zone_index + 4u) != 0u))
      return 0;
    date_time_length = zone_index;
  }
  if (zone_index == UINT32_MAX || date_time_length < 19u ||
      text[4] != (uint8_t)'-' || text[7] != (uint8_t)'-' ||
      (text[10] != (uint8_t)'T' && text[10] != (uint8_t)'t') ||
      text[13] != (uint8_t)':' || text[16] != (uint8_t)':')
    return 0;
  for (index = 0u; index < 4u; ++index) {
    if (!al_campfire_is_ascii_digit(text[index]))
      return 0;
  }
  if (!al_campfire_is_ascii_digit(text[5]) ||
      !al_campfire_is_ascii_digit(text[6]) ||
      !al_campfire_is_ascii_digit(text[8]) ||
      !al_campfire_is_ascii_digit(text[9]) ||
      !al_campfire_is_ascii_digit(text[11]) ||
      !al_campfire_is_ascii_digit(text[12]) ||
      !al_campfire_is_ascii_digit(text[14]) ||
      !al_campfire_is_ascii_digit(text[15]) ||
      !al_campfire_is_ascii_digit(text[17]) ||
      !al_campfire_is_ascii_digit(text[18]))
    return 0;
  if (date_time_length == 19u) {
    fractional_digits = 0u;
  } else {
    if (text[19] != (uint8_t)'.' || date_time_length < 21u ||
        date_time_length > 27u)
      return 0;
    fractional_digits = date_time_length - 20u;
    for (index = 0u; index < fractional_digits; ++index) {
      if (!al_campfire_is_ascii_digit(text[20u + index]))
        return 0;
      fractional_ticks = fractional_ticks * 10u +
                         (uint32_t)(text[20u + index] - (uint8_t)'0');
    }
    for (; fractional_digits < 7u; ++fractional_digits)
      fractional_ticks *= 10u;
  }
  year = al_campfire_decimal4(text);
  month = al_campfire_decimal2(text + 5u);
  day = al_campfire_decimal2(text + 8u);
  hour = al_campfire_decimal2(text + 11u);
  minute = al_campfire_decimal2(text + 14u);
  second = al_campfire_decimal2(text + 17u);
  if (year == 0u || month == 0u || month > 12u || hour > 23u ||
      minute > 59u || second > 59u)
    return 0;
  for (month_index = 1u; month_index < month; ++month_index) {
    uint32_t month_days = days_in_month[month_index - 1u];
    if (month_index == 2u && al_campfire_is_leap_year(year))
      ++month_days;
    days += month_days;
  }
  if (month == 2u && al_campfire_is_leap_year(year)) {
    if (day > 29u)
      return 0;
  } else if (day > days_in_month[month - 1u]) {
    return 0;
  }
  if (day == 0u)
    return 0;
  days += day - 1u;
  days += al_campfire_days_before_year(year);
  local_ticks = (uint64_t)days * UINT64_C(864000000000) +
                ((uint64_t)hour * 3600u + (uint64_t)minute * 60u + second) *
                    UINT64_C(10000000) +
                fractional_ticks;
  utc_ticks = (int64_t)local_ticks -
              (int64_t)offset_sign * (int64_t)offset_minutes *
                  INT64_C(600000000);
  if (utc_ticks < 0 || utc_ticks > INT64_C(3155378975999999999))
    return 0;
  *out_utc_ticks = utc_ticks;
  return 1;
}

static void al_campfire_rails_fields_init(al_campfire_rails_fields *fields) {
  SecureZeroMemory(fields, sizeof(*fields));
  fields->expiry_valid = 1u;
  /* CookieService retries a failed purpose-specific parse with no expected
   * purpose, where absent, null, and empty string all coerce to "". */
  fields->purpose_matches = 1u;
}

static int32_t al_campfire_json_parse_rails_object(
    al_campfire_json_parser *parser, al_campfire_pure_context *context,
    al_campfire_rails_fields *fields) {
  static const uint8_t rails_message_key[] = "message";
  static const uint8_t rails_purpose_key[] = "pur";
  static const uint8_t rails_expiry_key[] = "exp";
  static const uint8_t expected_purpose[] = "cookie.session_token";
  if (parser->index >= parser->length ||
      parser->bytes[parser->index] != (uint8_t)'{') {
    fields->expiry_valid = 0u;
    return al_campfire_json_parse_value(parser, 1u);
  }
  al_campfire_rails_fields_init(fields);
  ++parser->index;
  al_campfire_json_skip_whitespace(parser);
  if (parser->index < parser->length &&
      parser->bytes[parser->index] == (uint8_t)'}') {
    ++parser->index;
    return 1;
  }
  while (parser->index < parser->length) {
    uint32_t key_length = 0u;
    int32_t key_is_ascii = 0;
    int32_t message_key_matches;
    int32_t purpose_key_matches;
    int32_t expiry_key_matches;
    if (!al_campfire_json_parse_string(
            parser, NULL, 0u, NULL, context->raw_ascii,
            AL_CAMPFIRE_SCRATCH_BYTES, &key_length, &key_is_ascii, NULL, 0u,
            NULL))
      return 0;
    message_key_matches =
        key_is_ascii && key_length == sizeof(rails_message_key) - 1u &&
        memcmp(context->raw_ascii, rails_message_key,
               sizeof(rails_message_key) - 1u) == 0;
    purpose_key_matches =
        key_is_ascii && key_length == sizeof(rails_purpose_key) - 1u &&
        memcmp(context->raw_ascii, rails_purpose_key,
               sizeof(rails_purpose_key) - 1u) == 0;
    expiry_key_matches =
        key_is_ascii && key_length == sizeof(rails_expiry_key) - 1u &&
        memcmp(context->raw_ascii, rails_expiry_key,
               sizeof(rails_expiry_key) - 1u) == 0;
    al_campfire_json_skip_whitespace(parser);
    if (parser->index >= parser->length ||
        parser->bytes[parser->index] != (uint8_t)':')
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
    if (message_key_matches) {
      fields->message_is_string = 0u;
      fields->message_is_ascii = 0u;
      fields->message_length = 0u;
      if (parser->index < parser->length &&
          parser->bytes[parser->index] == (uint8_t)'"') {
        int32_t is_ascii = 0;
        fields->message_is_string = 1u;
        if (!al_campfire_json_parse_string(
                parser, NULL, 0u, NULL, context->message,
                AL_CAMPFIRE_SCRATCH_BYTES, &fields->message_length,
                &is_ascii, NULL, 0u, NULL))
          return 0;
        fields->message_is_ascii = (uint8_t)(is_ascii != 0);
      } else if (!al_campfire_json_parse_value(parser, 2u)) {
        return 0;
      }
    } else if (purpose_key_matches) {
      fields->purpose_matches = 0u;
      if (parser->index < parser->length &&
          parser->bytes[parser->index] == (uint8_t)'"') {
        uint32_t purpose_length = 0u;
        int32_t purpose_is_ascii = 0;
        if (!al_campfire_json_parse_string(
                parser, NULL, 0u, NULL, context->raw_ascii,
                AL_CAMPFIRE_SCRATCH_BYTES, &purpose_length,
                &purpose_is_ascii, NULL, 0u, NULL))
          return 0;
        fields->purpose_matches =
            (uint8_t)(purpose_is_ascii &&
                      (purpose_length == 0u ||
                       (purpose_length == sizeof(expected_purpose) - 1u &&
                        memcmp(context->raw_ascii, expected_purpose,
                               sizeof(expected_purpose) - 1u) == 0)));
      } else if (al_campfire_json_parse_null(parser)) {
        fields->purpose_matches = 1u;
      } else {
        if (!al_campfire_json_parse_value(parser, 2u))
          return 0;
        fields->purpose_matches = 0u;
      }
    } else if (expiry_key_matches) {
      fields->expiry_present = 1u;
      fields->expiry_valid = 0u;
      if (al_campfire_json_parse_null(parser)) {
        fields->expiry_present = 0u;
        fields->expiry_valid = 1u;
      } else if (parser->index < parser->length &&
                 parser->bytes[parser->index] == (uint8_t)'"') {
        uint32_t expiry_length = 0u;
        int32_t expiry_is_ascii = 0;
        if (!al_campfire_json_parse_string(
                parser, NULL, 0u, NULL, context->raw_ascii,
                AL_CAMPFIRE_SCRATCH_BYTES, &expiry_length,
                &expiry_is_ascii, NULL, 0u, NULL))
          return 0;
        if (expiry_is_ascii &&
            al_campfire_parse_rails_expiry(context->raw_ascii, expiry_length,
                                           &fields->expiry_utc_ticks))
          fields->expiry_valid = 1u;
      } else if (!al_campfire_json_parse_value(parser, 2u)) {
        return 0;
      }
    } else if (!al_campfire_json_parse_value(parser, 2u)) {
      return 0;
    }
    al_campfire_json_skip_whitespace(parser);
    if (parser->index < parser->length &&
        parser->bytes[parser->index] == (uint8_t)'}') {
      ++parser->index;
      return 1;
    }
    if (parser->index >= parser->length ||
        parser->bytes[parser->index] != (uint8_t)',')
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
  }
  return 0;
}

static int32_t al_campfire_json_parse_rails_envelope(
    const uint8_t *bytes, uint32_t length,
    al_campfire_pure_context *context,
    al_campfire_rails_fields *out_fields) {
  static const uint8_t rails_key[] = "_rails";
  al_campfire_json_parser parser;
  al_campfire_rails_fields last_rails;
  uint8_t rails_seen = 0u;
  if (bytes == NULL || context == NULL || out_fields == NULL ||
      length == 0u || length > 16384u)
    return 0;
  parser.bytes = bytes;
  parser.length = length;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'{')
    return 0;
  ++parser.index;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index < parser.length &&
      parser.bytes[parser.index] == (uint8_t)'}')
    return 0;
  while (parser.index < parser.length) {
    uint32_t key_length = 0u;
    int32_t key_is_ascii = 0;
    int32_t is_rails = 0;
    if (!al_campfire_json_parse_string(
            &parser, NULL, 0u, NULL, context->raw_ascii,
            AL_CAMPFIRE_SCRATCH_BYTES, &key_length, &key_is_ascii, NULL, 0u,
            NULL))
      return 0;
    is_rails = key_is_ascii && key_length == sizeof(rails_key) - 1u &&
               memcmp(context->raw_ascii, rails_key,
                      sizeof(rails_key) - 1u) == 0;
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)':')
      return 0;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
    if (is_rails) {
      al_campfire_rails_fields current;
      al_campfire_rails_fields_init(&current);
      if (!al_campfire_json_parse_rails_object(&parser, context, &current))
        return 0;
      last_rails = current;
      rails_seen = 1u;
    } else if (!al_campfire_json_parse_value(&parser, 1u)) {
      return 0;
    }
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index < parser.length &&
        parser.bytes[parser.index] == (uint8_t)'}') {
      ++parser.index;
      al_campfire_json_skip_whitespace(&parser);
      if (parser.index != parser.length || !rails_seen)
        return 0;
      *out_fields = last_rails;
      return 1;
    }
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)',')
      return 0;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
  }
  return 0;
}

static int32_t al_campfire_json_root_string(
    const uint8_t *bytes, uint32_t length, uint16_t *out_token,
    uint32_t token_capacity, uint32_t *out_token_units) {
  al_campfire_json_parser parser;
  int32_t is_string = 0;
  if (bytes == NULL || out_token == NULL || out_token_units == NULL ||
      length == 0u || length > 16384u)
    return 0;
  parser.bytes = bytes;
  parser.length = length;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length)
    return 0;
  if (parser.bytes[parser.index] == (uint8_t)'"') {
    is_string = 1;
    if (!al_campfire_json_parse_string(
            &parser, out_token, token_capacity, out_token_units, NULL, 0u,
            NULL, NULL, NULL, 0u, NULL))
      return 0;
  } else if (!al_campfire_json_parse_value(&parser, 0u)) {
    return 0;
  }
  al_campfire_json_skip_whitespace(&parser);
  return is_string && parser.index == parser.length;
}

/* Returns -1 for a CNG boundary failure, 0 for a bad digest, and 1 for a
 * constant-time digest match. */
static int32_t al_campfire_verify_digest(
    al_campfire_pure_context *context, const uint8_t *encoded,
    uint32_t encoded_length, const uint8_t *actual_hex) {
  static const uint8_t hex[] = "0123456789abcdef";
  BCRYPT_HASH_HANDLE hash = NULL;
  uint8_t digest[AL_CAMPFIRE_SHA1_BYTES];
  uint8_t expected_hex[AL_CAMPFIRE_HEX_SHA1_BYTES];
  uint32_t index;
  uint32_t difference = 0u;
  NTSTATUS status;
  SecureZeroMemory(digest, sizeof(digest));
  SecureZeroMemory(expected_hex, sizeof(expected_hex));
  status = BCryptCreateHash(context->hmac_sha1_algorithm, &hash,
                            context->hmac_object, context->hmac_object_bytes,
                            context->signing_key,
                            AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto crypto_failure;
  status = BCryptHashData(hash, (PUCHAR)encoded, encoded_length, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto crypto_failure;
  status = BCryptFinishHash(hash, digest, sizeof(digest), 0u);
  if (!BCRYPT_SUCCESS(status))
    goto crypto_failure;
  for (index = 0u; index < AL_CAMPFIRE_SHA1_BYTES; ++index) {
    expected_hex[index * 2u] = hex[digest[index] >> 4u];
    expected_hex[index * 2u + 1u] = hex[digest[index] & 0x0fu];
  }
  for (index = 0u; index < AL_CAMPFIRE_HEX_SHA1_BYTES; ++index)
    difference |= (uint32_t)(expected_hex[index] ^ actual_hex[index]);
  BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac_object, context->hmac_object_bytes);
  SecureZeroMemory(digest, sizeof(digest));
  SecureZeroMemory(expected_hex, sizeof(expected_hex));
  return difference == 0u ? 1 : 0;

crypto_failure:
  if (hash != NULL)
    BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac_object, context->hmac_object_bytes);
  SecureZeroMemory(digest, sizeof(digest));
  SecureZeroMemory(expected_hex, sizeof(expected_hex));
  return -1;
}

static int32_t al_campfire_parse_verified_cookie(
    al_campfire_pure_context *context,
    const al_campfire_string_view *raw_view, int64_t now_microseconds,
    uint32_t *out_token_units) {
  static const uint8_t envelope_prefix[] =
      "{\"_rails\":{\"message\":\"";
  uint32_t raw_utf8_bytes = 0u;
  uint32_t raw_units;
  uint32_t encoded_length;
  uint32_t payload_length = 0u;
  uint32_t inner_length = 0u;
  uint32_t index;
  int32_t digest_result;
  int64_t now_ticks;
  if (context == NULL || raw_view == NULL || out_token_units == NULL ||
      !al_campfire_utf8_byte_count(
          raw_view, AL_CAMPFIRE_COOKIE_MAX_BYTES -
                        AL_CAMPFIRE_COOKIE_NAME_BYTES,
          &raw_utf8_bytes) ||
      raw_utf8_bytes + AL_CAMPFIRE_COOKIE_NAME_BYTES >
          AL_CAMPFIRE_COOKIE_MAX_BYTES ||
      !al_campfire_string_is_ascii(raw_view))
    return 0;
  raw_units = raw_view->code_units;
  if (raw_units < 42u || raw_units >= AL_CAMPFIRE_SCRATCH_BYTES)
    return 0;
  for (index = 0u; index < raw_units; ++index)
    context->raw_ascii[index] = (uint8_t)al_campfire_string_unit(raw_view, index);
  encoded_length = raw_units - 42u;
  if (encoded_length == 0u ||
      context->raw_ascii[encoded_length] != (uint8_t)'-' ||
      context->raw_ascii[encoded_length + 1u] != (uint8_t)'-')
    return 0;
  for (index = 0u; index < AL_CAMPFIRE_HEX_SHA1_BYTES; ++index) {
    const uint8_t digit = context->raw_ascii[encoded_length + 2u + index];
    if (!((digit >= (uint8_t)'0' && digit <= (uint8_t)'9') ||
          (digit >= (uint8_t)'a' && digit <= (uint8_t)'f')))
      return 0;
  }
  digest_result = al_campfire_verify_digest(
      context, context->raw_ascii, encoded_length,
      context->raw_ascii + encoded_length + 2u);
  if (digest_result <= 0)
    return digest_result;
  if (!al_campfire_decode_base64_urlsafe(
          context->raw_ascii, encoded_length, context->payload,
          sizeof(context->payload), &payload_length))
    return 0;

  if (payload_length >= sizeof(envelope_prefix) - 1u &&
      memcmp(context->payload, envelope_prefix,
             sizeof(envelope_prefix) - 1u) == 0) {
    al_campfire_rails_fields fields;
    int64_t expiry_utc_ticks;
    if (!al_campfire_json_parse_rails_envelope(
            context->payload, payload_length, context, &fields) ||
        !fields.message_is_string || !fields.message_is_ascii ||
        !fields.purpose_matches || !fields.expiry_valid)
      return 0;
    if (fields.expiry_present) {
      expiry_utc_ticks = fields.expiry_utc_ticks;
      now_ticks = INT64_C(621355968000000000) +
                  now_microseconds * INT64_C(10);
      if (now_ticks >= expiry_utc_ticks)
        return 0;
    }
    if (!al_campfire_decode_base64_urlsafe(
            context->message, fields.message_length, context->inner_json,
            sizeof(context->inner_json), &inner_length))
      return 0;
    if (!al_campfire_json_root_string(
            context->inner_json, inner_length, context->token,
            AL_CAMPFIRE_TOKEN_UNITS, out_token_units))
      return 0;
    return 1;
  }

  if (!al_campfire_json_root_string(context->payload, payload_length,
                                    context->token,
                                    AL_CAMPFIRE_TOKEN_UNITS,
                                    out_token_units))
    return 0;
  return 1;
}

static int32_t al_campfire_cookie_output_valid(
    const al_owning_layout *layout, uint32_t output_type_index) {
  const al_owning_type_descriptor *option;
  const al_owning_field_descriptor *some_field;
  const al_owning_field_descriptor *none_field;
  if (!al_campfire_layout_valid(layout) ||
      output_type_index >= layout->type_count)
    return 0;
  option = &layout->types[output_type_index];
  if (option->kind != AL_OWNING_TYPE_OPTION || option->case_count != 2u ||
      option->field_count != 2u ||
      option->first_field > layout->field_count ||
      option->field_count > layout->field_count - option->first_field)
    return 0;
  some_field = &layout->fields[option->first_field];
  none_field = &layout->fields[option->first_field + 1u];
  if (some_field->child_type_index >= layout->type_count ||
      layout->types[some_field->child_type_index].kind != AL_OWNING_TYPE_STRING ||
      some_field->fixed_offset_bytes != 8u || some_field->flags != 0u ||
      some_field->reserved != 0u ||
      none_field->child_type_index != AL_OWNING_LAYOUT_DYNAMIC_U32 ||
      none_field->fixed_offset_bytes != 8u || none_field->flags != 0u ||
      none_field->reserved != 0u)
    return 0;
  return 1;
}

static int32_t al_campfire_write_cookie_none(
    uint8_t *output, uint32_t output_capacity_bytes,
    uint32_t *output_extent_bytes) {
  if (output == NULL || output_extent_bytes == NULL ||
      output_capacity_bytes < 8u)
    return 1;
  /* The compiled ABI5 Option order is Some=0, None=1. */
  al_campfire_write_u64_le(output, 1u);
  *output_extent_bytes = 8u;
  return 0;
}

static int32_t al_campfire_write_cookie_some(
    const al_owning_layout *layout, uint32_t output_type_index,
    const uint16_t *token, uint32_t token_units, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  uint64_t utf8_bytes = 0u;
  uint64_t string_payload;
  uint64_t string_extent;
  uint64_t total_extent;
  uint32_t index;
  uint32_t cursor;
  if (output == NULL || token == NULL || output_extent_bytes == NULL ||
      !al_campfire_cookie_output_valid(layout, output_type_index))
    return 1;
  /* Measure the UTF-8 form; unpaired surrogates have none and are rejected. */
  for (index = 0u; index < token_units; ++index) {
    const uint32_t unit = token[index];
    if (unit < 0x80u)
      utf8_bytes += 1u;
    else if (unit < 0x800u)
      utf8_bytes += 2u;
    else if (unit >= 0xd800u && unit <= 0xdbffu && index + 1u < token_units &&
             token[index + 1u] >= 0xdc00u && token[index + 1u] <= 0xdfffu) {
      utf8_bytes += 4u;
      ++index;
    } else if (unit >= 0xd800u && unit <= 0xdfffu)
      return 1;
    else
      utf8_bytes += 3u;
  }
  string_payload = 8u + utf8_bytes;
  string_extent = (string_payload + 7u) & ~UINT64_C(7);
  total_extent = 8u + string_extent;
  if (total_extent > UINT32_MAX || total_extent > output_capacity_bytes)
    return 1;
  SecureZeroMemory(output, (size_t)total_extent);
  al_campfire_write_u64_le(output, 0u);
  al_campfire_write_u32_le(output + 8u, (uint32_t)utf8_bytes);
  cursor = 16u;
  for (index = 0u; index < token_units; ++index) {
    uint32_t scalar = token[index];
    if (scalar >= 0xd800u && scalar <= 0xdbffu) {
      scalar = 0x10000u + ((scalar - 0xd800u) << 10) +
               ((uint32_t)token[index + 1u] - 0xdc00u);
      ++index;
    }
    if (scalar < 0x80u) {
      output[cursor++] = (uint8_t)scalar;
    } else if (scalar < 0x800u) {
      output[cursor++] = (uint8_t)(0xc0u | (scalar >> 6));
      output[cursor++] = (uint8_t)(0x80u | (scalar & 0x3fu));
    } else if (scalar < 0x10000u) {
      output[cursor++] = (uint8_t)(0xe0u | (scalar >> 12));
      output[cursor++] = (uint8_t)(0x80u | ((scalar >> 6) & 0x3fu));
      output[cursor++] = (uint8_t)(0x80u | (scalar & 0x3fu));
    } else {
      output[cursor++] = (uint8_t)(0xf0u | (scalar >> 18));
      output[cursor++] = (uint8_t)(0x80u | ((scalar >> 12) & 0x3fu));
      output[cursor++] = (uint8_t)(0x80u | ((scalar >> 6) & 0x3fu));
      output[cursor++] = (uint8_t)(0x80u | (scalar & 0x3fu));
    }
  }
  *output_extent_bytes = (uint32_t)total_extent;
  return 0;
}

uint32_t al_campfire_pure_context_size_v1(void) {
  return (uint32_t)sizeof(al_campfire_pure_context);
}

uint32_t al_campfire_pure_context_alignment_v1(void) {
  return (uint32_t)__alignof(al_campfire_pure_context);
}

int32_t al_campfire_pure_context_init_v1(void *storage,
                                        uint32_t storage_bytes,
                                        const uint8_t *secret_utf8,
                                        uint32_t secret_length) {
  static const uint8_t signed_cookie_salt[] = "signed cookie";
  static const uint8_t signed_id_salt[] = "active_record/signed_id";
  al_campfire_pure_context *context;
  BCRYPT_ALG_HANDLE sha256_algorithm = NULL;
  ULONG result_bytes = 0u;
  ULONG object_bytes = 0u;
  NTSTATUS status;
  if (storage == NULL || storage_bytes < sizeof(al_campfire_pure_context) ||
      ((uintptr_t)storage % __alignof(al_campfire_pure_context)) != 0u ||
      secret_utf8 == NULL || secret_length < 30u)
    return 1;
  context = (al_campfire_pure_context *)storage;
  SecureZeroMemory(context, sizeof(*context));
  InitializeSRWLock(&context->lock);
  status = BCryptOpenAlgorithmProvider(&sha256_algorithm,
                                       BCRYPT_SHA256_ALGORITHM, NULL,
                                       BCRYPT_ALG_HANDLE_HMAC_FLAG);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptDeriveKeyPBKDF2(
      sha256_algorithm, (PUCHAR)secret_utf8, secret_length,
      (PUCHAR)signed_cookie_salt,
      (ULONG)(sizeof(signed_cookie_salt) - 1u), 1000u,
      context->signing_key, AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptDeriveKeyPBKDF2(
      sha256_algorithm, (PUCHAR)secret_utf8, secret_length,
      (PUCHAR)signed_id_salt, (ULONG)(sizeof(signed_id_salt) - 1u), 1000u,
      context->avatar_key, AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptGetProperty(sha256_algorithm, BCRYPT_OBJECT_LENGTH,
                             (PUCHAR)&object_bytes, sizeof(object_bytes),
                             &result_bytes, 0u);
  if (!BCRYPT_SUCCESS(status) || result_bytes != sizeof(object_bytes) ||
      object_bytes == 0u || object_bytes > sizeof(context->hmac256_object))
    goto initialization_failure;
  context->hmac256_object_bytes = object_bytes;
  context->hmac_sha256_algorithm = sha256_algorithm;
  sha256_algorithm = NULL;
  status = BCryptOpenAlgorithmProvider(
      &context->hmac_sha1_algorithm, BCRYPT_SHA1_ALGORITHM, NULL,
      BCRYPT_ALG_HANDLE_HMAC_FLAG);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptGetProperty(context->hmac_sha1_algorithm, BCRYPT_OBJECT_LENGTH,
                             (PUCHAR)&object_bytes, sizeof(object_bytes),
                             &result_bytes, 0u);
  if (!BCRYPT_SUCCESS(status) || result_bytes != sizeof(object_bytes) ||
      object_bytes == 0u || object_bytes > sizeof(context->hmac_object))
    goto initialization_failure;
  context->hmac_object_bytes = object_bytes;
  context->magic = AL_CAMPFIRE_PURE_CONTEXT_MAGIC;
  return 0;

initialization_failure:
  if (sha256_algorithm != NULL)
    BCryptCloseAlgorithmProvider(sha256_algorithm, 0u);
  if (context->hmac_sha256_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha256_algorithm, 0u);
    context->hmac_sha256_algorithm = NULL;
  }
  if (context->hmac_sha1_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha1_algorithm, 0u);
    context->hmac_sha1_algorithm = NULL;
  }
  SecureZeroMemory(context, sizeof(*context));
  return 1;
}

void al_campfire_pure_context_destroy_v1(void *pure_context) {
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)pure_context;
  if (context == NULL || context->magic != AL_CAMPFIRE_PURE_CONTEXT_MAGIC)
    return;
  AcquireSRWLockExclusive(&context->lock);
  context->magic = 0u;
  if (context->hmac_sha1_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha1_algorithm, 0u);
    context->hmac_sha1_algorithm = NULL;
  }
  if (context->hmac_sha256_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha256_algorithm, 0u);
    context->hmac_sha256_algorithm = NULL;
  }
  al_campfire_clear_scratch(context);
  SecureZeroMemory(context->signing_key, sizeof(context->signing_key));
  SecureZeroMemory(context->avatar_key, sizeof(context->avatar_key));
  ReleaseSRWLockExclusive(&context->lock);
  SecureZeroMemory(context, sizeof(*context));
}

int32_t al_campfire_verify_cookie_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)pure_context;
  al_campfire_string_view raw_cookie;
  int64_t now_microseconds = 0;
  uint32_t token_units = 0u;
  int32_t token_valid = 0;
  int32_t status = 1;
  if (output_extent_bytes != NULL)
    *output_extent_bytes = 0u;
  if (context == NULL || context->magic != AL_CAMPFIRE_PURE_CONTEXT_MAGIC ||
      !al_campfire_layout_valid(layout) || inputs == NULL || input_count != 2u ||
      output == NULL || output_extent_bytes == NULL ||
      !al_campfire_cookie_output_valid(layout, output_type_index) ||
      !al_campfire_read_input_string(layout, &inputs[0], &raw_cookie) ||
      !al_campfire_read_input_i64(layout, &inputs[1], &now_microseconds) ||
      now_microseconds < INT64_C(-62135596800000000) ||
      now_microseconds > INT64_C(253402300799999999))
    return 1;
  AcquireSRWLockExclusive(&context->lock);
  if (context->magic != AL_CAMPFIRE_PURE_CONTEXT_MAGIC)
    goto cleanup;
  al_campfire_clear_scratch(context);
  token_valid = al_campfire_parse_verified_cookie(
      context, &raw_cookie, now_microseconds, &token_units);
  if (token_valid < 0)
    goto cleanup;
  if (token_valid != 0) {
    status = al_campfire_write_cookie_some(
        layout, output_type_index, context->token, token_units, output,
        output_capacity_bytes, output_extent_bytes);
  } else {
    status = al_campfire_write_cookie_none(
        output, output_capacity_bytes, output_extent_bytes);
  }

cleanup:
  al_campfire_clear_scratch(context);
  ReleaseSRWLockExclusive(&context->lock);
  return status;
}

static int32_t al_campfire_is_ascii_digit_unit(
    const al_campfire_string_view *view, uint32_t index) {
  const uint16_t value = al_campfire_string_unit(view, index);
  return value >= (uint16_t)'0' && value <= (uint16_t)'9';
}

static int32_t al_campfire_is_ascii_letter_unit(
    const al_campfire_string_view *view, uint32_t index) {
  const uint16_t value = al_campfire_string_unit(view, index);
  return (value >= (uint16_t)'A' && value <= (uint16_t)'Z') ||
         (value >= (uint16_t)'a' && value <= (uint16_t)'z');
}

static int32_t al_campfire_is_ruby_space(uint16_t value) {
  return value == (uint16_t)' ' || value == (uint16_t)'\t' ||
         value == (uint16_t)'\n' || value == 0x000bu ||
         value == 0x000cu || value == (uint16_t)'\r';
}

static int32_t al_campfire_string_is_blank(
    const al_campfire_string_view *view) {
  uint32_t index;
  for (index = 0u; index < view->code_units; ++index) {
    if (!al_campfire_is_ruby_space(al_campfire_string_unit(view, index)))
      return 0;
  }
  return 1;
}

static uint32_t al_campfire_initial_digit_count(
    const al_campfire_string_view *view) {
  uint32_t index = 0u;
  while (index < view->code_units &&
         al_campfire_is_ascii_digit_unit(view, index))
    ++index;
  return index;
}

static int32_t al_campfire_version_is_comparable(
    const al_campfire_string_view *view) {
  const uint32_t digits = al_campfire_initial_digit_count(view);
  return !al_campfire_string_is_blank(view) && digits > 0u &&
         (digits == view->code_units ||
          al_campfire_string_unit(view, digits) == (uint16_t)'.');
}

static int32_t al_campfire_next_version_segment(
    const al_campfire_string_view *view, uint32_t *cursor,
    al_campfire_version_segment *out_segment) {
  uint32_t index = *cursor;
  while (index < view->code_units) {
    if (al_campfire_is_ascii_digit_unit(view, index)) {
      const uint32_t start = index;
      uint32_t normalized_start;
      while (index < view->code_units &&
             al_campfire_is_ascii_digit_unit(view, index))
        ++index;
      normalized_start = start;
      while (normalized_start < index &&
             al_campfire_string_unit(view, normalized_start) ==
                 (uint16_t)'0')
        ++normalized_start;
      out_segment->kind = AL_CAMPFIRE_SEGMENT_INTEGER;
      out_segment->owner = view;
      out_segment->is_zero_literal = normalized_start == index;
      out_segment->start_unit = out_segment->is_zero_literal ? index - 1u
                                                              : normalized_start;
      out_segment->length_units = out_segment->is_zero_literal
                                      ? 1u
                                      : index - normalized_start;
      *cursor = index;
      return 1;
    }
    if (al_campfire_is_ascii_letter_unit(view, index)) {
      const uint32_t start = index;
      uint32_t finish = index + 1u;
      while (finish < view->code_units &&
             (al_campfire_is_ascii_letter_unit(view, finish) ||
              al_campfire_is_ascii_digit_unit(view, finish) ||
              al_campfire_string_unit(view, finish) == (uint16_t)'-'))
        ++finish;
      if (finish == view->code_units) {
        out_segment->kind = AL_CAMPFIRE_SEGMENT_TEXT;
        out_segment->owner = view;
        out_segment->start_unit = start;
        out_segment->length_units = finish - start;
        out_segment->is_zero_literal = 0u;
        *cursor = finish;
        return 1;
      }
      index = start + 1u;
    } else {
      ++index;
    }
  }
  *cursor = view->code_units;
  return 0;
}

static int32_t al_campfire_get_segment(
    const al_campfire_string_view *view, int32_t comparable, int32_t blank,
    uint32_t *cursor, uint32_t index,
    al_campfire_version_segment *out_segment) {
  if (blank)
    return 0;
  if (!comparable) {
    if (index != 0u)
      return 0;
    out_segment->kind = AL_CAMPFIRE_SEGMENT_TEXT;
    out_segment->owner = view;
    out_segment->start_unit = 0u;
    out_segment->length_units = view->code_units;
    out_segment->is_zero_literal = 0u;
    return 1;
  }
  return al_campfire_next_version_segment(view, cursor, out_segment);
}

typedef struct al_campfire_utf8_iterator {
  const al_campfire_string_view *view;
  uint32_t index;
  uint32_t end;
  uint8_t pending[4];
  uint32_t pending_index;
  uint32_t pending_length;
} al_campfire_utf8_iterator;

static int32_t al_campfire_utf8_next(al_campfire_utf8_iterator *iterator,
                                     uint8_t *out_byte) {
  if (iterator->pending_index < iterator->pending_length) {
    *out_byte = iterator->pending[iterator->pending_index++];
    return 1;
  }
  if (iterator->index >= iterator->end)
    return 0;
  {
    uint32_t scalar;
    const uint16_t first =
        al_campfire_string_unit(iterator->view, iterator->index++);
    iterator->pending_index = 0u;
    if (first >= 0xd800u && first <= 0xdbffu &&
        iterator->index < iterator->end) {
      const uint16_t second =
          al_campfire_string_unit(iterator->view, iterator->index);
      if (second >= 0xdc00u && second <= 0xdfffu) {
        ++iterator->index;
        scalar = 0x10000u +
                 ((((uint32_t)first - 0xd800u) << 10u) |
                  ((uint32_t)second - 0xdc00u));
      } else {
        scalar = 0xfffdu;
      }
    } else if (first >= 0xd800u && first <= 0xdfffu) {
      scalar = 0xfffdu;
    } else {
      scalar = first;
    }
    if (scalar <= 0x7fu) {
      iterator->pending[0] = (uint8_t)scalar;
      iterator->pending_length = 1u;
    } else if (scalar <= 0x7ffu) {
      iterator->pending[0] = (uint8_t)(0xc0u | (scalar >> 6u));
      iterator->pending[1] = (uint8_t)(0x80u | (scalar & 0x3fu));
      iterator->pending_length = 2u;
    } else if (scalar <= 0xffffu) {
      iterator->pending[0] = (uint8_t)(0xe0u | (scalar >> 12u));
      iterator->pending[1] = (uint8_t)(0x80u | ((scalar >> 6u) & 0x3fu));
      iterator->pending[2] = (uint8_t)(0x80u | (scalar & 0x3fu));
      iterator->pending_length = 3u;
    } else {
      iterator->pending[0] = (uint8_t)(0xf0u | (scalar >> 18u));
      iterator->pending[1] = (uint8_t)(0x80u | ((scalar >> 12u) & 0x3fu));
      iterator->pending[2] = (uint8_t)(0x80u | ((scalar >> 6u) & 0x3fu));
      iterator->pending[3] = (uint8_t)(0x80u | (scalar & 0x3fu));
      iterator->pending_length = 4u;
    }
  }
  *out_byte = iterator->pending[iterator->pending_index++];
  return 1;
}

static int32_t al_campfire_compare_utf8_segments(
    const al_campfire_version_segment *left,
    const al_campfire_version_segment *right) {
  al_campfire_utf8_iterator left_iterator;
  al_campfire_utf8_iterator right_iterator;
  uint8_t left_byte;
  uint8_t right_byte;
  left_iterator.view = left->owner;
  left_iterator.index = left->start_unit;
  left_iterator.end = left->start_unit + left->length_units;
  left_iterator.pending_index = 0u;
  left_iterator.pending_length = 0u;
  right_iterator.view = right->owner;
  right_iterator.index = right->start_unit;
  right_iterator.end = right->start_unit + right->length_units;
  right_iterator.pending_index = 0u;
  right_iterator.pending_length = 0u;
  for (;;) {
    const int32_t has_left = al_campfire_utf8_next(&left_iterator, &left_byte);
    const int32_t has_right =
        al_campfire_utf8_next(&right_iterator, &right_byte);
    if (!has_left || !has_right) {
      if (has_left)
        return 1;
      if (has_right)
        return -1;
      return 0;
    }
    if (left_byte != right_byte)
      return left_byte < right_byte ? -1 : 1;
  }
}

static int32_t al_campfire_compare_segments(
    const al_campfire_version_segment *left,
    const al_campfire_version_segment *right) {
  if (left->kind == AL_CAMPFIRE_SEGMENT_INTEGER &&
      right->kind == AL_CAMPFIRE_SEGMENT_INTEGER) {
    uint32_t index;
    if (left->length_units != right->length_units)
      return left->length_units < right->length_units ? -1 : 1;
    for (index = 0u; index < left->length_units; ++index) {
      const uint16_t left_value = left->is_zero_literal
                                      ? (uint16_t)'0'
                                      : al_campfire_string_unit(
                                            left->owner,
                                            left->start_unit + index);
      const uint16_t right_value = right->is_zero_literal
                                       ? (uint16_t)'0'
                                       : al_campfire_string_unit(
                                             right->owner,
                                             right->start_unit + index);
      if (left_value != right_value)
        return left_value < right_value ? -1 : 1;
    }
    return 0;
  }
  if (left->kind == AL_CAMPFIRE_SEGMENT_TEXT &&
      right->kind == AL_CAMPFIRE_SEGMENT_INTEGER)
    return -1;
  if (left->kind == AL_CAMPFIRE_SEGMENT_INTEGER &&
      right->kind == AL_CAMPFIRE_SEGMENT_TEXT)
    return 1;
  return al_campfire_compare_utf8_segments(left, right);
}

static int32_t al_campfire_compare_versions(
    const al_campfire_string_view *left,
    const al_campfire_string_view *right) {
  uint32_t left_cursor = 0u;
  uint32_t right_cursor = 0u;
  uint32_t index;
  const int32_t left_comparable = al_campfire_version_is_comparable(left);
  const int32_t right_comparable = al_campfire_version_is_comparable(right);
  const int32_t left_blank = al_campfire_string_is_blank(left);
  const int32_t right_blank = al_campfire_string_is_blank(right);
  if (!left_comparable) {
    uint32_t unit;
    if (left->code_units == right->code_units) {
      for (unit = 0u; unit < left->code_units; ++unit) {
        if (al_campfire_string_unit(left, unit) !=
            al_campfire_string_unit(right, unit))
          return -1;
      }
      return 0;
    }
    return -1;
  }
  for (index = 0u; index < 6u; ++index) {
    al_campfire_version_segment left_segment;
    al_campfire_version_segment right_segment;
    const int32_t has_left = al_campfire_get_segment(
        left, left_comparable, left_blank, &left_cursor, index, &left_segment);
    const int32_t has_right = al_campfire_get_segment(
        right, right_comparable, right_blank, &right_cursor, index,
        &right_segment);
    int32_t result;
    if (!has_left) {
      left_segment.kind = AL_CAMPFIRE_SEGMENT_INTEGER;
      left_segment.owner = NULL;
      left_segment.start_unit = 0u;
      left_segment.length_units = 1u;
      left_segment.is_zero_literal = 1u;
    }
    if (!has_right) {
      right_segment.kind = AL_CAMPFIRE_SEGMENT_INTEGER;
      right_segment.owner = NULL;
      right_segment.start_unit = 0u;
      right_segment.length_units = 1u;
      right_segment.is_zero_literal = 1u;
    }
    result = al_campfire_compare_segments(&left_segment, &right_segment);
    if (result != 0)
      return result;
  }
  return 0;
}

int32_t al_campfire_browser_version_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_string_view version;
  al_campfire_string_view minimum;
  uint64_t result;
  (void)pure_context;
  if (output_extent_bytes != NULL)
    *output_extent_bytes = 0u;
  if (!al_campfire_layout_valid(layout) || inputs == NULL || input_count != 2u ||
      output == NULL || output_extent_bytes == NULL || output_capacity_bytes < 8u ||
      output_type_index >= layout->type_count ||
      layout->types[output_type_index].kind != AL_OWNING_TYPE_BOOL ||
      !al_campfire_read_input_string(layout, &inputs[0], &version) ||
      !al_campfire_read_input_string(layout, &inputs[1], &minimum))
    return 1;
  result = al_campfire_compare_versions(&version, &minimum) >= 0 ? 1u : 0u;
  al_campfire_write_u64_le(output, result);
  *output_extent_bytes = 8u;
  return 0;
}

/* The signed_id JSON envelope: {"_rails":{"data":<id>,"pur":"user/avatar"}}. */
static uint32_t al_campfire_avatar_json(int64_t user_id, uint8_t *json) {
  static const char prefix[] = "{\"_rails\":{\"data\":";
  static const char suffix[] = ",\"pur\":\"user/avatar\"}}";
  uint8_t digits[24];
  uint32_t digit_count = 0u;
  uint32_t length = 0u;
  uint64_t magnitude = user_id < 0 ? (uint64_t)0 - (uint64_t)user_id
                                   : (uint64_t)user_id;
  uint32_t index;
  do {
    digits[digit_count++] = (uint8_t)('0' + (magnitude % 10u));
    magnitude /= 10u;
  } while (magnitude != 0u);
  for (index = 0u; index < sizeof(prefix) - 1u; ++index)
    json[length++] = (uint8_t)prefix[index];
  if (user_id < 0)
    json[length++] = '-';
  while (digit_count > 0u)
    json[length++] = digits[--digit_count];
  for (index = 0u; index < sizeof(suffix) - 1u; ++index)
    json[length++] = (uint8_t)suffix[index];
  return length;
}

uint32_t al_campfire_avatar_token_length_v1(int64_t user_id) {
  uint8_t json[64];
  const uint32_t json_length = al_campfire_avatar_json(user_id, json);
  /* Unpadded base64url, "--", 64 hex digits. */
  return (json_length * 4u + 2u) / 3u + 2u + 64u;
}

int32_t al_campfire_avatar_token_v1(void *pure_context, int64_t user_id,
                                    uint8_t *output, uint32_t output_capacity,
                                    uint32_t *output_length) {
  static const uint8_t alphabet[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  static const uint8_t hex[] = "0123456789abcdef";
  al_campfire_pure_context *context = (al_campfire_pure_context *)pure_context;
  BCRYPT_HASH_HANDLE hash = NULL;
  uint8_t json[64];
  uint8_t digest[32];
  uint32_t json_length;
  uint32_t encoded = 0u;
  uint32_t index;
  NTSTATUS status;
  int32_t result = 1;
  if (context == NULL || context->magic != AL_CAMPFIRE_PURE_CONTEXT_MAGIC ||
      output == NULL || output_length == NULL ||
      output_capacity < al_campfire_avatar_token_length_v1(user_id))
    return 1;
  json_length = al_campfire_avatar_json(user_id, json);
  for (index = 0u; index + 2u < json_length; index += 3u) {
    const uint32_t triple = ((uint32_t)json[index] << 16u) |
                            ((uint32_t)json[index + 1u] << 8u) |
                            json[index + 2u];
    output[encoded++] = alphabet[(triple >> 18u) & 63u];
    output[encoded++] = alphabet[(triple >> 12u) & 63u];
    output[encoded++] = alphabet[(triple >> 6u) & 63u];
    output[encoded++] = alphabet[triple & 63u];
  }
  if (json_length - index == 1u) {
    const uint32_t triple = (uint32_t)json[index] << 16u;
    output[encoded++] = alphabet[(triple >> 18u) & 63u];
    output[encoded++] = alphabet[(triple >> 12u) & 63u];
  } else if (json_length - index == 2u) {
    const uint32_t triple = ((uint32_t)json[index] << 16u) |
                            ((uint32_t)json[index + 1u] << 8u);
    output[encoded++] = alphabet[(triple >> 18u) & 63u];
    output[encoded++] = alphabet[(triple >> 12u) & 63u];
    output[encoded++] = alphabet[(triple >> 6u) & 63u];
  }
  AcquireSRWLockExclusive(&context->lock);
  status = BCryptCreateHash(context->hmac_sha256_algorithm, &hash,
                            context->hmac256_object,
                            context->hmac256_object_bytes, context->avatar_key,
                            AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (BCRYPT_SUCCESS(status))
    status = BCryptHashData(hash, (PUCHAR)output, encoded, 0u);
  if (BCRYPT_SUCCESS(status))
    status = BCryptFinishHash(hash, digest, sizeof(digest), 0u);
  if (hash != NULL)
    BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac256_object, context->hmac256_object_bytes);
  ReleaseSRWLockExclusive(&context->lock);
  if (BCRYPT_SUCCESS(status)) {
    output[encoded++] = '-';
    output[encoded++] = '-';
    for (index = 0u; index < sizeof(digest); ++index) {
      output[encoded++] = hex[digest[index] >> 4u];
      output[encoded++] = hex[digest[index] & 0x0fu];
    }
    *output_length = encoded;
    result = 0;
  }
  SecureZeroMemory(digest, sizeof(digest));
  return result;
}

/* --- Rails CSRF (report 271) ------------------------------------------------
 * The session's _csrf_token is urlsafe Base64 of 32 random bytes. A form's
 * authenticity token is Rails' masked token: a fresh 32-byte pad followed by
 * pad XOR HMAC-SHA256(secret, identifier), urlsafe Base64 without padding (86
 * characters). The identifier is "<path>#<method>", or "!real_csrf_token" for
 * the page's global token. Pinned against Rails' own vectors. */
#define AL_CAMPFIRE_CSRF_SECRET_BYTES 32u
#define AL_CAMPFIRE_CSRF_SECRET_CHARS 43u
#define AL_CAMPFIRE_CSRF_TOKEN_CHARS 86u
#define AL_CAMPFIRE_CSRF_TOKEN_EXTENT 96u /* 8-byte header + 86, padded */
#define AL_CAMPFIRE_CSRF_SECRET_EXTENT 56u /* 8-byte header + 43, padded */
#define AL_CAMPFIRE_BOOST_FORMS 8u

static const uint8_t al_campfire_urlsafe_alphabet[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

static int32_t al_campfire_urlsafe_value(uint8_t c) {
  if (c >= 'A' && c <= 'Z') return c - 'A';
  if (c >= 'a' && c <= 'z') return c - 'a' + 26;
  if (c >= '0' && c <= '9') return c - '0' + 52;
  if (c == '-') return 62;
  if (c == '_') return 63;
  return -1;
}

/* Exactly 43 unpadded characters decoding to 32 bytes (trailing bits zero). */
static int32_t al_campfire_csrf_decode_secret(const al_campfire_string_view *text,
                                              uint8_t raw[AL_CAMPFIRE_CSRF_SECRET_BYTES]) {
  uint32_t accumulator = 0u, bits = 0u, written = 0u, index;
  if (text->code_units != AL_CAMPFIRE_CSRF_SECRET_CHARS)
    return 0;
  for (index = 0u; index < text->code_units; ++index) {
    const int32_t value = al_campfire_urlsafe_value(text->data[index]);
    if (value < 0)
      return 0;
    accumulator = (accumulator << 6) | (uint32_t)value;
    bits += 6u;
    if (bits >= 8u) {
      bits -= 8u;
      raw[written++] = (uint8_t)(accumulator >> bits);
    }
  }
  return written == AL_CAMPFIRE_CSRF_SECRET_BYTES &&
         (accumulator & ((1u << bits) - 1u)) == 0u;
}

static uint32_t al_campfire_urlsafe_encode(const uint8_t *bytes, uint32_t count,
                                           uint8_t *out) {
  uint32_t index = 0u, written = 0u;
  for (; index + 2u < count; index += 3u) {
    const uint32_t triple = ((uint32_t)bytes[index] << 16) |
                            ((uint32_t)bytes[index + 1u] << 8) | bytes[index + 2u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 18) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 12) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 6) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[triple & 63u];
  }
  if (count - index == 1u) {
    const uint32_t triple = (uint32_t)bytes[index] << 16;
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 18) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 12) & 63u];
  } else if (count - index == 2u) {
    const uint32_t triple = ((uint32_t)bytes[index] << 16) |
                            ((uint32_t)bytes[index + 1u] << 8);
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 18) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 12) & 63u];
    out[written++] = al_campfire_urlsafe_alphabet[(triple >> 6) & 63u];
  }
  return written;
}

/* Writes one owning String (header, bytes, zero padding) at output. */
static void al_campfire_write_owning_string(uint8_t *output, const uint8_t *bytes,
                                            uint32_t count, uint32_t extent) {
  uint32_t index;
  al_campfire_write_u64_le(output, count);
  for (index = 0u; index < count; ++index)
    output[8u + index] = bytes[index];
  for (index = 8u + count; index < extent; ++index)
    output[index] = 0u;
}

static int32_t al_campfire_csrf_hmac(al_campfire_pure_context *context,
                                     const uint8_t raw[AL_CAMPFIRE_CSRF_SECRET_BYTES],
                                     const uint8_t *identifier, uint32_t identifier_bytes,
                                     uint8_t digest[32]) {
  BCRYPT_HASH_HANDLE hash = NULL;
  NTSTATUS status = BCryptCreateHash(context->hmac_sha256_algorithm, &hash,
                                     context->hmac256_object,
                                     context->hmac256_object_bytes, (PUCHAR)raw,
                                     AL_CAMPFIRE_CSRF_SECRET_BYTES, 0u);
  if (BCRYPT_SUCCESS(status))
    status = BCryptHashData(hash, (PUCHAR)identifier, identifier_bytes, 0u);
  if (BCRYPT_SUCCESS(status))
    status = BCryptFinishHash(hash, digest, 32u, 0u);
  if (hash != NULL)
    BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac256_object, context->hmac256_object_bytes);
  return BCRYPT_SUCCESS(status) ? 1 : 0;
}

/* One masked token (86 characters) for an HMAC digest and a 32-byte pad. */
static void al_campfire_csrf_mask_with(const uint8_t digest[32], const uint8_t pad[32],
                                       uint8_t out[AL_CAMPFIRE_CSRF_TOKEN_CHARS]) {
  uint8_t masked[64];
  uint32_t index;
  for (index = 0u; index < 32u; ++index)
    masked[index] = pad[index];
  for (index = 0u; index < 32u; ++index)
    masked[32u + index] = (uint8_t)(masked[index] ^ digest[index]);
  (void)al_campfire_urlsafe_encode(masked, 64u, out);
  SecureZeroMemory(masked, sizeof(masked));
}

/* One masked token with a fresh pad. */
static int32_t al_campfire_csrf_mask(const uint8_t digest[32], uint8_t out[AL_CAMPFIRE_CSRF_TOKEN_CHARS]) {
  uint8_t masked[64];
  uint32_t index;
  if (!BCRYPT_SUCCESS(BCryptGenRandom(NULL, masked, 32u, BCRYPT_USE_SYSTEM_PREFERRED_RNG)))
    return 0;
  for (index = 0u; index < 32u; ++index)
    masked[32u + index] = (uint8_t)(masked[index] ^ digest[index]);
  (void)al_campfire_urlsafe_encode(masked, 64u, out);
  SecureZeroMemory(masked, sizeof(masked));
  return 1;
}

static int32_t al_campfire_csrf_enter(al_campfire_pure_context *context,
                                      const al_owning_layout *layout,
                                      const al_campfire_room_read_input *inputs,
                                      uint32_t input_count, uint32_t expected_count,
                                      uint8_t *output, uint32_t *output_extent_bytes) {
  if (output_extent_bytes != NULL)
    *output_extent_bytes = 0u;
  return context != NULL && context->magic == AL_CAMPFIRE_PURE_CONTEXT_MAGIC &&
         al_campfire_layout_valid(layout) && inputs != NULL &&
         input_count == expected_count && output != NULL &&
         output_extent_bytes != NULL;
}

/* campfire.csrf.session-secret(Option<String>) -> CsrfSecret { value, fresh }. */
int32_t al_campfire_csrf_session_secret_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_pure_context *context = (al_campfire_pure_context *)pure_context;
  uint8_t raw[AL_CAMPFIRE_CSRF_SECRET_BYTES];
  uint8_t text[AL_CAMPFIRE_CSRF_SECRET_CHARS + 1u];
  uint32_t fresh = 1u;
  (void)output_type_index;
  if (!al_campfire_csrf_enter(context, layout, inputs, input_count, 1u, output,
                              output_extent_bytes) ||
      inputs[0].data == NULL || inputs[0].extent_bytes < 8u ||
      output_capacity_bytes < AL_CAMPFIRE_CSRF_SECRET_EXTENT + 8u)
    return 1;
  if (inputs[0].type_index < layout->type_count &&
      layout->types[inputs[0].type_index].kind == AL_OWNING_TYPE_OPTION &&
      layout->types[inputs[0].type_index].first_field < layout->field_count &&
      al_campfire_read_u64_le(inputs[0].data) == 0u && inputs[0].extent_bytes >= 16u) {
    al_campfire_room_read_input some = inputs[0];
    al_campfire_string_view candidate;
    some.data = inputs[0].data + 8u;
    some.extent_bytes = inputs[0].extent_bytes - 8u;
    some.type_index = layout->fields[layout->types[inputs[0].type_index].first_field].child_type_index;
    if (al_campfire_read_input_string(layout, &some, &candidate) &&
        al_campfire_csrf_decode_secret(&candidate, raw)) {
      uint32_t index;
      for (index = 0u; index < AL_CAMPFIRE_CSRF_SECRET_CHARS; ++index)
        text[index] = candidate.data[index];
      fresh = 0u;
    }
  }
  if (fresh != 0u) {
    if (!BCRYPT_SUCCESS(BCryptGenRandom(NULL, raw, AL_CAMPFIRE_CSRF_SECRET_BYTES,
                                        BCRYPT_USE_SYSTEM_PREFERRED_RNG)))
      return 1;
    (void)al_campfire_urlsafe_encode(raw, AL_CAMPFIRE_CSRF_SECRET_BYTES, text);
  }
  al_campfire_write_owning_string(output, text, AL_CAMPFIRE_CSRF_SECRET_CHARS,
                                  AL_CAMPFIRE_CSRF_SECRET_EXTENT);
  al_campfire_write_u64_le(output + AL_CAMPFIRE_CSRF_SECRET_EXTENT, fresh);
  *output_extent_bytes = AL_CAMPFIRE_CSRF_SECRET_EXTENT + 8u;
  SecureZeroMemory(raw, sizeof(raw));
  SecureZeroMemory(text, sizeof(text));
  return 0;
}

/* campfire.csrf.form-token(secret: String, identifier: String) -> String. */
int32_t al_campfire_csrf_form_token_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  al_campfire_pure_context *context = (al_campfire_pure_context *)pure_context;
  al_campfire_string_view secret, identifier;
  uint8_t raw[AL_CAMPFIRE_CSRF_SECRET_BYTES], digest[32];
  uint8_t token[AL_CAMPFIRE_CSRF_TOKEN_CHARS];
  int32_t ok;
  (void)output_type_index;
  if (!al_campfire_csrf_enter(context, layout, inputs, input_count, 2u, output,
                              output_extent_bytes) ||
      output_capacity_bytes < AL_CAMPFIRE_CSRF_TOKEN_EXTENT ||
      !al_campfire_read_input_string(layout, &inputs[0], &secret) ||
      !al_campfire_read_input_string(layout, &inputs[1], &identifier) ||
      !al_campfire_csrf_decode_secret(&secret, raw))
    return 1;
  AcquireSRWLockExclusive(&context->lock);
  ok = al_campfire_csrf_hmac(context, raw, identifier.data, identifier.code_units, digest);
  ReleaseSRWLockExclusive(&context->lock);
  ok = ok && al_campfire_csrf_mask(digest, token);
  SecureZeroMemory(raw, sizeof(raw));
  SecureZeroMemory(digest, sizeof(digest));
  if (!ok)
    return 1;
  al_campfire_write_owning_string(output, token, AL_CAMPFIRE_CSRF_TOKEN_CHARS,
                                  AL_CAMPFIRE_CSRF_TOKEN_EXTENT);
  *output_extent_bytes = AL_CAMPFIRE_CSRF_TOKEN_EXTENT;
  return 0;
}

/* campfire.csrf.boost-tokens(secret: String, message-id: MessageId) ->
 * BoostTokens: eight masked tokens for /messages/:id/boosts#post (one HMAC). */
int32_t al_campfire_csrf_boost_tokens_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  static const uint8_t prefix[] = "/messages/";
  static const uint8_t suffix[] = "/boosts#post";
  al_campfire_pure_context *context = (al_campfire_pure_context *)pure_context;
  al_campfire_string_view secret;
  int64_t message_id = 0;
  uint8_t raw[AL_CAMPFIRE_CSRF_SECRET_BYTES], digest[32];
  uint8_t identifier[64], digits[24];
  uint8_t token[AL_CAMPFIRE_CSRF_TOKEN_CHARS];
  uint32_t length = 0u, digit_count = 0u, index;
  uint64_t magnitude;
  int32_t ok;
  (void)output_type_index;
  if (!al_campfire_csrf_enter(context, layout, inputs, input_count, 2u, output,
                              output_extent_bytes) ||
      output_capacity_bytes < AL_CAMPFIRE_BOOST_FORMS * AL_CAMPFIRE_CSRF_TOKEN_EXTENT ||
      !al_campfire_read_input_string(layout, &inputs[0], &secret) ||
      !al_campfire_read_input_i64(layout, &inputs[1], &message_id) ||
      message_id < 0 || !al_campfire_csrf_decode_secret(&secret, raw))
    return 1;
  for (index = 0u; index < sizeof(prefix) - 1u; ++index)
    identifier[length++] = prefix[index];
  magnitude = (uint64_t)message_id;
  do {
    digits[digit_count++] = (uint8_t)('0' + (magnitude % 10u));
    magnitude /= 10u;
  } while (magnitude != 0u);
  while (digit_count > 0u)
    identifier[length++] = digits[--digit_count];
  for (index = 0u; index < sizeof(suffix) - 1u; ++index)
    identifier[length++] = suffix[index];
  AcquireSRWLockExclusive(&context->lock);
  ok = al_campfire_csrf_hmac(context, raw, identifier, length, digest);
  ReleaseSRWLockExclusive(&context->lock);
  {
    /* All eight pads in one system RNG call. */
    uint8_t pads[AL_CAMPFIRE_BOOST_FORMS * 32u];
    ok = ok && BCRYPT_SUCCESS(BCryptGenRandom(NULL, pads, (ULONG)sizeof(pads),
                                              BCRYPT_USE_SYSTEM_PREFERRED_RNG));
    for (index = 0u; ok && index < AL_CAMPFIRE_BOOST_FORMS; ++index) {
      al_campfire_csrf_mask_with(digest, pads + index * 32u, token);
      al_campfire_write_owning_string(output + index * AL_CAMPFIRE_CSRF_TOKEN_EXTENT,
                                      token, AL_CAMPFIRE_CSRF_TOKEN_CHARS,
                                      AL_CAMPFIRE_CSRF_TOKEN_EXTENT);
    }
    SecureZeroMemory(pads, sizeof(pads));
  }
  SecureZeroMemory(raw, sizeof(raw));
  SecureZeroMemory(digest, sizeof(digest));
  if (!ok)
    return 1;
  *output_extent_bytes = AL_CAMPFIRE_BOOST_FORMS * AL_CAMPFIRE_CSRF_TOKEN_EXTENT;
  return 0;
}

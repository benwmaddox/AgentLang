#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>

#include <stdint.h>
#include <stddef.h>
#include <limits.h>
#include <string.h>

#include "cookie_response_codec.h"

#pragma comment(lib, "bcrypt.lib")

#define AL_CAMPFIRE_PURE_CONTEXT_MAGIC UINT32_C(0x43505231)
#define AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC UINT32_C(0x43525231)
#define AL_CAMPFIRE_COOKIE_MAX_BYTES 4096u
#define AL_CAMPFIRE_COOKIE_NAME_BYTES 13u
#define AL_CAMPFIRE_SCRATCH_BYTES 4096u
#define AL_CAMPFIRE_TOKEN_UNITS 4096u
#define AL_CAMPFIRE_SIGNING_KEY_BYTES 64u
#define AL_CAMPFIRE_SHA1_BYTES 20u
#define AL_CAMPFIRE_HEX_SHA1_BYTES 40u
#define AL_CAMPFIRE_HMAC_OBJECT_CAPACITY 512u
#define AL_CAMPFIRE_AES_OBJECT_CAPACITY 4096u
#define AL_CAMPFIRE_ENCRYPTION_KEY_BYTES 32u
#define AL_CAMPFIRE_AES_NONCE_BYTES 12u
#define AL_CAMPFIRE_AES_TAG_BYTES 16u
#define AL_CAMPFIRE_MAX_JSON_BYTES 16384u
#define AL_CAMPFIRE_MAX_JSON_ESTIMATE 65536u
#define AL_CAMPFIRE_SESSION_COOKIE_NAME_BYTES 17u
#define AL_CAMPFIRE_TOKEN_COOKIE_NAME_BYTES 13u
#define AL_CAMPFIRE_TOKEN_COOKIE_RAW_MAX \
  (AL_CAMPFIRE_COOKIE_MAX_BYTES - AL_CAMPFIRE_TOKEN_COOKIE_NAME_BYTES)
#define AL_CAMPFIRE_SESSION_COOKIE_RAW_MAX \
  (AL_CAMPFIRE_COOKIE_MAX_BYTES - AL_CAMPFIRE_SESSION_COOKIE_NAME_BYTES)
#define AL_CAMPFIRE_JSON_MAX_DEPTH 32u

typedef struct al_campfire_pure_context {
  uint32_t magic;
  SRWLOCK lock;
  BCRYPT_ALG_HANDLE hmac_sha1_algorithm;
  ULONG hmac_object_bytes;
  uint8_t signing_key[AL_CAMPFIRE_SIGNING_KEY_BYTES];
  uint8_t hmac_object[AL_CAMPFIRE_HMAC_OBJECT_CAPACITY];
  BCRYPT_ALG_HANDLE aes_algorithm;
  BCRYPT_KEY_HANDLE aes_key;
  ULONG aes_object_bytes;
  uint8_t encryption_key[AL_CAMPFIRE_ENCRYPTION_KEY_BYTES];
  uint8_t aes_key_object[AL_CAMPFIRE_AES_OBJECT_CAPACITY];
  uint8_t nonce[AL_CAMPFIRE_AES_NONCE_BYTES];
  uint8_t tag[AL_CAMPFIRE_AES_TAG_BYTES];
  uint8_t session_cipher[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t raw_ascii[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t payload[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t message[AL_CAMPFIRE_SCRATCH_BYTES];
  uint8_t inner_json[AL_CAMPFIRE_MAX_JSON_BYTES];
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

static uint16_t al_campfire_read_u16_le(const uint8_t *bytes) {
  return (uint16_t)((uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8u));
}

static void al_campfire_write_u16_le(uint8_t *bytes, uint16_t value) {
  bytes[0] = (uint8_t)value;
  bytes[1] = (uint8_t)(value >> 8u);
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
  payload = 8u + (uint64_t)code_units * 2u;
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

static uint16_t al_campfire_string_unit(const al_campfire_string_view *view,
                                       uint32_t index) {
  return al_campfire_read_u16_le(view->data + ((size_t)index * 2u));
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
  SecureZeroMemory(context->nonce, sizeof(context->nonce));
  SecureZeroMemory(context->tag, sizeof(context->tag));
  SecureZeroMemory(context->session_cipher, sizeof(context->session_cipher));
  SecureZeroMemory(context->raw_ascii, sizeof(context->raw_ascii));
  SecureZeroMemory(context->payload, sizeof(context->payload));
  SecureZeroMemory(context->message, sizeof(context->message));
  SecureZeroMemory(context->inner_json, sizeof(context->inner_json));
  SecureZeroMemory(context->token, sizeof(context->token));
}

static int32_t al_campfire_utf8_byte_count(
    const al_campfire_string_view *view, uint32_t maximum,
    uint32_t *out_bytes) {
  uint64_t total = 0u;
  uint32_t index = 0u;
  if (view == NULL || out_bytes == NULL)
    return 0;
  while (index < view->code_units) {
    const uint16_t first = al_campfire_string_unit(view, index);
    if (first <= 0x7fu) {
      total += 1u;
      ++index;
    } else if (first <= 0x7ffu) {
      total += 2u;
      ++index;
    } else if (first >= 0xd800u && first <= 0xdbffu &&
               index + 1u < view->code_units) {
      const uint16_t second = al_campfire_string_unit(view, index + 1u);
      if (second >= 0xdc00u && second <= 0xdfffu) {
        total += 4u;
        index += 2u;
      } else {
        /* Encoding.UTF8's default replacement for an unpaired surrogate. */
        total += 3u;
        ++index;
      }
    } else {
      /* Includes isolated low surrogates, which also become U+FFFD. */
      total += 3u;
      ++index;
    }
    if (total > maximum)
      return 0;
  }
  *out_bytes = (uint32_t)total;
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

static int32_t al_campfire_utf8_span_valid(const uint8_t *value,
                                           uint32_t value_bytes) {
  uint32_t index = 0u;
  if (value == NULL && value_bytes != 0u)
    return 0;
  while (index < value_bytes) {
    uint32_t scalar = 0u;
    if (!al_campfire_read_utf8_scalar(value, value_bytes, &index, &scalar))
      return 0;
  }
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

static int32_t al_campfire_json_parse_null(al_campfire_json_parser *parser);

static int32_t al_campfire_json_estimate_add(uint64_t *estimate,
                                             uint64_t amount) {
  if (estimate == NULL || *estimate > AL_CAMPFIRE_MAX_JSON_ESTIMATE ||
      amount > AL_CAMPFIRE_MAX_JSON_ESTIMATE - *estimate)
    return 0;
  *estimate += amount;
  return 1;
}

static int32_t al_campfire_json_estimate_value(
    al_campfire_json_parser *parser, uint32_t depth, uint64_t *estimate) {
  uint32_t string_bytes = 0u;
  if (parser == NULL || estimate == NULL || parser->index >= parser->length)
    return 0;
  al_campfire_json_skip_whitespace(parser);
  if (parser->index >= parser->length)
    return 0;
  switch (parser->bytes[parser->index]) {
  case (uint8_t)'"':
    if (!al_campfire_json_parse_string(parser, NULL, 0u, NULL, NULL, 0u,
                                      &string_bytes, NULL, NULL, 0u, NULL))
      return 0;
    return al_campfire_json_estimate_add(estimate,
                                         (uint64_t)string_bytes + 2u);
  case (uint8_t)'{': {
    if (depth >= AL_CAMPFIRE_JSON_MAX_DEPTH ||
        !al_campfire_json_estimate_add(estimate, 2u))
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
                                        &string_bytes, NULL, NULL, 0u, NULL) ||
          !al_campfire_json_estimate_add(estimate,
                                         (uint64_t)string_bytes + 4u))
        return 0;
      al_campfire_json_skip_whitespace(parser);
      if (parser->index >= parser->length ||
          parser->bytes[parser->index] != (uint8_t)':')
        return 0;
      ++parser->index;
      if (!al_campfire_json_estimate_value(parser, depth + 1u, estimate))
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
  case (uint8_t)'[': {
    if (depth >= AL_CAMPFIRE_JSON_MAX_DEPTH ||
        !al_campfire_json_estimate_add(estimate, 2u))
      return 0;
    ++parser->index;
    al_campfire_json_skip_whitespace(parser);
    if (parser->index < parser->length &&
        parser->bytes[parser->index] == (uint8_t)']') {
      ++parser->index;
      return 1;
    }
    while (parser->index < parser->length) {
      if (!al_campfire_json_estimate_value(parser, depth + 1u, estimate))
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
  case (uint8_t)'n':
    if (!al_campfire_json_parse_null(parser))
      return 0;
    return al_campfire_json_estimate_add(estimate, 4u);
  case (uint8_t)'t':
  case (uint8_t)'f':
    if (!al_campfire_json_parse_value(parser, depth))
      return 0;
    return al_campfire_json_estimate_add(estimate, 32u);
  default:
    if (!al_campfire_json_parse_number(parser))
      return 0;
    return al_campfire_json_estimate_add(estimate, 32u);
  }
}

static int32_t al_campfire_json_root_object(
    const uint8_t *bytes, uint32_t length, uint64_t *out_estimate) {
  al_campfire_json_parser parser;
  uint64_t estimate = 0u;
  if (bytes == NULL || out_estimate == NULL || length == 0u ||
      length > AL_CAMPFIRE_MAX_JSON_BYTES)
    return 0;
  parser.bytes = bytes;
  parser.length = length;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'{' ||
      !al_campfire_json_estimate_value(&parser, 0u, &estimate))
    return 0;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index != parser.length)
    return 0;
  *out_estimate = estimate;
  return 1;
}

static int32_t al_campfire_json_root_object_valid(const uint8_t *bytes,
                                                  uint32_t length) {
  al_campfire_json_parser parser;
  if (bytes == NULL || length == 0u || length > AL_CAMPFIRE_MAX_JSON_BYTES)
    return 0;
  parser.bytes = bytes;
  parser.length = length;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'{' ||
      !al_campfire_json_parse_value(&parser, 0u))
    return 0;
  al_campfire_json_skip_whitespace(&parser);
  return parser.index == parser.length;
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
    al_campfire_rails_fields *fields, const uint8_t *expected_purpose,
    uint32_t expected_purpose_length) {
  static const uint8_t rails_message_key[] = "message";
  static const uint8_t rails_purpose_key[] = "pur";
  static const uint8_t rails_expiry_key[] = "exp";
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
                       (expected_purpose != NULL &&
                        purpose_length == expected_purpose_length &&
                        memcmp(context->raw_ascii, expected_purpose,
                               expected_purpose_length) == 0)));
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
    al_campfire_rails_fields *out_fields, const uint8_t *expected_purpose,
    uint32_t expected_purpose_length) {
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
      if (!al_campfire_json_parse_rails_object(
              &parser, context, &current, expected_purpose,
              expected_purpose_length))
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
            context->payload, payload_length, context, &fields,
            (const uint8_t *)"cookie.session_token",
            (uint32_t)(sizeof("cookie.session_token") - 1u)) ||
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
  const uint64_t string_payload = 8u + (uint64_t)token_units * 2u;
  const uint64_t string_extent = (string_payload + 7u) & ~UINT64_C(7);
  const uint64_t total_extent = 8u + string_extent;
  uint32_t index;
  if (output == NULL || token == NULL || output_extent_bytes == NULL ||
      !al_campfire_cookie_output_valid(layout, output_type_index) ||
      total_extent > UINT32_MAX || total_extent > output_capacity_bytes)
    return 1;
  SecureZeroMemory(output, (size_t)total_extent);
  al_campfire_write_u64_le(output, 0u);
  al_campfire_write_u32_le(output + 8u, token_units);
  for (index = 0u; index < token_units; ++index)
    al_campfire_write_u16_le(output + 16u + index * 2u, token[index]);
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
  static const uint8_t encrypted_cookie_salt[] =
      "authenticated encrypted cookie";
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
      (PUCHAR)encrypted_cookie_salt,
      (ULONG)(sizeof(encrypted_cookie_salt) - 1u), 1000u,
      context->encryption_key, AL_CAMPFIRE_ENCRYPTION_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptDeriveKeyPBKDF2(
      sha256_algorithm, (PUCHAR)secret_utf8, secret_length,
      (PUCHAR)signed_cookie_salt,
      (ULONG)(sizeof(signed_cookie_salt) - 1u), 1000u,
      context->signing_key, AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
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
  status = BCryptOpenAlgorithmProvider(&context->aes_algorithm,
                                       BCRYPT_AES_ALGORITHM, NULL, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  status = BCryptSetProperty(
      context->aes_algorithm, BCRYPT_CHAINING_MODE,
      (PUCHAR)BCRYPT_CHAIN_MODE_GCM,
      (ULONG)sizeof(BCRYPT_CHAIN_MODE_GCM), 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  object_bytes = 0u;
  status = BCryptGetProperty(context->aes_algorithm, BCRYPT_OBJECT_LENGTH,
                              (PUCHAR)&object_bytes, sizeof(object_bytes),
                              &result_bytes, 0u);
  if (!BCRYPT_SUCCESS(status) || result_bytes != sizeof(object_bytes) ||
      object_bytes == 0u || object_bytes > sizeof(context->aes_key_object))
    goto initialization_failure;
  context->aes_object_bytes = object_bytes;
  status = BCryptGenerateSymmetricKey(
      context->aes_algorithm, &context->aes_key, context->aes_key_object,
      context->aes_object_bytes, context->encryption_key,
      AL_CAMPFIRE_ENCRYPTION_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto initialization_failure;
  context->magic = AL_CAMPFIRE_PURE_CONTEXT_MAGIC;
  BCryptCloseAlgorithmProvider(sha256_algorithm, 0u);
  return 0;

initialization_failure:
  if (sha256_algorithm != NULL)
    BCryptCloseAlgorithmProvider(sha256_algorithm, 0u);
  if (context->hmac_sha1_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha1_algorithm, 0u);
    context->hmac_sha1_algorithm = NULL;
  }
  if (context->aes_key != NULL) {
    BCryptDestroyKey(context->aes_key);
    context->aes_key = NULL;
  }
  if (context->aes_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->aes_algorithm, 0u);
    context->aes_algorithm = NULL;
  }
  SecureZeroMemory(context, sizeof(*context));
  return 1;
}

static void al_campfire_context_destroy(
    al_campfire_pure_context *context, uint32_t expected_magic) {
  if (context == NULL || context->magic != expected_magic)
    return;
  AcquireSRWLockExclusive(&context->lock);
  if (context->magic != expected_magic) {
    ReleaseSRWLockExclusive(&context->lock);
    return;
  }
  context->magic = 0u;
  if (context->hmac_sha1_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->hmac_sha1_algorithm, 0u);
    context->hmac_sha1_algorithm = NULL;
  }
  if (context->aes_key != NULL) {
    BCryptDestroyKey(context->aes_key);
    context->aes_key = NULL;
  }
  if (context->aes_algorithm != NULL) {
    BCryptCloseAlgorithmProvider(context->aes_algorithm, 0u);
    context->aes_algorithm = NULL;
  }
  al_campfire_clear_scratch(context);
  SecureZeroMemory(context->signing_key, sizeof(context->signing_key));
  SecureZeroMemory(context->encryption_key, sizeof(context->encryption_key));
  SecureZeroMemory(context->aes_key_object, sizeof(context->aes_key_object));
  ReleaseSRWLockExclusive(&context->lock);
  SecureZeroMemory(context, sizeof(*context));
}

void al_campfire_pure_context_destroy_v1(void *pure_context) {
  al_campfire_context_destroy((al_campfire_pure_context *)pure_context,
                              AL_CAMPFIRE_PURE_CONTEXT_MAGIC);
}

uint32_t al_campfire_cookie_response_context_size_v1(void) {
  return (uint32_t)sizeof(al_campfire_pure_context);
}

uint32_t al_campfire_cookie_response_context_alignment_v1(void) {
  return (uint32_t)__alignof(al_campfire_pure_context);
}

int32_t al_campfire_cookie_response_context_init_v1(
    void *storage, uint32_t storage_bytes, const uint8_t *secret_utf8,
    uint32_t secret_length) {
  int32_t status = al_campfire_pure_context_init_v1(
      storage, storage_bytes, secret_utf8, secret_length);
  if (status == 0)
    ((al_campfire_pure_context *)storage)->magic =
        AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC;
  return status;
}

void al_campfire_cookie_response_context_destroy_v1(void *response_context) {
  al_campfire_context_destroy(
      (al_campfire_pure_context *)response_context,
      AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC);
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

static int32_t al_campfire_base64_encode_standard(
    const uint8_t *input, uint32_t input_bytes, uint8_t *output,
    uint32_t output_capacity, uint32_t *output_bytes) {
  static const uint8_t alphabet[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  uint64_t expected = ((uint64_t)input_bytes + 2u) / 3u * 4u;
  uint32_t source = 0u;
  uint32_t target = 0u;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if ((input == NULL && input_bytes != 0u) || output == NULL ||
      output_bytes == NULL || expected > UINT32_MAX ||
      expected > output_capacity)
    return 0;
  while (source < input_bytes) {
    const uint32_t remaining = input_bytes - source;
    const uint32_t a = input[source++];
    const uint32_t b = remaining > 1u ? input[source++] : 0u;
    const uint32_t c = remaining > 2u ? input[source++] : 0u;
    output[target++] = alphabet[(a >> 2u) & 0x3fu];
    output[target++] = alphabet[((a & 0x03u) << 4u) | (b >> 4u)];
    output[target++] = remaining > 1u
                           ? alphabet[((b & 0x0fu) << 2u) | (c >> 6u)]
                           : (uint8_t)'=';
    output[target++] = remaining > 2u ? alphabet[c & 0x3fu] : (uint8_t)'=';
  }
  if (target != (uint32_t)expected)
    return 0;
  *output_bytes = target;
  return 1;
}

static int32_t al_campfire_base64_decode_standard(
    const uint8_t *encoded, uint32_t encoded_bytes, uint8_t *output,
    uint32_t output_capacity, uint32_t *output_bytes) {
  uint32_t index;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if (encoded == NULL || encoded_bytes == 0u || (encoded_bytes & 3u) != 0u ||
      output == NULL || output_bytes == NULL)
    return 0;
  for (index = 0u; index < encoded_bytes; ++index) {
    const uint8_t value = encoded[index];
    if (!((value >= (uint8_t)'A' && value <= (uint8_t)'Z') ||
          (value >= (uint8_t)'a' && value <= (uint8_t)'z') ||
          (value >= (uint8_t)'0' && value <= (uint8_t)'9') ||
          value == (uint8_t)'+' || value == (uint8_t)'/' ||
          value == (uint8_t)'='))
      return 0;
  }
  if (!al_campfire_decode_base64_urlsafe(encoded, encoded_bytes, output,
                                         output_capacity, output_bytes))
    return 0;
  return 1;
}

static int32_t al_campfire_append_bytes(uint8_t *output, uint32_t capacity,
                                       uint32_t *used, const uint8_t *bytes,
                                       uint32_t length) {
  if (output == NULL || used == NULL || (bytes == NULL && length != 0u) ||
      *used > capacity || length > capacity - *used)
    return 0;
  if (length != 0u)
    memcpy(output + *used, bytes, length);
  *used += length;
  return 1;
}

static int32_t al_campfire_utf8_json_string(
    const uint8_t *value, uint32_t value_bytes, uint8_t *output,
    uint32_t output_capacity, uint32_t *output_bytes) {
  static const uint8_t hex[] = "0123456789abcdef";
  uint32_t input_index = 0u;
  uint32_t used = 0u;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if ((value == NULL && value_bytes != 0u) || output == NULL ||
      output_bytes == NULL || output_capacity < 2u)
    return 0;
  output[used++] = (uint8_t)'"';
  while (input_index < value_bytes) {
    const uint32_t start = input_index;
    uint32_t scalar = 0u;
    uint8_t escaped[6];
    uint32_t escaped_length = 0u;
    if (!al_campfire_read_utf8_scalar(value, value_bytes, &input_index,
                                      &scalar))
      return 0;
    switch (scalar) {
    case '"':
    case '\\':
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)scalar;
      escaped_length = 2u;
      break;
    case '<':
    case '>':
    case '&':
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'u';
      escaped[2] = (uint8_t)'0';
      escaped[3] = (uint8_t)'0';
      escaped[4] = scalar == '&' ? (uint8_t)'2' : (uint8_t)'3';
      escaped[5] = scalar == '<' ? (uint8_t)'c'
                   : scalar == '>' ? (uint8_t)'e'
                                   : (uint8_t)'6';
      escaped_length = 6u;
      break;
    case 0x08u:
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'b';
      escaped_length = 2u;
      break;
    case 0x0cu:
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'f';
      escaped_length = 2u;
      break;
    case 0x0au:
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'n';
      escaped_length = 2u;
      break;
    case 0x0du:
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'r';
      escaped_length = 2u;
      break;
    case 0x09u:
      escaped[0] = (uint8_t)'\\';
      escaped[1] = (uint8_t)'t';
      escaped_length = 2u;
      break;
    default:
      if (scalar < 0x20u) {
        escaped[0] = (uint8_t)'\\';
        escaped[1] = (uint8_t)'u';
        escaped[2] = (uint8_t)'0';
        escaped[3] = (uint8_t)'0';
        escaped[4] = hex[(scalar >> 4u) & 0x0fu];
        escaped[5] = hex[scalar & 0x0fu];
        escaped_length = 6u;
      }
      break;
    }
    if (escaped_length != 0u) {
      if (!al_campfire_append_bytes(output, output_capacity, &used, escaped,
                                    escaped_length))
        return 0;
    } else if (!al_campfire_append_bytes(output, output_capacity, &used,
                                         value + start,
                                         input_index - start)) {
      return 0;
    }
  }
  if (used >= output_capacity)
    return 0;
  output[used++] = (uint8_t)'"';
  *output_bytes = used;
  return 1;
}

static void al_campfire_write_decimal2(uint8_t *output, uint32_t value) {
  output[0] = (uint8_t)('0' + ((value / 10u) % 10u));
  output[1] = (uint8_t)('0' + (value % 10u));
}

static void al_campfire_write_decimal3(uint8_t *output, uint32_t value) {
  output[0] = (uint8_t)('0' + ((value / 100u) % 10u));
  output[1] = (uint8_t)('0' + ((value / 10u) % 10u));
  output[2] = (uint8_t)('0' + (value % 10u));
}

static void al_campfire_write_decimal4(uint8_t *output, uint32_t value) {
  output[0] = (uint8_t)('0' + ((value / 1000u) % 10u));
  output[1] = (uint8_t)('0' + ((value / 100u) % 10u));
  output[2] = (uint8_t)('0' + ((value / 10u) % 10u));
  output[3] = (uint8_t)('0' + (value % 10u));
}

static int32_t al_campfire_expiry_twenty_calendar_years(
    int64_t now_microseconds, uint8_t expiry[24]) {
  static const uint8_t days_in_month[12] = {31u, 28u, 31u, 30u, 31u, 30u,
                                            31u, 31u, 30u, 31u, 30u, 31u};
  const int64_t unix_epoch_ticks = INT64_C(621355968000000000);
  const int64_t ticks_per_day = INT64_C(864000000000);
  int64_t current_ticks;
  uint64_t days;
  uint64_t time_of_day;
  uint32_t low_year = 1u;
  uint32_t high_year = 10000u;
  uint32_t year;
  uint32_t target_year;
  uint32_t day_of_year;
  uint32_t month = 1u;
  uint32_t day;
  uint32_t month_days;
  uint32_t hour;
  uint32_t minute;
  uint32_t second;
  uint32_t milliseconds;
  if (expiry == NULL ||
      now_microseconds < INT64_C(-62135596800000000) ||
      now_microseconds > INT64_C(253402300799999999))
    return 0;
  current_ticks = unix_epoch_ticks + now_microseconds * INT64_C(10);
  if (current_ticks < 0 ||
      current_ticks > INT64_C(3155378975999999999))
    return 0;
  days = (uint64_t)current_ticks / (uint64_t)ticks_per_day;
  time_of_day = (uint64_t)current_ticks % (uint64_t)ticks_per_day;
  while (low_year + 1u < high_year) {
    const uint32_t middle = low_year + (high_year - low_year) / 2u;
    if (al_campfire_days_before_year(middle) <= days)
      low_year = middle;
    else
      high_year = middle;
  }
  year = low_year;
  target_year = year + 20u;
  if (target_year > 9999u)
    return 0;
  day_of_year = (uint32_t)(days - al_campfire_days_before_year(year));
  while (month <= 12u) {
    month_days = days_in_month[month - 1u];
    if (month == 2u && al_campfire_is_leap_year(year))
      ++month_days;
    if (day_of_year < month_days)
      break;
    day_of_year -= month_days;
    ++month;
  }
  if (month > 12u)
    return 0;
  day = day_of_year + 1u;
  if (month == 2u && day == 29u &&
      !al_campfire_is_leap_year(target_year))
    day = 28u;
  hour = (uint32_t)(time_of_day / UINT64_C(36000000000));
  time_of_day %= UINT64_C(36000000000);
  minute = (uint32_t)(time_of_day / UINT64_C(600000000));
  time_of_day %= UINT64_C(600000000);
  second = (uint32_t)(time_of_day / UINT64_C(10000000));
  time_of_day %= UINT64_C(10000000);
  milliseconds = (uint32_t)(time_of_day / UINT64_C(10000));
  al_campfire_write_decimal4(expiry, target_year);
  expiry[4] = (uint8_t)'-';
  al_campfire_write_decimal2(expiry + 5u, month);
  expiry[7] = (uint8_t)'-';
  al_campfire_write_decimal2(expiry + 8u, day);
  expiry[10] = (uint8_t)'T';
  al_campfire_write_decimal2(expiry + 11u, hour);
  expiry[13] = (uint8_t)':';
  al_campfire_write_decimal2(expiry + 14u, minute);
  expiry[16] = (uint8_t)':';
  al_campfire_write_decimal2(expiry + 17u, second);
  expiry[19] = (uint8_t)'.';
  al_campfire_write_decimal3(expiry + 20u, milliseconds);
  expiry[23] = (uint8_t)'Z';
  return 1;
}

static int32_t al_campfire_hmac_sha1(
    al_campfire_pure_context *context, const uint8_t *bytes,
    uint32_t length, uint8_t digest[AL_CAMPFIRE_SHA1_BYTES]) {
  BCRYPT_HASH_HANDLE hash = NULL;
  NTSTATUS status;
  if (context == NULL || bytes == NULL || digest == NULL ||
      context->hmac_object_bytes == 0u)
    return 0;
  SecureZeroMemory(digest, AL_CAMPFIRE_SHA1_BYTES);
  status = BCryptCreateHash(context->hmac_sha1_algorithm, &hash,
                            context->hmac_object, context->hmac_object_bytes,
                            context->signing_key,
                            AL_CAMPFIRE_SIGNING_KEY_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto failure;
  status = BCryptHashData(hash, (PUCHAR)bytes, (ULONG)length, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto failure;
  status = BCryptFinishHash(hash, digest, AL_CAMPFIRE_SHA1_BYTES, 0u);
  if (!BCRYPT_SUCCESS(status))
    goto failure;
  BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac_object, context->hmac_object_bytes);
  return 1;

failure:
  if (hash != NULL)
    BCryptDestroyHash(hash);
  SecureZeroMemory(context->hmac_object, context->hmac_object_bytes);
  SecureZeroMemory(digest, AL_CAMPFIRE_SHA1_BYTES);
  return 0;
}

static int32_t al_campfire_aes_gcm_encrypt(
    al_campfire_pure_context *context, uint32_t plaintext_bytes,
    uint32_t *ciphertext_bytes) {
  BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO auth;
  ULONG output_bytes = 0u;
  NTSTATUS status;
  if (ciphertext_bytes != NULL)
    *ciphertext_bytes = 0u;
  if (context == NULL || ciphertext_bytes == NULL ||
      context->aes_key == NULL || plaintext_bytes > sizeof(context->payload))
    return 0;
  status = BCryptGenRandom(NULL, context->nonce, sizeof(context->nonce),
                           BCRYPT_USE_SYSTEM_PREFERRED_RNG);
  if (!BCRYPT_SUCCESS(status))
    return 0;
  BCRYPT_INIT_AUTH_MODE_INFO(auth);
  auth.pbNonce = context->nonce;
  auth.cbNonce = sizeof(context->nonce);
  auth.pbTag = context->tag;
  auth.cbTag = sizeof(context->tag);
  status = BCryptEncrypt(context->aes_key, context->payload, plaintext_bytes,
                         &auth, NULL, 0u, context->session_cipher,
                         sizeof(context->session_cipher), &output_bytes, 0u);
  if (!BCRYPT_SUCCESS(status) || output_bytes != plaintext_bytes)
    return 0;
  *ciphertext_bytes = output_bytes;
  return 1;
}

static int32_t al_campfire_aes_gcm_decrypt(
    al_campfire_pure_context *context, uint32_t ciphertext_bytes,
    uint32_t *plaintext_bytes) {
  BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO auth;
  ULONG output_bytes = 0u;
  NTSTATUS status;
  if (plaintext_bytes != NULL)
    *plaintext_bytes = 0u;
  if (context == NULL || plaintext_bytes == NULL ||
      context->aes_key == NULL ||
      ciphertext_bytes > sizeof(context->session_cipher))
    return 0;
  BCRYPT_INIT_AUTH_MODE_INFO(auth);
  auth.pbNonce = context->nonce;
  auth.cbNonce = sizeof(context->nonce);
  auth.pbTag = context->tag;
  auth.cbTag = sizeof(context->tag);
  status = BCryptDecrypt(context->aes_key, context->session_cipher,
                         ciphertext_bytes, &auth, NULL, 0u, context->payload,
                         sizeof(context->payload), &output_bytes, 0u);
  if (!BCRYPT_SUCCESS(status) || output_bytes > sizeof(context->payload))
    return 0;
  *plaintext_bytes = output_bytes;
  return 1;
}

static int32_t al_campfire_build_session_envelope(
    al_campfire_pure_context *context, const uint8_t *json,
    uint32_t json_bytes, int64_t now_microseconds,
    uint32_t *out_envelope_bytes) {
  static const uint8_t prefix[] = "{\"_rails\":{\"message\":\"";
  static const uint8_t middle[] = "\",\"exp\":\"";
  static const uint8_t purpose_prefix[] = "\",\"pur\":\"";
  static const uint8_t purpose[] = "cookie._campfire_session";
  static const uint8_t suffix[] = "\"}}";
  uint8_t expiry[24];
  uint32_t message_bytes = 0u;
  uint32_t used = 0u;
  int32_t result = 0;
  SecureZeroMemory(expiry, sizeof(expiry));
  if (context == NULL || json == NULL || out_envelope_bytes == NULL ||
      !al_campfire_base64_encode_standard(json, json_bytes, context->message,
                                          sizeof(context->message),
                                          &message_bytes) ||
      !al_campfire_expiry_twenty_calendar_years(now_microseconds, expiry))
    goto cleanup;
  if (!al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, prefix, sizeof(prefix) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, context->message, message_bytes) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, middle, sizeof(middle) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, expiry, sizeof(expiry)) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, purpose_prefix,
                                sizeof(purpose_prefix) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, purpose, sizeof(purpose) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &used, suffix, sizeof(suffix) - 1u))
    goto cleanup;
  *out_envelope_bytes = used;
  result = 1;

cleanup:
  SecureZeroMemory(expiry, sizeof(expiry));
  return result;
}

int32_t al_campfire_cookie_response_sign_session_token_v1(
    void *response_context, const uint8_t *token_utf8, uint32_t token_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes) {
  static const uint8_t prefix[] = "{\"_rails\":{\"message\":\"";
  static const uint8_t middle[] = "\",\"exp\":\"";
  static const uint8_t purpose_prefix[] = "\",\"pur\":\"";
  static const uint8_t purpose[] = "cookie.session_token";
  static const uint8_t suffix[] = "\"}}";
  static const uint8_t hex[] = "0123456789abcdef";
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)response_context;
  uint8_t expiry[24];
  uint8_t digest[AL_CAMPFIRE_SHA1_BYTES];
  uint32_t token_json_bytes = 0u;
  uint32_t message_bytes = 0u;
  uint32_t envelope_bytes = 0u;
  uint32_t encoded_bytes = 0u;
  uint32_t used = 0u;
  uint32_t index;
  int32_t status = 3;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  SecureZeroMemory(expiry, sizeof(expiry));
  SecureZeroMemory(digest, sizeof(digest));
  if (context == NULL ||
      context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC ||
      (token_utf8 == NULL && token_bytes != 0u) ||
      token_bytes > AL_CAMPFIRE_MAX_JSON_BYTES ||
      output == NULL || output_bytes == NULL ||
      now_unix_microseconds < INT64_C(-62135596800000000) ||
      now_unix_microseconds > INT64_C(253402300799999999)) {
    status = 1;
    goto done;
  }
  AcquireSRWLockExclusive(&context->lock);
  if (context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC)
    goto locked_done;
  al_campfire_clear_scratch(context);
  if (!al_campfire_utf8_json_string(token_utf8, token_bytes,
                                    context->inner_json,
                                    sizeof(context->inner_json),
                                    &token_json_bytes) ||
      !al_campfire_base64_encode_standard(
          context->inner_json, token_json_bytes, context->message,
          sizeof(context->message), &message_bytes) ||
      !al_campfire_expiry_twenty_calendar_years(now_unix_microseconds,
                                                expiry)) {
    status = 2;
    goto locked_done;
  }
  if (!al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, prefix,
                                sizeof(prefix) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, context->message,
                                message_bytes) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, middle,
                                sizeof(middle) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, expiry, sizeof(expiry)) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, purpose_prefix,
                                sizeof(purpose_prefix) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, purpose,
                                sizeof(purpose) - 1u) ||
      !al_campfire_append_bytes(context->payload, sizeof(context->payload),
                                &envelope_bytes, suffix,
                                sizeof(suffix) - 1u) ||
      !al_campfire_base64_encode_standard(
          context->payload, envelope_bytes, context->raw_ascii,
          sizeof(context->raw_ascii), &encoded_bytes) ||
      encoded_bytes > AL_CAMPFIRE_TOKEN_COOKIE_RAW_MAX - 42u ||
      output_capacity_bytes < encoded_bytes + 42u) {
    status = 2;
    goto locked_done;
  }
  if (!al_campfire_hmac_sha1(context, context->raw_ascii, encoded_bytes,
                             digest))
    goto locked_done;
  memcpy(output, context->raw_ascii, encoded_bytes);
  output[encoded_bytes] = (uint8_t)'-';
  output[encoded_bytes + 1u] = (uint8_t)'-';
  used = encoded_bytes + 2u;
  for (index = 0u; index < sizeof(digest); ++index) {
    output[used++] = hex[digest[index] >> 4u];
    output[used++] = hex[digest[index] & 0x0fu];
  }
  *output_bytes = used;
  status = 0;

locked_done:
  al_campfire_clear_scratch(context);
  ReleaseSRWLockExclusive(&context->lock);
done:
  SecureZeroMemory(expiry, sizeof(expiry));
  SecureZeroMemory(digest, sizeof(digest));
  return status;
}

int32_t al_campfire_cookie_response_encrypt_session_json_v1(
    void *response_context, const uint8_t *session_json_utf8,
    uint32_t session_json_bytes, int64_t now_unix_microseconds,
    uint8_t *output, uint32_t output_capacity_bytes,
    uint32_t *output_bytes) {
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)response_context;
  uint64_t estimate = 0u;
  uint32_t envelope_bytes = 0u;
  uint32_t ciphertext_bytes = 0u;
  uint32_t base64_cipher_bytes;
  uint32_t encoded_bytes = 0u;
  uint32_t used = 0u;
  static const uint8_t separator[] = "--";
  int32_t status = 3;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if (context == NULL ||
      context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC ||
      session_json_utf8 == NULL || output == NULL || output_bytes == NULL ||
      now_unix_microseconds < INT64_C(-62135596800000000) ||
      now_unix_microseconds > INT64_C(253402300799999999))
    return 1;
  if (!al_campfire_json_root_object(session_json_utf8, session_json_bytes,
                                    &estimate))
    return 1;
  AcquireSRWLockExclusive(&context->lock);
  if (context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC)
    goto cleanup;
  al_campfire_clear_scratch(context);
  if (!al_campfire_build_session_envelope(
          context, session_json_utf8, session_json_bytes,
          now_unix_microseconds, &envelope_bytes)) {
    status = 2;
    goto cleanup;
  }
  if (envelope_bytes > sizeof(context->payload)) {
    status = 2;
    goto cleanup;
  }
  base64_cipher_bytes =
      (uint32_t)(((uint64_t)envelope_bytes + 2u) / 3u * 4u);
  if ((uint64_t)base64_cipher_bytes + 44u >
          AL_CAMPFIRE_SESSION_COOKIE_RAW_MAX ||
      base64_cipher_bytes + 44u > output_capacity_bytes) {
    status = 2;
    goto cleanup;
  }
  if (!al_campfire_aes_gcm_encrypt(context, envelope_bytes,
                                  &ciphertext_bytes) ||
      ciphertext_bytes != envelope_bytes ||
      !al_campfire_base64_encode_standard(
          context->session_cipher, ciphertext_bytes, context->raw_ascii,
          sizeof(context->raw_ascii), &encoded_bytes) ||
      encoded_bytes != base64_cipher_bytes)
    goto cleanup;
  if (!al_campfire_append_bytes(context->raw_ascii,
                                sizeof(context->raw_ascii), &encoded_bytes,
                                separator, 2u) ||
      !al_campfire_base64_encode_standard(
          context->nonce, sizeof(context->nonce), context->raw_ascii + encoded_bytes,
          sizeof(context->raw_ascii) - encoded_bytes, &used))
    goto cleanup;
  encoded_bytes += used;
  used = 0u;
  if (!al_campfire_append_bytes(context->raw_ascii,
                                sizeof(context->raw_ascii), &encoded_bytes,
                                separator, 2u) ||
      !al_campfire_base64_encode_standard(
          context->tag, sizeof(context->tag), context->raw_ascii + encoded_bytes,
          sizeof(context->raw_ascii) - encoded_bytes, &used))
    goto cleanup;
  encoded_bytes += used;
  if (encoded_bytes > AL_CAMPFIRE_SESSION_COOKIE_RAW_MAX ||
      encoded_bytes > output_capacity_bytes)
    goto cleanup;
  memcpy(output, context->raw_ascii, encoded_bytes);
  *output_bytes = encoded_bytes;
  status = 0;

cleanup:
  al_campfire_clear_scratch(context);
  ReleaseSRWLockExclusive(&context->lock);
  return status;
}

int32_t al_campfire_cookie_response_decrypt_session_json_v1(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes) {
  static const uint8_t rails_prefix[] = "{\"_rails\":{\"message\":\"";
  static const uint8_t expected_purpose[] = "cookie._campfire_session";
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)response_context;
  uint32_t first_separator = UINT32_MAX;
  uint32_t second_separator = UINT32_MAX;
  uint32_t ciphertext_bytes = 0u;
  uint32_t nonce_bytes = 0u;
  uint32_t tag_bytes = 0u;
  uint32_t plaintext_bytes = 0u;
  uint32_t inner_bytes = 0u;
  uint32_t index;
  const uint8_t *json = NULL;
  uint32_t json_bytes = 0u;
  int32_t status = 1;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if (context == NULL ||
      context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC ||
      raw_cookie_utf8 == NULL || output == NULL || output_bytes == NULL ||
      cookie_bytes == 0u || cookie_bytes > AL_CAMPFIRE_SESSION_COOKIE_RAW_MAX ||
      now_unix_microseconds < INT64_C(-62135596800000000) ||
      now_unix_microseconds > INT64_C(253402300799999999))
    return 1;
  for (index = 0u; index < cookie_bytes; ++index) {
    if (raw_cookie_utf8[index] > 0x7fu)
      return 1;
    if (raw_cookie_utf8[index] == (uint8_t)'-' &&
        index + 1u < cookie_bytes &&
        raw_cookie_utf8[index + 1u] == (uint8_t)'-') {
      if (first_separator == UINT32_MAX) {
        first_separator = index;
        ++index;
      } else if (second_separator == UINT32_MAX) {
        second_separator = index;
        ++index;
      } else {
        return 1;
      }
    }
  }
  if (first_separator == UINT32_MAX || second_separator == UINT32_MAX ||
      second_separator <= first_separator + 2u ||
      second_separator + 2u >= cookie_bytes)
    return 1;
  AcquireSRWLockExclusive(&context->lock);
  if (context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC)
    goto cleanup;
  al_campfire_clear_scratch(context);
  if (!al_campfire_base64_decode_standard(
          raw_cookie_utf8, first_separator, context->session_cipher,
          sizeof(context->session_cipher), &ciphertext_bytes) ||
      !al_campfire_base64_decode_standard(
          raw_cookie_utf8 + first_separator + 2u,
          second_separator - first_separator - 2u, context->nonce,
          sizeof(context->nonce), &nonce_bytes) ||
      !al_campfire_base64_decode_standard(
          raw_cookie_utf8 + second_separator + 2u,
          cookie_bytes - second_separator - 2u, context->tag,
          sizeof(context->tag), &tag_bytes) ||
      nonce_bytes != AL_CAMPFIRE_AES_NONCE_BYTES ||
      tag_bytes != AL_CAMPFIRE_AES_TAG_BYTES || ciphertext_bytes == 0u ||
      ciphertext_bytes > sizeof(context->payload) ||
      !al_campfire_aes_gcm_decrypt(context, ciphertext_bytes,
                                   &plaintext_bytes))
    goto cleanup;
  if (plaintext_bytes >= sizeof(rails_prefix) - 1u &&
      memcmp(context->payload, rails_prefix, sizeof(rails_prefix) - 1u) ==
          0) {
    al_campfire_rails_fields fields;
    int64_t now_ticks;
    if (!al_campfire_json_parse_rails_envelope(
            context->payload, plaintext_bytes, context, &fields,
            expected_purpose, (uint32_t)(sizeof(expected_purpose) - 1u)) ||
        !fields.message_is_string || !fields.message_is_ascii ||
        !fields.purpose_matches || !fields.expiry_valid)
      goto cleanup;
    if (fields.expiry_present) {
      now_ticks = INT64_C(621355968000000000) +
                  now_unix_microseconds * INT64_C(10);
      if (now_ticks >= fields.expiry_utc_ticks)
        goto cleanup;
    }
    if (!al_campfire_base64_decode_standard(
            context->message, fields.message_length, context->inner_json,
            sizeof(context->inner_json), &inner_bytes))
      goto cleanup;
    json = context->inner_json;
    json_bytes = inner_bytes;
  } else {
    json = context->payload;
    json_bytes = plaintext_bytes;
  }
  if (!al_campfire_json_root_object_valid(json, json_bytes) ||
      json_bytes > output_capacity_bytes)
    goto cleanup;
  memcpy(output, json, json_bytes);
  *output_bytes = json_bytes;
  status = 0;

cleanup:
  al_campfire_clear_scratch(context);
  ReleaseSRWLockExclusive(&context->lock);
  return status;
}

static int32_t al_campfire_json_prior_key_matches(
    const uint8_t *json, uint32_t json_bytes, uint32_t key_start,
    const uint16_t *key, uint32_t key_units, uint16_t *compare_key,
    uint32_t key_capacity) {
  al_campfire_json_parser parser;
  if (json == NULL || key == NULL || compare_key == NULL ||
      key_start >= json_bytes)
    return -1;
  parser.bytes = json;
  parser.length = json_bytes;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'{')
    return -1;
  ++parser.index;
  al_campfire_json_skip_whitespace(&parser);
  while (parser.index < key_start) {
    uint32_t prior_key_units = 0u;
    uint32_t prior_key_start = parser.index;
    uint32_t index;
    if (!al_campfire_json_parse_string(
            &parser, compare_key, key_capacity, &prior_key_units, NULL, 0u,
            NULL, NULL, NULL, 0u, NULL) ||
        parser.index > key_start)
      return -1;
    if (prior_key_units == key_units) {
      for (index = 0u; index < key_units; ++index) {
        if (compare_key[index] != key[index])
          break;
      }
      if (index == key_units)
        return 1;
    }
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)':')
      return -1;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
    if (!al_campfire_json_parse_value(&parser, 1u))
      return -1;
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index < parser.length &&
        parser.bytes[parser.index] == (uint8_t)'}')
      return 0;
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)',')
      return -1;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index <= prior_key_start)
      return -1;
  }
  return 0;
}

static int32_t al_campfire_json_append_member_prefix(
    uint8_t *output, uint32_t capacity, uint32_t *used,
    uint32_t *property_count, const uint8_t *key, uint32_t key_bytes) {
  static const uint8_t comma = ',';
  static const uint8_t colon = ':';
  if (property_count == NULL)
    return 0;
  if (*property_count != 0u &&
      !al_campfire_append_bytes(output, capacity, used, &comma, 1u))
    return 0;
  if (!al_campfire_append_bytes(output, capacity, used, key, key_bytes) ||
      !al_campfire_append_bytes(output, capacity, used, &colon, 1u))
    return 0;
  ++*property_count;
  return 1;
}

static int32_t al_campfire_json_append_alert_value(
    uint8_t *output, uint32_t capacity, uint32_t *used,
    const uint8_t *alert_utf8, uint32_t alert_bytes) {
  static const uint8_t prefix[] = "{\"discard\":[],\"flashes\":{\"alert\":";
  static const uint8_t suffix[] = "}}";
  uint32_t encoded_bytes = 0u;
  if (!al_campfire_append_bytes(output, capacity, used, prefix,
                                sizeof(prefix) - 1u) ||
      *used > capacity ||
      !al_campfire_utf8_json_string(alert_utf8, alert_bytes, output + *used,
                                    capacity - *used, &encoded_bytes))
    return 0;
  *used += encoded_bytes;
  return al_campfire_append_bytes(output, capacity, used, suffix,
                                  sizeof(suffix) - 1u);
}

static int32_t al_campfire_json_mutate_session(
    const uint8_t *input_json, uint32_t input_bytes,
    uint32_t has_return_url, const uint8_t *return_url_utf8,
    uint32_t return_url_bytes, uint32_t has_alert,
    const uint8_t *alert_utf8, uint32_t alert_bytes,
    uint8_t *output_json, uint32_t output_capacity,
    uint32_t *output_bytes) {
  static const uint8_t empty_object[] = "{}";
  static const uint8_t root_open = '{';
  static const uint8_t root_close = '}';
  static const uint8_t return_key[] = "return_to_after_authenticating";
  static const uint8_t flash_key[] = "flash";
  static const uint8_t return_property_name[] =
      "\"return_to_after_authenticating\"";
  static const uint8_t flash_property_name[] = "\"flash\"";
  static const uint8_t comma = ',';
  al_campfire_json_parser parser;
  uint16_t current_key[AL_CAMPFIRE_TOKEN_UNITS];
  uint16_t compare_key[AL_CAMPFIRE_TOKEN_UNITS];
  uint32_t current_key_units;
  uint32_t used = 0u;
  uint32_t property_count = 0u;
  uint32_t saw_return = 0u;
  uint32_t saw_flash = 0u;
  int32_t result = 0;
  const uint8_t *json = input_json;
  uint32_t json_bytes = input_bytes;
  SecureZeroMemory(current_key, sizeof(current_key));
  SecureZeroMemory(compare_key, sizeof(compare_key));
  if (output_bytes != NULL)
    *output_bytes = 0u;
  if (output_json == NULL || output_bytes == NULL ||
      (has_return_url != 0u &&
       return_url_utf8 == NULL && return_url_bytes != 0u) ||
      (has_alert != 0u && alert_utf8 == NULL && alert_bytes != 0u))
    goto cleanup;
  if (json == NULL || json_bytes == 0u) {
    json = empty_object;
    json_bytes = sizeof(empty_object) - 1u;
  }
  if (!al_campfire_json_root_object_valid(json, json_bytes) ||
      json_bytes > AL_CAMPFIRE_MAX_JSON_BYTES)
    goto cleanup;
  parser.bytes = json;
  parser.length = json_bytes;
  parser.index = 0u;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'{')
    goto cleanup;
  ++parser.index;
  result = 2;
  if (!al_campfire_append_bytes(output_json, output_capacity, &used,
                                &root_open, 1u))
    goto cleanup;
  al_campfire_json_skip_whitespace(&parser);
  while (parser.index < parser.length &&
         parser.bytes[parser.index] != (uint8_t)'}') {
    const uint32_t key_start = parser.index;
    uint32_t key_end;
    uint32_t value_start;
    uint32_t value_end;
    uint32_t ignored_ascii_length = 0u;
    int32_t is_return_key = 0;
    int32_t is_flash_key = 0;
    int32_t prior_match;
    uint32_t index;
    if (!al_campfire_json_parse_string(
            &parser, current_key, AL_CAMPFIRE_TOKEN_UNITS,
            &current_key_units, NULL, 0u, &ignored_ascii_length, NULL,
            return_key, (uint32_t)(sizeof(return_key) - 1u),
            &is_return_key))
      goto malformed;
    {
      al_campfire_json_parser key_match_parser;
      key_match_parser.bytes = json;
      key_match_parser.length = json_bytes;
      key_match_parser.index = key_start;
      if (!al_campfire_json_parse_string(
              &key_match_parser, NULL, 0u, NULL, NULL, 0u, NULL, NULL,
              flash_key, (uint32_t)(sizeof(flash_key) - 1u),
              &is_flash_key))
        goto malformed;
    }
    prior_match = al_campfire_json_prior_key_matches(
        json, json_bytes, key_start, current_key, current_key_units,
        compare_key, AL_CAMPFIRE_TOKEN_UNITS);
    if (prior_match != 0) {
      result = prior_match > 0 ? 4 : 3;
      goto cleanup;
    }
    key_end = parser.index;
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)':')
      goto malformed;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
    value_start = parser.index;
    if (!al_campfire_json_parse_value(&parser, 1u))
      goto malformed;
    value_end = parser.index;
    if (is_return_key && has_return_url != 0u) {
      if (!al_campfire_json_append_member_prefix(
              output_json, output_capacity, &used, &property_count,
              return_property_name,
              (uint32_t)(sizeof(return_property_name) - 1u)) ||
          !al_campfire_utf8_json_string(
              return_url_utf8, return_url_bytes, output_json + used,
              output_capacity - used, &index))
        goto cleanup;
      used += index;
      saw_return = 1u;
    } else if (is_flash_key && has_alert != 0u) {
      if (!al_campfire_json_append_member_prefix(
              output_json, output_capacity, &used, &property_count,
              flash_property_name,
              (uint32_t)(sizeof(flash_property_name) - 1u)) ||
          !al_campfire_json_append_alert_value(
              output_json, output_capacity, &used, alert_utf8, alert_bytes))
        goto cleanup;
      saw_flash = 1u;
    } else {
      static const uint8_t copied_colon = ':';
      if (property_count != 0u &&
          !al_campfire_append_bytes(output_json, output_capacity, &used,
                                    &comma, 1u))
        goto cleanup;
      if (!al_campfire_append_bytes(output_json, output_capacity, &used,
                                    json + key_start, key_end - key_start) ||
          !al_campfire_append_bytes(output_json, output_capacity, &used,
                                    &copied_colon, 1u) ||
          !al_campfire_append_bytes(output_json, output_capacity, &used,
                                    json + value_start,
                                    value_end - value_start))
        goto cleanup;
      ++property_count;
    }
    al_campfire_json_skip_whitespace(&parser);
    if (parser.index < parser.length &&
        parser.bytes[parser.index] == (uint8_t)'}')
      break;
    if (parser.index >= parser.length ||
        parser.bytes[parser.index] != (uint8_t)',')
      goto malformed;
    ++parser.index;
    al_campfire_json_skip_whitespace(&parser);
  }
  if (parser.index >= parser.length ||
      parser.bytes[parser.index] != (uint8_t)'}')
    goto malformed;
  ++parser.index;
  al_campfire_json_skip_whitespace(&parser);
  if (parser.index != parser.length)
    goto malformed;
  if (has_return_url != 0u && saw_return == 0u) {
    uint32_t encoded_bytes = 0u;
    if (!al_campfire_json_append_member_prefix(
            output_json, output_capacity, &used, &property_count,
            return_property_name,
            (uint32_t)(sizeof(return_property_name) - 1u)) ||
        !al_campfire_utf8_json_string(
            return_url_utf8, return_url_bytes, output_json + used,
            output_capacity - used, &encoded_bytes))
      goto cleanup;
    used += encoded_bytes;
  }
  if (has_alert != 0u && saw_flash == 0u) {
    if (!al_campfire_json_append_member_prefix(
            output_json, output_capacity, &used, &property_count,
            flash_property_name,
            (uint32_t)(sizeof(flash_property_name) - 1u)) ||
        !al_campfire_json_append_alert_value(
            output_json, output_capacity, &used, alert_utf8, alert_bytes))
      goto cleanup;
  }
  if (!al_campfire_append_bytes(output_json, output_capacity, &used,
                                &root_close, 1u))
    goto cleanup;
  *output_bytes = used;
  result = 1;
  goto cleanup;

malformed:
  result = 3;

cleanup:
  SecureZeroMemory(current_key, sizeof(current_key));
  SecureZeroMemory(compare_key, sizeof(compare_key));
  return result;
}

int32_t al_campfire_cookie_response_finalize_session_v1(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    uint32_t has_return_url, const uint8_t *return_url_utf8,
    uint32_t return_url_bytes, uint32_t has_alert,
    const uint8_t *alert_utf8, uint32_t alert_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes) {
  static const uint8_t empty_object[] = "{}";
  uint8_t session_json[AL_CAMPFIRE_MAX_JSON_BYTES];
  uint8_t mutated_json[AL_CAMPFIRE_MAX_JSON_BYTES];
  uint32_t session_json_bytes = 0u;
  uint32_t mutated_json_bytes = 0u;
  al_campfire_pure_context *context =
      (al_campfire_pure_context *)response_context;
  int32_t mutation_result;
  int32_t status = 3;
  if (output_bytes != NULL)
    *output_bytes = 0u;
  SecureZeroMemory(session_json, sizeof(session_json));
  SecureZeroMemory(mutated_json, sizeof(mutated_json));
  if (context == NULL ||
      context->magic != AL_CAMPFIRE_COOKIE_RESPONSE_CONTEXT_MAGIC ||
      output == NULL || output_bytes == NULL || has_return_url > 1u ||
      has_alert > 1u || (raw_cookie_utf8 == NULL && cookie_bytes != 0u) ||
      (has_return_url == 0u &&
       (return_url_utf8 != NULL || return_url_bytes != 0u)) ||
      (has_alert == 0u && (alert_utf8 != NULL || alert_bytes != 0u)) ||
      return_url_bytes > AL_CAMPFIRE_MAX_JSON_BYTES ||
      alert_bytes > AL_CAMPFIRE_MAX_JSON_BYTES ||
      !al_campfire_utf8_span_valid(return_url_utf8,
                                   has_return_url ? return_url_bytes : 0u) ||
      !al_campfire_utf8_span_valid(alert_utf8,
                                   has_alert ? alert_bytes : 0u) ||
      now_unix_microseconds < INT64_C(-62135596800000000) ||
      now_unix_microseconds > INT64_C(253402300799999999)) {
    status = 1;
    goto cleanup;
  }
  if (cookie_bytes != 0u) {
    const int32_t decrypt_status =
        al_campfire_cookie_response_decrypt_session_json_v1(
            response_context, raw_cookie_utf8, cookie_bytes,
            now_unix_microseconds, session_json, sizeof(session_json),
            &session_json_bytes);
    if (decrypt_status != 0) {
      SecureZeroMemory(session_json, sizeof(session_json));
      memcpy(session_json, empty_object, sizeof(empty_object) - 1u);
      session_json_bytes = (uint32_t)(sizeof(empty_object) - 1u);
    }
  } else {
    memcpy(session_json, empty_object, sizeof(empty_object) - 1u);
    session_json_bytes = (uint32_t)(sizeof(empty_object) - 1u);
  }
  mutation_result = al_campfire_json_mutate_session(
      session_json, session_json_bytes, has_return_url, return_url_utf8,
      return_url_bytes, has_alert, alert_utf8, alert_bytes, mutated_json,
      sizeof(mutated_json), &mutated_json_bytes);
  if (mutation_result != 1) {
    status = mutation_result == 4 ? 4 : mutation_result == 2 ? 2 : 3;
    goto cleanup;
  }
  status = al_campfire_cookie_response_encrypt_session_json_v1(
      response_context, mutated_json, mutated_json_bytes, now_unix_microseconds,
      output, output_capacity_bytes, output_bytes);

cleanup:
  SecureZeroMemory(session_json, sizeof(session_json));
  SecureZeroMemory(mutated_json, sizeof(mutated_json));
  return status;
}

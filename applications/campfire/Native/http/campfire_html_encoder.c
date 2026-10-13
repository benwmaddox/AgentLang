#include "campfire_html_encoder.h"

#include <stdint.h>

static uint32_t al_html_next_scalar(const uint16_t *input,
                                    uint32_t input_code_units,
                                    uint32_t *index) {
  const uint32_t first = input[*index];
  ++*index;

  if (first >= UINT32_C(0xd800) && first <= UINT32_C(0xdbff)) {
    if (*index < input_code_units) {
      const uint32_t second = input[*index];
      if (second >= UINT32_C(0xdc00) && second <= UINT32_C(0xdfff)) {
        ++*index;
        return UINT32_C(0x10000) + ((first - UINT32_C(0xd800)) << 10u) +
               (second - UINT32_C(0xdc00));
      }
    }
    return UINT32_C(0xfffd);
  }

  if (first >= UINT32_C(0xdc00) && first <= UINT32_C(0xdfff))
    return UINT32_C(0xfffd);
  return first;
}

static uint32_t al_html_numeric_entity_size(uint32_t scalar) {
  uint32_t digits = 1u;
  while (scalar >= 16u) {
    scalar >>= 4u;
    ++digits;
  }
  return 4u + digits;
}

static uint32_t al_html_scalar_output_size(uint32_t scalar) {
  switch (scalar) {
  case (uint32_t)'<':
  case (uint32_t)'>':
    return 4u;
  case (uint32_t)'&':
    return 5u;
  case (uint32_t)'"':
    return 6u;
  default:
    break;
  }

  if (scalar >= UINT32_C(0x20) && scalar <= UINT32_C(0x7e) &&
      scalar != (uint32_t)'\'' && scalar != (uint32_t)'+')
    return 1u;
  return al_html_numeric_entity_size(scalar);
}

static int al_html_measure(const uint16_t *input, uint32_t input_code_units,
                           uint32_t *out_required_bytes) {
  uint64_t total = 0u;
  uint32_t index = 0u;
  while (index < input_code_units) {
    const uint32_t scalar =
        al_html_next_scalar(input, input_code_units, &index);
    total += al_html_scalar_output_size(scalar);
    if (total > UINT32_MAX)
      return 0;
  }
  *out_required_bytes = (uint32_t)total;
  return 1;
}

static void al_html_write_numeric_entity(uint32_t scalar, uint8_t *output,
                                         uint32_t *offset) {
  static const uint8_t hex[] = "0123456789ABCDEF";
  uint32_t digits = al_html_numeric_entity_size(scalar) - 4u;
  uint32_t index;

  output[*offset] = (uint8_t)'&';
  output[*offset + 1u] = (uint8_t)'#';
  output[*offset + 2u] = (uint8_t)'x';
  for (index = digits; index > 0u; --index) {
    output[*offset + 2u + index] = hex[scalar & 0x0fu];
    scalar >>= 4u;
  }
  output[*offset + 3u + digits] = (uint8_t)';';
  *offset += 4u + digits;
}

static void al_html_write_scalar(uint32_t scalar, uint8_t *output,
                                 uint32_t *offset) {
  const uint8_t *entity = NULL;
  uint32_t entity_length = 0u;
  switch (scalar) {
  case (uint32_t)'<':
    entity = (const uint8_t *)"&lt;";
    entity_length = 4u;
    break;
  case (uint32_t)'>':
    entity = (const uint8_t *)"&gt;";
    entity_length = 4u;
    break;
  case (uint32_t)'&':
    entity = (const uint8_t *)"&amp;";
    entity_length = 5u;
    break;
  case (uint32_t)'"':
    entity = (const uint8_t *)"&quot;";
    entity_length = 6u;
    break;
  default:
    break;
  }

  if (entity != NULL) {
    uint32_t index;
    for (index = 0u; index < entity_length; ++index)
      output[*offset + index] = entity[index];
    *offset += entity_length;
    return;
  }

  if (scalar >= UINT32_C(0x20) && scalar <= UINT32_C(0x7e) &&
      scalar != (uint32_t)'\'' && scalar != (uint32_t)'+') {
    output[*offset] = (uint8_t)scalar;
    ++*offset;
    return;
  }

  al_html_write_numeric_entity(scalar, output, offset);
}

static void al_html_write(const uint16_t *input, uint32_t input_code_units,
                          uint8_t *output) {
  uint32_t index = 0u;
  uint32_t offset = 0u;
  while (index < input_code_units) {
    const uint32_t scalar =
        al_html_next_scalar(input, input_code_units, &index);
    al_html_write_scalar(scalar, output, &offset);
  }
}

int32_t al_campfire_html_encode_utf8_v1(const uint16_t *input_utf16,
                                        uint32_t input_code_units,
                                        uint8_t *output_utf8,
                                        uint32_t output_capacity_bytes,
                                        uint32_t *out_required_bytes) {
  uint32_t required_bytes = 0u;

  if (out_required_bytes == NULL)
    return AL_CAMPFIRE_HTML_ENCODE_INVALID_ARGUMENT;
  *out_required_bytes = 0u;
  if ((input_code_units != 0u && input_utf16 == NULL) ||
      (output_capacity_bytes != 0u && output_utf8 == NULL))
    return AL_CAMPFIRE_HTML_ENCODE_INVALID_ARGUMENT;

  if (!al_html_measure(input_utf16, input_code_units, &required_bytes))
    return AL_CAMPFIRE_HTML_ENCODE_LENGTH_OVERFLOW;

  if (required_bytes > output_capacity_bytes) {
    *out_required_bytes = required_bytes;
    return AL_CAMPFIRE_HTML_ENCODE_OUTPUT_TOO_SMALL;
  }
  if (required_bytes == 0u) {
    *out_required_bytes = 0u;
    return AL_CAMPFIRE_HTML_ENCODE_OK;
  }
  if (output_utf8 == NULL)
    return AL_CAMPFIRE_HTML_ENCODE_INVALID_ARGUMENT;

  al_html_write(input_utf16, input_code_units, output_utf8);
  *out_required_bytes = required_bytes;
  return AL_CAMPFIRE_HTML_ENCODE_OK;
}

#ifndef AGENTLANG_OWNING_FLOAT_FORMAT_H
#define AGENTLANG_OWNING_FLOAT_FORMAT_H

#include <stdint.h>

/*
 * This is a compact Dragon4-style shortest-decimal conversion.  It builds an
 * exact rational value and the two adjacent rounding boundaries with a common
 * factor of four, then emits decimal digits until the remaining value is
 * inside one or both boundaries.  A normal power of two above the smallest
 * normal has a quarter-ULP lower boundary and a half-ULP upper boundary; all
 * other finite values have symmetric half-ULP boundaries.  Boundary equality
 * is accepted only for an even binary significand, matching round-to-nearest,
 * ties-to-even.  If both decimal choices are valid, the nearer one is used,
 * with a decimal tie resolved by the final decimal digit's parity.
 *
 * The fixed 40-limb workspace holds 1280 bits.  The largest exact rational
 * component is below 1140 bits; its four values use 640 bytes, plus one small
 * 160-byte comparison temporary.  Decimal normalization is bounded to 324
 * iterations and digit generation to 17 digits.  There is no floating-point
 * arithmetic, allocation, libc formatter, parser, or external dependency.
 */
#define AL_OWNING_FF_LIMBS 40u
#define AL_OWNING_FF_MAX_DIGITS 17u
#define AL_OWNING_FF_MAX_NORMALIZE 324u

typedef struct al_owning_ff_big {
  uint32_t limb[AL_OWNING_FF_LIMBS];
} al_owning_ff_big;

static void al_owning_ff_zero(al_owning_ff_big *value) {
  volatile uint32_t *limbs = value->limb;
  uint32_t index;
  for (index = 0u; index < AL_OWNING_FF_LIMBS; ++index)
    limbs[index] = 0u;
}

static void al_owning_ff_set_u64(al_owning_ff_big *value, uint64_t number) {
  al_owning_ff_zero(value);
  value->limb[0] = (uint32_t)number;
  value->limb[1] = (uint32_t)(number >> 32u);
}

static void al_owning_ff_copy(al_owning_ff_big *destination,
                              const al_owning_ff_big *source) {
  volatile uint32_t *destination_limbs = destination->limb;
  const volatile uint32_t *source_limbs = source->limb;
  uint32_t index;
  for (index = 0u; index < AL_OWNING_FF_LIMBS; ++index)
    destination_limbs[index] = source_limbs[index];
}

static void al_owning_ff_copy_bytes(uint8_t *destination,
                                    const uint8_t *source,
                                    uint32_t byte_count) {
  volatile uint8_t *destination_bytes = destination;
  const volatile uint8_t *source_bytes = source;
  uint32_t index;
  for (index = 0u; index < byte_count; ++index)
    destination_bytes[index] = source_bytes[index];
}

static int32_t al_owning_ff_compare(const al_owning_ff_big *left,
                                    const al_owning_ff_big *right) {
  uint32_t index = AL_OWNING_FF_LIMBS;
  while (index > 0u) {
    --index;
    if (left->limb[index] < right->limb[index])
      return -1;
    if (left->limb[index] > right->limb[index])
      return 1;
  }
  return 0;
}

static int32_t al_owning_ff_shift_left(al_owning_ff_big *value,
                                       uint32_t bit_count) {
  while (bit_count > 0u) {
    uint32_t index;
    uint32_t carry = 0u;
    for (index = 0u; index < AL_OWNING_FF_LIMBS; ++index) {
      const uint32_t next = value->limb[index] >> 31u;
      value->limb[index] = (value->limb[index] << 1u) | carry;
      carry = next;
    }
    if (carry != 0u)
      return 0;
    --bit_count;
  }
  return 1;
}

static int32_t al_owning_ff_multiply_small(al_owning_ff_big *value,
                                           uint32_t multiplier) {
  uint32_t index;
  uint64_t carry = 0u;
  for (index = 0u; index < AL_OWNING_FF_LIMBS; ++index) {
    const uint64_t product = (uint64_t)value->limb[index] * multiplier + carry;
    value->limb[index] = (uint32_t)product;
    carry = product >> 32u;
  }
  return carry == 0u;
}

/* Subtract right from left, which must be at least as large. */
static int32_t al_owning_ff_subtract(al_owning_ff_big *left,
                                     const al_owning_ff_big *right) {
  uint32_t index;
  uint64_t borrow = 0u;
  for (index = 0u; index < AL_OWNING_FF_LIMBS; ++index) {
    const uint64_t subtrahend = (uint64_t)right->limb[index] + borrow;
    const uint64_t minuend = left->limb[index];
    if (minuend < subtrahend) {
      left->limb[index] = (uint32_t)((UINT64_C(1) << 32u) + minuend -
                                     subtrahend);
      borrow = 1u;
    } else {
      left->limb[index] = (uint32_t)(minuend - subtrahend);
      borrow = 0u;
    }
  }
  return borrow == 0u;
}

static int32_t al_owning_ff_append(uint8_t *buffer, uint32_t *length,
                                  uint8_t character) {
  if (*length >= AL_OWNING_FLOAT_ASCII_CAPACITY)
    return 0;
  buffer[*length] = character;
  ++*length;
  return 1;
}

static int32_t al_owning_ff_append_zeros(uint8_t *buffer, uint32_t *length,
                                         uint32_t count) {
  while (count > 0u) {
    if (!al_owning_ff_append(buffer, length, (uint8_t)'0'))
      return 0;
    --count;
  }
  return 1;
}

/* Plain notation covers adjusted exponents -4 through 16.  Other values use
 * uppercase E with an explicit sign and at least two exponent digits.  Plain
 * integral values retain a decimal suffix so every result is visibly Float. */
static int32_t al_owning_ff_build_text(const uint8_t *digits,
                                      uint32_t digit_count,
                                      int32_t decimal_exponent,
                                      uint8_t *buffer,
                                      uint32_t *out_length) {
  uint32_t length = 0u;
  uint32_t index;

  if (digit_count == 0u || digit_count > AL_OWNING_FF_MAX_DIGITS)
    return 0;

  if (decimal_exponent >= -4 && decimal_exponent <= 16) {
    const int32_t point = decimal_exponent + 1;
    if (point <= 0) {
      if (!al_owning_ff_append(buffer, &length, (uint8_t)'0') ||
          !al_owning_ff_append(buffer, &length, (uint8_t)'.') ||
          !al_owning_ff_append_zeros(buffer, &length,
                                     (uint32_t)(-point)))
        return 0;
      for (index = 0u; index < digit_count; ++index) {
        if (!al_owning_ff_append(buffer, &length, digits[index]))
          return 0;
      }
    } else {
      const uint32_t integer_digits = (uint32_t)point;
      const uint32_t before = integer_digits < digit_count ? integer_digits
                                                            : digit_count;
      for (index = 0u; index < before; ++index) {
        if (!al_owning_ff_append(buffer, &length, digits[index]))
          return 0;
      }
      if (integer_digits > digit_count &&
          !al_owning_ff_append_zeros(buffer, &length,
                                     integer_digits - digit_count))
        return 0;
      if (integer_digits < digit_count) {
        if (!al_owning_ff_append(buffer, &length, (uint8_t)'.'))
          return 0;
        for (index = integer_digits; index < digit_count; ++index) {
          if (!al_owning_ff_append(buffer, &length, digits[index]))
            return 0;
        }
      }
    }
  } else {
    int32_t magnitude = decimal_exponent;
    uint32_t exponent_digits;
    uint8_t reversed[3];
    uint32_t reversed_count = 0u;
    if (!al_owning_ff_append(buffer, &length, digits[0]))
      return 0;
    if (digit_count > 1u) {
      if (!al_owning_ff_append(buffer, &length, (uint8_t)'.'))
        return 0;
      for (index = 1u; index < digit_count; ++index) {
        if (!al_owning_ff_append(buffer, &length, digits[index]))
          return 0;
      }
    }
    if (!al_owning_ff_append(buffer, &length, (uint8_t)'E'))
      return 0;
    if (magnitude < 0) {
      if (!al_owning_ff_append(buffer, &length, (uint8_t)'-'))
        return 0;
      magnitude = -magnitude;
    } else if (!al_owning_ff_append(buffer, &length, (uint8_t)'+')) {
      return 0;
    }
    do {
      reversed[reversed_count++] = (uint8_t)('0' + magnitude % 10);
      magnitude /= 10;
    } while (magnitude > 0 && reversed_count < 3u);
    if (magnitude != 0)
      return 0;
    exponent_digits = reversed_count;
    if (exponent_digits < 2u &&
        !al_owning_ff_append(buffer, &length, (uint8_t)'0'))
      return 0;
    while (reversed_count > 0u) {
      --reversed_count;
      if (!al_owning_ff_append(buffer, &length,
                               reversed[reversed_count]))
        return 0;
    }
  }

  {
    int32_t has_decimal_or_exponent = 0;
    for (index = 0u; index < length; ++index) {
      if (buffer[index] == (uint8_t)'.' || buffer[index] == (uint8_t)'E') {
        has_decimal_or_exponent = 1;
        break;
      }
    }
    if (!has_decimal_or_exponent &&
        (!al_owning_ff_append(buffer, &length, (uint8_t)'.') ||
         !al_owning_ff_append(buffer, &length, (uint8_t)'0')))
      return 0;
  }

  *out_length = length;
  return 1;
}

/* Returns 1 on success and leaves both outputs untouched on failure. */
static int32_t al_owning_float_format_ascii(uint64_t bits,
                                            uint8_t *ascii32,
                                            uint32_t *out_length) {
  al_owning_ff_big remainder;
  al_owning_ff_big denominator;
  al_owning_ff_big lower_margin;
  al_owning_ff_big upper_margin;
  al_owning_ff_big temporary;
  uint8_t digits[AL_OWNING_FF_MAX_DIGITS];
  uint8_t result[AL_OWNING_FLOAT_ASCII_CAPACITY];
  uint32_t digit_count = 0u;
  uint32_t normalization_steps = 0u;
  uint32_t result_length = 0u;
  uint64_t fraction = bits & UINT64_C(0x000fffffffffffff);
  uint32_t exponent_field = (uint32_t)((bits >> 52u) & UINT64_C(0x7ff));
  uint64_t mantissa;
  int32_t binary_exponent;
  int32_t decimal_exponent = 0;
  int32_t inclusive_boundaries;
  int32_t stopped = 0;
  int32_t round_up = 0;
  int32_t negative = (bits >> 63u) != 0u;
  uint32_t index;

  if (ascii32 == 0 || out_length == 0 || exponent_field == 0x7ffu)
    return 0;

  if (exponent_field == 0u) {
    mantissa = fraction;
    binary_exponent = -1074;
    if (mantissa == 0u) {
      result[0] = negative ? (uint8_t)'-' : (uint8_t)'0';
      if (negative) {
        result[1] = (uint8_t)'0';
        result[2] = (uint8_t)'.';
        result[3] = (uint8_t)'0';
        result_length = 4u;
      } else {
        result[1] = (uint8_t)'.';
        result[2] = (uint8_t)'0';
        result_length = 3u;
      }
      al_owning_ff_copy_bytes(ascii32, result, result_length);
      *out_length = result_length;
      return 1;
    }
  } else {
    mantissa = UINT64_C(0x0010000000000000) | fraction;
    binary_exponent = (int32_t)exponent_field - 1075;
  }

  inclusive_boundaries = (mantissa & 1u) == 0u;

  al_owning_ff_set_u64(&remainder, mantissa * UINT64_C(4));
  al_owning_ff_set_u64(&denominator, UINT64_C(4));
  al_owning_ff_set_u64(&lower_margin,
                       (exponent_field > 1u && fraction == 0u) ? 1u : 2u);
  al_owning_ff_set_u64(&upper_margin, UINT64_C(2));

  if (binary_exponent < 0) {
    if (!al_owning_ff_shift_left(&denominator,
                                 (uint32_t)(-binary_exponent)))
      return 0;
  } else {
    if (!al_owning_ff_shift_left(&remainder,
                                 (uint32_t)binary_exponent) ||
        !al_owning_ff_shift_left(&lower_margin,
                                 (uint32_t)binary_exponent) ||
        !al_owning_ff_shift_left(&upper_margin,
                                 (uint32_t)binary_exponent))
      return 0;
  }

  while (al_owning_ff_compare(&remainder, &denominator) < 0) {
    if (normalization_steps >= AL_OWNING_FF_MAX_NORMALIZE ||
        !al_owning_ff_multiply_small(&remainder, 10u) ||
        !al_owning_ff_multiply_small(&lower_margin, 10u) ||
        !al_owning_ff_multiply_small(&upper_margin, 10u) ||
        decimal_exponent <= -(int32_t)AL_OWNING_FF_MAX_NORMALIZE)
      return 0;
    --decimal_exponent;
    ++normalization_steps;
  }

  for (;;) {
    al_owning_ff_copy(&temporary, &denominator);
    if (!al_owning_ff_multiply_small(&temporary, 10u))
      return 0;
    if (al_owning_ff_compare(&remainder, &temporary) < 0)
      break;
    if (normalization_steps >= AL_OWNING_FF_MAX_NORMALIZE ||
        decimal_exponent >= (int32_t)AL_OWNING_FF_MAX_NORMALIZE)
      return 0;
    al_owning_ff_copy(&denominator, &temporary);
    ++decimal_exponent;
    ++normalization_steps;
  }

  while (digit_count < AL_OWNING_FF_MAX_DIGITS) {
    uint32_t digit = 0u;
    int32_t lower_comparison;
    int32_t upper_comparison;
    int32_t low;
    int32_t high;

    while (al_owning_ff_compare(&remainder, &denominator) >= 0) {
      if (digit >= 9u || !al_owning_ff_subtract(&remainder, &denominator))
        return 0;
      ++digit;
    }
    digits[digit_count++] = (uint8_t)('0' + digit);

    lower_comparison = al_owning_ff_compare(&remainder, &lower_margin);
    low = lower_comparison < 0 ||
          (lower_comparison == 0 && inclusive_boundaries);

    al_owning_ff_copy(&temporary, &denominator);
    if (!al_owning_ff_subtract(&temporary, &remainder))
      return 0;
    upper_comparison = al_owning_ff_compare(&temporary, &upper_margin);
    high = upper_comparison < 0 ||
           (upper_comparison == 0 && inclusive_boundaries);

    if (low || high) {
      stopped = 1;
      if (low && high) {
        al_owning_ff_copy(&temporary, &remainder);
        if (!al_owning_ff_multiply_small(&temporary, 2u))
          return 0;
        lower_comparison = al_owning_ff_compare(&temporary, &denominator);
        round_up = lower_comparison > 0 ||
                   (lower_comparison == 0 &&
                    ((digits[digit_count - 1u] - (uint8_t)'0') & 1u) != 0u);
      } else {
        round_up = high;
      }
      break;
    }

    if (digit_count >= AL_OWNING_FF_MAX_DIGITS ||
        !al_owning_ff_multiply_small(&remainder, 10u) ||
        !al_owning_ff_multiply_small(&lower_margin, 10u) ||
        !al_owning_ff_multiply_small(&upper_margin, 10u))
      return 0;
  }

  if (!stopped || digit_count == 0u)
    return 0;

  if (round_up) {
    uint32_t carry_index = digit_count;
    while (carry_index > 0u && digits[carry_index - 1u] == (uint8_t)'9') {
      digits[carry_index - 1u] = (uint8_t)'0';
      --carry_index;
    }
    if (carry_index == 0u) {
      digits[0] = (uint8_t)'1';
      if (decimal_exponent >= 308)
        return 0;
      ++decimal_exponent;
    } else {
      ++digits[carry_index - 1u];
    }
  }

  while (digit_count > 1u && digits[digit_count - 1u] == (uint8_t)'0')
    --digit_count;

  if (!al_owning_ff_build_text(digits, digit_count, decimal_exponent, result,
                               &result_length) ||
      result_length == 0u || result_length > AL_OWNING_FLOAT_ASCII_CAPACITY)
    return 0;

  if (negative) {
    volatile uint8_t *result_bytes = result;
    const volatile uint8_t *source_bytes = result;
    if (result_length >= AL_OWNING_FLOAT_ASCII_CAPACITY)
      return 0;
    for (index = result_length; index > 0u; --index)
      result_bytes[index] = source_bytes[index - 1u];
    result[0] = (uint8_t)'-';
    ++result_length;
  }

  al_owning_ff_copy_bytes(ascii32, result, result_length);
  *out_length = result_length;
  return 1;
}

#endif

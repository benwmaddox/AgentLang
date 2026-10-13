#include "cookie_expiry.h"

#define CF_MICROSECONDS_PER_DAY INT64_C(86400000000)
#define CF_DAYS_UNIX_EPOCH_ORDINAL UINT32_C(719162)
#define CF_DOTNET_MIN_UNIX_MICROSECONDS INT64_C(-62135596800000000)
#define CF_DOTNET_MAX_UNIX_MICROSECONDS INT64_C(253402300799999999)
#define CF_COOKIE_EXPIRY_BYTES UINT32_C(29)

static uint32_t days_before_year(uint32_t year) {
  const uint32_t prior = year - 1u;
  return 365u * prior + prior / 4u - prior / 100u + prior / 400u;
}

static int is_leap_year(uint32_t year) {
  return (year % 4u == 0u) && ((year % 100u != 0u) || (year % 400u == 0u));
}

static uint32_t days_in_month(uint32_t year, uint32_t month) {
  static const uint8_t month_lengths[12] = {
      31u, 28u, 31u, 30u, 31u, 30u,
      31u, 31u, 30u, 31u, 30u, 31u};
  if (month == 2u && is_leap_year(year)) return 29u;
  return month_lengths[month - 1u];
}

static uint32_t year_for_ordinal(uint32_t ordinal) {
  uint32_t low = 1u;
  uint32_t high = 10000u;
  while (low + 1u < high) {
    const uint32_t middle = low + (high - low) / 2u;
    if (days_before_year(middle) <= ordinal)
      low = middle;
    else
      high = middle;
  }
  return low;
}

static uint32_t month_day_for_ordinal(uint32_t year, uint32_t day_of_year,
                                     uint32_t *month_out) {
  uint32_t month = 1u;
  while (month < 12u) {
    const uint32_t length = days_in_month(year, month);
    if (day_of_year < length) break;
    day_of_year -= length;
    ++month;
  }
  *month_out = month;
  return day_of_year + 1u;
}

static void write_two_digits(uint8_t *output, uint32_t *index,
                             uint32_t value) {
  output[(*index)++] = (uint8_t)('0' + value / 10u);
  output[(*index)++] = (uint8_t)('0' + value % 10u);
}

static void write_four_digits(uint8_t *output, uint32_t *index,
                              uint32_t value) {
  output[(*index)++] = (uint8_t)('0' + value / 1000u);
  output[(*index)++] = (uint8_t)('0' + (value / 100u) % 10u);
  output[(*index)++] = (uint8_t)('0' + (value / 10u) % 10u);
  output[(*index)++] = (uint8_t)('0' + value % 10u);
}

int32_t al_campfire_cookie_expiry_20_years_v1(
    int64_t now_unix_microseconds, uint8_t *output, uint32_t capacity,
    uint32_t *written) {
  static const char *const weekdays[7] = {
      "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"};
  static const char *const months[12] = {
      "Jan", "Feb", "Mar", "Apr", "May", "Jun",
      "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
  uint8_t formatted[CF_COOKIE_EXPIRY_BYTES];
  int64_t whole_days, day_microseconds, ordinal_signed;
  uint32_t year, target_year, day_of_year, month, day;
  uint32_t target_ordinal, weekday, hour, minute, second, index = 0u;
  uint32_t month_index;

  if (output == NULL || written == NULL || capacity < CF_COOKIE_EXPIRY_BYTES)
    return 0;
  if (now_unix_microseconds < CF_DOTNET_MIN_UNIX_MICROSECONDS ||
      now_unix_microseconds > CF_DOTNET_MAX_UNIX_MICROSECONDS)
    return 0;

  whole_days = now_unix_microseconds / CF_MICROSECONDS_PER_DAY;
  if (now_unix_microseconds % CF_MICROSECONDS_PER_DAY < 0) --whole_days;
  day_microseconds =
      now_unix_microseconds - whole_days * CF_MICROSECONDS_PER_DAY;
  ordinal_signed = whole_days + (int64_t)CF_DAYS_UNIX_EPOCH_ORDINAL;
  if (ordinal_signed < 0 || ordinal_signed > INT32_MAX) return 0;

  year = year_for_ordinal((uint32_t)ordinal_signed);
  if (year > 9979u) return 0;
  day_of_year = (uint32_t)ordinal_signed - days_before_year(year);
  day = month_day_for_ordinal(year, day_of_year, &month);
  target_year = year + 20u;
  if (month == 2u && day == 29u && !is_leap_year(target_year)) day = 28u;

  target_ordinal = days_before_year(target_year);
  for (month_index = 1u; month_index < month; ++month_index)
    target_ordinal += days_in_month(target_year, month_index);
  target_ordinal += day - 1u;
  weekday = (target_ordinal + 1u) % 7u;

  hour = (uint32_t)(day_microseconds / INT64_C(3600000000));
  minute = (uint32_t)((day_microseconds / INT64_C(60000000)) % 60);
  second = (uint32_t)((day_microseconds / INT64_C(1000000)) % 60);

  for (month_index = 0u; month_index < 3u; ++month_index)
    formatted[index++] = (uint8_t)weekdays[weekday][month_index];
  formatted[index++] = ',';
  formatted[index++] = ' ';
  write_two_digits(formatted, &index, day);
  formatted[index++] = ' ';
  for (month_index = 0u; month_index < 3u; ++month_index)
    formatted[index++] = (uint8_t)months[month - 1u][month_index];
  formatted[index++] = ' ';
  write_four_digits(formatted, &index, target_year);
  formatted[index++] = ' ';
  write_two_digits(formatted, &index, hour);
  formatted[index++] = ':';
  write_two_digits(formatted, &index, minute);
  formatted[index++] = ':';
  write_two_digits(formatted, &index, second);
  formatted[index++] = ' ';
  formatted[index++] = 'G';
  formatted[index++] = 'M';
  formatted[index++] = 'T';

  if (index != CF_COOKIE_EXPIRY_BYTES) return 0;
  for (index = 0u; index < CF_COOKIE_EXPIRY_BYTES; ++index)
    output[index] = formatted[index];
  *written = CF_COOKIE_EXPIRY_BYTES;
  return 1;
}

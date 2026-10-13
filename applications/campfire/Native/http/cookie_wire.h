#ifndef CF_COOKIE_WIRE_H
#define CF_COOKIE_WIRE_H
#include <stdint.h>
/* Narrow output adapter for native Rails crypto values and numeric last_room.
 * Those producers emit ASCII; arbitrary UTF8 input is deliberately rejected.
 * Non-overlapping borrowed input/output/required storage; no heap or NUL suffix.
 * 0 success, 1 invalid arguments/nonASCII, 2 capacity, 3 raw cookie limit.
 * Failures leave output unchanged. Capacity failure reports exact required size. */
int cf_cookie_escape_ascii(const uint8_t *raw, uint32_t length,
                           uint8_t *wire, uint32_t capacity, uint32_t *required);
#endif

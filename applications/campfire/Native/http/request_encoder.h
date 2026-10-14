#ifndef CAMPFIRE_REQUEST_ENCODER_H
#define CAMPFIRE_REQUEST_ENCODER_H
#include <stdint.h>
typedef struct cf_text { const uint16_t *data; uint32_t length; } cf_text;
typedef struct cf_optional_text { uint32_t present; cf_text value; } cf_optional_text;
/* Closed CampfireRoomReadV1 input, owning layout ABI5. Enum tags must be
 * checked against the compiler-minted profile before host admission. */
typedef struct cf_room_request {
 uint32_t method; /* RequestMethod: get=0, head=1; HTTP adapter has no write route. */
 int64_t room_id;
 int64_t now_unix_microseconds;
 cf_text remote_ip, base_url;
 cf_optional_text user_agent, origin, fetch_site, session_token, bot_key, last_room;
 uint32_t request_is_ssl, force_ssl, accepts_turbo, accepts_html;
 cf_optional_text csrf_token; /* the Rails session's _csrf_token (report 271) */
} cf_room_request;
/* Returns 0 on success, 1 invalid input, 2 insufficient output capacity.
 * Measurement (output=NULL, capacity=0) returns 2 and exact required bytes.
 * Failure leaves output unchanged. Inputs/output must not overlap. No pointers
 * are stored in the serialized value or retained by this helper. */
int cf_encode_room_request(const cf_room_request *request, uint8_t *output,
                           uint32_t capacity, uint32_t *required);
#endif

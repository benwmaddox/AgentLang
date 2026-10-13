#include "request_encoder.h"
#include <stddef.h>
#define CF_INPUT_MAX_BYTES (1024u * 1024u)
typedef struct writer { uint8_t *out; uint32_t cursor; } writer;
/* Host text is UTF-16; the owning String is UTF-8. Returns UINT32_MAX for an
 * unpaired surrogate, which has no UTF-8 form. */
static uint32_t utf8_length(cf_text t) {
 uint32_t i, bytes=0u;
 for (i=0u; i<t.length; ++i) {
  uint32_t u=t.data[i];
  if (u < 0x80u) bytes += 1u;
  else if (u < 0x800u) bytes += 2u;
  else if (u >= 0xd800u && u <= 0xdbffu && i+1u < t.length &&
           t.data[i+1u] >= 0xdc00u && t.data[i+1u] <= 0xdfffu) { bytes += 4u; ++i; }
  else if (u >= 0xd800u && u <= 0xdfffu) return UINT32_MAX;
  else bytes += 3u;
 }
 return bytes;
}
static int valid_text(cf_text t) {
 return t.length <= 16384u && (t.length == 0u || t.data != NULL) &&
        (t.length == 0u || utf8_length(t) != UINT32_MAX);
}
static int valid_optional(cf_optional_text t) {
 return t.present <= 1u && (t.present ? valid_text(t.value) :
                            (t.value.length == 0u && t.value.data == NULL));
}
static uint32_t text_size(cf_text t) { return (8u + utf8_length(t) + 7u) & ~7u; }
static uint32_t optional_size(cf_optional_text t) {
 return 8u + (t.present ? text_size(t.value) : 0u);
}
static void scalar(writer *w, uint64_t v) {
 uint32_t i;
 for (i=0u; i<8u; ++i) w->out[w->cursor++] = (uint8_t)(v >> (i * 8u));
}
static void text(writer *w, cf_text t) {
 uint32_t i, start=w->cursor, extent=text_size(t);
 scalar(w, utf8_length(t));
 for (i=0u; i<t.length; ++i) {
  uint32_t s=t.data[i];
  if (s >= 0xd800u && s <= 0xdbffu) { s = 0x10000u + ((s - 0xd800u) << 10) + ((uint32_t)t.data[i+1u] - 0xdc00u); ++i; }
  if (s < 0x80u) w->out[w->cursor++] = (uint8_t)s;
  else if (s < 0x800u) { w->out[w->cursor++] = (uint8_t)(0xc0u | (s >> 6)); w->out[w->cursor++] = (uint8_t)(0x80u | (s & 0x3fu)); }
  else if (s < 0x10000u) { w->out[w->cursor++] = (uint8_t)(0xe0u | (s >> 12)); w->out[w->cursor++] = (uint8_t)(0x80u | ((s >> 6) & 0x3fu)); w->out[w->cursor++] = (uint8_t)(0x80u | (s & 0x3fu)); }
  else { w->out[w->cursor++] = (uint8_t)(0xf0u | (s >> 18)); w->out[w->cursor++] = (uint8_t)(0x80u | ((s >> 12) & 0x3fu)); w->out[w->cursor++] = (uint8_t)(0x80u | ((s >> 6) & 0x3fu)); w->out[w->cursor++] = (uint8_t)(0x80u | (s & 0x3fu)); }
 }
 while (w->cursor < start+extent) w->out[w->cursor++] = 0u;
}
static void optional(writer *w, cf_optional_text t) {
 scalar(w, t.present ? 0u : 1u);
 if (t.present) text(w,t.value);
}
int cf_encode_room_request(const cf_room_request *r, uint8_t *out,
                           uint32_t capacity, uint32_t *required) {
 uint32_t extent;
 writer w;
 if (required == NULL) return 1;
 *required=0u;
 if (r == NULL || r->method > 1u || r->browser_family > 5u ||
     r->now_unix_microseconds < INT64_C(-62135596800000000) ||
     r->now_unix_microseconds > INT64_C(253402300799999999) ||
     r->request_is_ssl > 1u || r->force_ssl > 1u || r->accepts_turbo > 1u ||
     r->accepts_html > 1u || r->browser_is_bot > 1u ||
     !valid_text(r->remote_ip) || !valid_text(r->base_url) ||
     !valid_optional(r->user_agent) || !valid_optional(r->origin) ||
     !valid_optional(r->fetch_site) || !valid_optional(r->session_token) ||
     !valid_optional(r->bot_key) || !valid_optional(r->last_room) ||
     !valid_optional(r->browser_version)) return 1;
 /* Scalars: route/method/room/now, SSL flags, accepts flags, browser family/bot.
  * Three form fields each hold Result.Ok(Option.None), 16 bytes. */
 extent=10u*8u + 3u*16u + text_size(r->remote_ip) + text_size(r->base_url) +
  optional_size(r->user_agent) + optional_size(r->origin) +
  optional_size(r->fetch_site) + optional_size(r->session_token) +
  optional_size(r->bot_key) + optional_size(r->last_room) +
  optional_size(r->browser_version);
 if (extent > CF_INPUT_MAX_BYTES) return 1;
 *required=extent;
 if (out == NULL || capacity < extent) return 2;
 w.out=out; w.cursor=0u;
 scalar(&w,0u); /* HttpRoute.room-show */
 scalar(&w,r->method); scalar(&w,(uint64_t)r->room_id);
 scalar(&w,(uint64_t)r->now_unix_microseconds);
 text(&w,r->remote_ip); optional(&w,r->user_agent); text(&w,r->base_url);
 scalar(&w,r->request_is_ssl); scalar(&w,r->force_ssl);
 optional(&w,r->origin); optional(&w,r->fetch_site);
 optional(&w,r->session_token); optional(&w,r->bot_key);
 scalar(&w,0u); scalar(&w,1u); /* message-form: Ok None */
 scalar(&w,0u); scalar(&w,1u); /* login-form */
 scalar(&w,0u); scalar(&w,1u); /* logout-form */
 scalar(&w,r->accepts_turbo); scalar(&w,r->accepts_html);
 optional(&w,r->last_room); scalar(&w,r->browser_family);
 optional(&w,r->browser_version); scalar(&w,r->browser_is_bot);
 return 0;
}

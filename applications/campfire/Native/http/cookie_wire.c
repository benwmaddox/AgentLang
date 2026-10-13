#include "cookie_wire.h"
#include <stddef.h>
static int plain(uint8_t b) {
 return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') ||
        (b >= '0' && b <= '9') || b=='-' || b=='.' || b=='_' || b=='*';
}
int cf_cookie_escape_ascii(const uint8_t *raw, uint32_t length,
                           uint8_t *wire, uint32_t capacity, uint32_t *required) {
 uint32_t i, extent=0u, cursor=0u;
 static const uint8_t hex[]="0123456789ABCDEF";
 if(required==NULL) return 1;
 *required=0u;
 if((raw==NULL && length!=0u) || (wire==NULL && capacity!=0u)) return 1;
 if(length>4096u) return 3;
 for(i=0u;i<length;++i) {
  if(raw[i]>127u) return 1;
  extent += plain(raw[i]) || raw[i]==' ' ? 1u : 3u;
 }
 *required=extent;
 if(capacity<extent) return 2;
 for(i=0u;i<length;++i) {
  uint8_t b=raw[i];
  if(plain(b)) wire[cursor++]=b;
  else if(b==' ') wire[cursor++]='+';
  else { wire[cursor++]='%'; wire[cursor++]=hex[b>>4u]; wire[cursor++]=hex[b&15u]; }
 }
 return 0;
}

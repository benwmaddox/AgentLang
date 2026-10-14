/* Standard library slice 2 (report 274): HMAC-SHA1, AES-256-GCM, JSON,
 * cookies and time. Included once by owning_stack_runtime.c after the slice 1
 * writer and argument helpers; every function here is static.
 *
 * Same rules as slice 1: freestanding C (no libc, no struct copies, no
 * aggregate zero-initializers), one writer for the plan and write passes,
 * Option tags 0 = Some / 1 = None, and results that match the interpreter
 * byte for byte, including every none case. Json values use the Bytes layout
 * (runtime kind 11): the exact text of one validated JSON value. */

#ifndef AL_OWNING_STDLIB2_H
#define AL_OWNING_STDLIB2_H

/* ---------------------------------------------------------------- SHA-1 */

typedef struct {
  uint32_t h[5];
  uint64_t length;
  uint8_t block[64];
  uint32_t used;
} al_stdlib_sha1;

static uint32_t al_stdlib_rotl(uint32_t value, uint32_t shift) {
  return (value << shift) | (value >> (32u - shift));
}

static void al_stdlib_sha1_compress(uint32_t h[5], const uint8_t *block) {
  uint32_t w[80];
  uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
  uint32_t i;
  for (i = 0u; i < 16u; ++i)
    w[i] = ((uint32_t)block[i * 4u] << 24) |
           ((uint32_t)block[i * 4u + 1u] << 16) |
           ((uint32_t)block[i * 4u + 2u] << 8) | (uint32_t)block[i * 4u + 3u];
  for (i = 16u; i < 80u; ++i)
    w[i] = al_stdlib_rotl(w[i - 3u] ^ w[i - 8u] ^ w[i - 14u] ^ w[i - 16u], 1u);
  for (i = 0u; i < 80u; ++i) {
    uint32_t f;
    uint32_t k;
    uint32_t t;
    if (i < 20u) {
      f = (b & c) | (~b & d);
      k = 0x5a827999u;
    } else if (i < 40u) {
      f = b ^ c ^ d;
      k = 0x6ed9eba1u;
    } else if (i < 60u) {
      f = (b & c) | (b & d) | (c & d);
      k = 0x8f1bbcdcu;
    } else {
      f = b ^ c ^ d;
      k = 0xca62c1d6u;
    }
    t = al_stdlib_rotl(a, 5u) + f + e + k + w[i];
    e = d;
    d = c;
    c = al_stdlib_rotl(b, 30u);
    b = a;
    a = t;
  }
  h[0] += a;
  h[1] += b;
  h[2] += c;
  h[3] += d;
  h[4] += e;
}

static void al_stdlib_sha1_init(al_stdlib_sha1 *s) {
  s->h[0] = 0x67452301u;
  s->h[1] = 0xefcdab89u;
  s->h[2] = 0x98badcfeu;
  s->h[3] = 0x10325476u;
  s->h[4] = 0xc3d2e1f0u;
  s->length = 0u;
  s->used = 0u;
}

static void al_stdlib_sha1_update(al_stdlib_sha1 *s, const uint8_t *data,
                                  uint32_t length) {
  uint32_t i;
  s->length += length;
  for (i = 0u; i < length; ++i) {
    s->block[s->used++] = data[i];
    if (s->used == 64u) {
      al_stdlib_sha1_compress(s->h, s->block);
      s->used = 0u;
    }
  }
}

static void al_stdlib_sha1_final(al_stdlib_sha1 *s, uint8_t out[20]) {
  const uint64_t bits = s->length * 8u;
  uint32_t i;
  s->block[s->used++] = 0x80u;
  if (s->used > 56u) {
    while (s->used < 64u)
      s->block[s->used++] = 0u;
    al_stdlib_sha1_compress(s->h, s->block);
    s->used = 0u;
  }
  while (s->used < 56u)
    s->block[s->used++] = 0u;
  for (i = 0u; i < 8u; ++i)
    s->block[56u + i] = (uint8_t)(bits >> (56u - i * 8u));
  al_stdlib_sha1_compress(s->h, s->block);
  for (i = 0u; i < 5u; ++i) {
    out[i * 4u] = (uint8_t)(s->h[i] >> 24);
    out[i * 4u + 1u] = (uint8_t)(s->h[i] >> 16);
    out[i * 4u + 2u] = (uint8_t)(s->h[i] >> 8);
    out[i * 4u + 3u] = (uint8_t)s->h[i];
  }
}

static void al_stdlib_hmac_sha1(const uint8_t *key, uint32_t key_length,
                                const uint8_t *message,
                                uint32_t message_length, uint8_t out[20]) {
  uint8_t block[64];
  uint8_t pad[64];
  uint8_t inner_digest[20];
  al_stdlib_sha1 s;
  uint32_t i;
  for (i = 0u; i < 64u; ++i)
    block[i] = 0u;
  if (key_length > 64u) {
    al_stdlib_sha1_init(&s);
    al_stdlib_sha1_update(&s, key, key_length);
    al_stdlib_sha1_final(&s, block);
  } else {
    for (i = 0u; i < key_length; ++i)
      block[i] = key[i];
  }
  for (i = 0u; i < 64u; ++i)
    pad[i] = (uint8_t)(block[i] ^ 0x36u);
  al_stdlib_sha1_init(&s);
  al_stdlib_sha1_update(&s, pad, 64u);
  al_stdlib_sha1_update(&s, message, message_length);
  al_stdlib_sha1_final(&s, inner_digest);
  for (i = 0u; i < 64u; ++i)
    pad[i] = (uint8_t)(block[i] ^ 0x5cu);
  al_stdlib_sha1_init(&s);
  al_stdlib_sha1_update(&s, pad, 64u);
  al_stdlib_sha1_update(&s, inner_digest, 20u);
  al_stdlib_sha1_final(&s, out);
  for (i = 0u; i < 64u; ++i) {
    block[i] = 0u;
    pad[i] = 0u;
  }
}

/* ------------------------------------------------------------ dispatch */

/* Slice 2 operations; returns 1 on success, 0 after setting a failure or for
 * an unknown id (the caller reports it). */
static int32_t al_stdlib2_emit(al_owning_stack_context *ctx, int32_t op,
                               const al_stdlib_argument *a, const uint32_t *n,
                               al_stdlib_writer *w, uint32_t error_id) {
  const uint8_t *x = al_stdlib_data(ctx, a[0]);
  (void)error_id;
  switch (op) {
  case AL_STDLIB_HMAC_SHA1: {
    uint8_t digest[20];
    uint32_t i;
    for (i = 0u; i < 20u; ++i)
      digest[i] = 0u;
    if (w->base != 0)
      al_stdlib_hmac_sha1(x, n[0], al_stdlib_data(ctx, a[1]), n[1], digest);
    al_stdlib_text(w, digest, 20u);
    return 1;
  }
  default:
    return 0;
  }
}

#endif

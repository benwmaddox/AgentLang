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

/* Ids match OwningStdlib.operations in OwningStackAot.fs; argument shapes
 * are AL_STDLIB_SLICE2_SHAPES in owning_stack_runtime.c. */
enum {
  AL_STDLIB_HMAC_SHA1 = 23,
  AL_STDLIB_AES_SEAL = 24,
  AL_STDLIB_AES_OPEN = 25,
  AL_STDLIB_JSON_PARSE = 26,
  AL_STDLIB_JSON_FIELD = 27,
  AL_STDLIB_JSON_INDEX = 28,
  AL_STDLIB_JSON_KEY_AT = 29,
  AL_STDLIB_JSON_COUNT = 30,
  AL_STDLIB_JSON_TEXT = 31,
  AL_STDLIB_JSON_INT = 32,
  AL_STDLIB_JSON_BOOL = 33,
  AL_STDLIB_JSON_NULL = 34,
  AL_STDLIB_JSON_ENCODE = 35,
  AL_STDLIB_JSON_QUOTE = 36,
  AL_STDLIB_COOKIE_FIND = 37,
  AL_STDLIB_COOKIE_COUNT = 38,
  AL_STDLIB_COOKIE_ESCAPE = 39,
  AL_STDLIB_COOKIE_UNESCAPE = 40,
  AL_STDLIB_SET_COOKIE = 41,
  AL_STDLIB_TIME_ADD_YEARS = 42,
  AL_STDLIB_TIME_RFC1123 = 43,
  AL_STDLIB_TIME_ISO8601 = 44,
  AL_STDLIB_TIME_PARSE_ISO8601 = 45,
  AL_STDLIB_STRING_COMPARE = 46,
  AL_STDLIB_STRING_LESS_THAN = 47,
  AL_STDLIB_STRING_LESS_OR_EQUAL = 48,
  AL_STDLIB_STRING_GREATER_THAN = 49,
  AL_STDLIB_STRING_GREATER_OR_EQUAL = 50,
  AL_STDLIB_STRING_URL_SAFE = 51,
  AL_STDLIB_STRING_TRIM = 52
};

/* ---------------------------------------------------------- OS crypto */

/* Every cryptographic primitive runs on the OS library; the runtime has no
 * hand-written cryptography. Each function returns 1 on success and -1 when
 * the library is unavailable or fails (AES-GCM also returns 0 for a tag
 * mismatch); callers turn -1 into RUNTIME_OS_CRYPTO_UNAVAILABLE.
 *
 * An empty HMAC key (or PBKDF2 password) is passed as one zero byte: HMAC
 * zero-pads keys to the block size, so the two keys are the same key, and
 * some library entry points reject a zero-length secret. */
#if defined(_WIN32)
/* Windows CNG. Modules that call a crypto.* primitive define
 * al_owning_os_crypto_table in their generated IR with the BCrypt entry
 * points (GenerateSymmetricKey, Encrypt, Decrypt, DestroyKey, Hash,
 * DeriveKeyPBKDF2); every other module resolves the reference to this
 * failing stub and imports nothing from bcrypt.dll. The algorithms are CNG
 * pseudo-handles (Windows 10+), so no provider is opened per call. */
int32_t al_owning_os_crypto_table_unavailable(void **table, uint32_t count) {
  (void)table;
  (void)count;
  return -1;
}
int32_t al_owning_os_crypto_table(void **table, uint32_t count);
#pragma comment(linker, "/alternatename:al_owning_os_crypto_table=al_owning_os_crypto_table_unavailable")

#define AL_BCRYPT_SHA256_ALG_HANDLE ((void *)(uintptr_t)0x41u)
#define AL_BCRYPT_HMAC_SHA1_ALG_HANDLE ((void *)(uintptr_t)0xa1u)
#define AL_BCRYPT_HMAC_SHA256_ALG_HANDLE ((void *)(uintptr_t)0xb1u)
#define AL_BCRYPT_AES_GCM_ALG_HANDLE ((void *)(uintptr_t)0x1e1u)
#define AL_OS_CRYPTO_ENTRIES 6u

/* BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO, filled field by field. */
typedef struct {
  uint32_t cbSize;
  uint32_t dwInfoVersion;
  uint8_t *pbNonce;
  uint32_t cbNonce;
  uint8_t *pbAuthData;
  uint32_t cbAuthData;
  uint8_t *pbTag;
  uint32_t cbTag;
  uint8_t *pbMacContext;
  uint32_t cbMacContext;
  uint32_t cbAAD;
  uint64_t cbData;
  uint32_t dwFlags;
} al_bcrypt_auth_info;
typedef int32_t (*al_bcrypt_generate_key_fn)(void *, void **, uint8_t *,
                                              uint32_t, uint8_t *, uint32_t,
                                              uint32_t);
typedef int32_t (*al_bcrypt_crypt_fn)(void *, uint8_t *, uint32_t, void *,
                                       uint8_t *, uint32_t, uint8_t *,
                                       uint32_t, uint32_t *, uint32_t);
typedef int32_t (*al_bcrypt_destroy_key_fn)(void *);
typedef int32_t (*al_bcrypt_hash_fn)(void *, uint8_t *, uint32_t, uint8_t *,
                                      uint32_t, uint8_t *, uint32_t);
typedef int32_t (*al_bcrypt_pbkdf2_fn)(void *, uint8_t *, uint32_t, uint8_t *,
                                        uint32_t, uint64_t, uint8_t *,
                                        uint32_t, uint32_t);

static int32_t al_os_crypto_entries(void **table) {
  uint32_t k;
  for (k = 0u; k < AL_OS_CRYPTO_ENTRIES; ++k)
    table[k] = 0;
  if (al_owning_os_crypto_table(table, AL_OS_CRYPTO_ENTRIES) != 0)
    return 0;
  for (k = 0u; k < AL_OS_CRYPTO_ENTRIES; ++k)
    if (table[k] == 0)
      return 0;
  return 1;
}

/* One-shot BCryptHash: a digest (no secret) or an HMAC (secret). */
static int32_t al_os_bcrypt_hash(void *algorithm, int32_t keyed,
                                 const uint8_t *secret, uint32_t secret_length,
                                 const uint8_t *data, uint32_t length,
                                 uint8_t *out, uint32_t out_length) {
  void *table[AL_OS_CRYPTO_ENTRIES];
  uint8_t zero[1];
  zero[0] = 0u;
  if (!al_os_crypto_entries(table))
    return -1;
  if (keyed && secret_length == 0u) {
    secret = zero;
    secret_length = 1u;
  }
  return ((al_bcrypt_hash_fn)table[4])(
             algorithm, keyed ? (uint8_t *)secret : 0, keyed ? secret_length : 0u,
             length != 0u ? (uint8_t *)data : zero, length, out,
             out_length) < 0
             ? -1
             : 1;
}

static int32_t al_os_sha256(const uint8_t *data, uint32_t length,
                            uint8_t out[32]) {
  return al_os_bcrypt_hash(AL_BCRYPT_SHA256_ALG_HANDLE, 0, 0, 0u, data, length,
                           out, 32u);
}

/* HMAC-SHA1 (20-byte digest) or HMAC-SHA256 (32 bytes). */
static int32_t al_os_hmac(int32_t sha1, const uint8_t *key,
                          uint32_t key_length, const uint8_t *data,
                          uint32_t length, uint8_t *out) {
  return al_os_bcrypt_hash(sha1 ? AL_BCRYPT_HMAC_SHA1_ALG_HANDLE
                                : AL_BCRYPT_HMAC_SHA256_ALG_HANDLE,
                           1, key, key_length, data, length, out,
                           sha1 ? 20u : 32u);
}

static int32_t al_os_pbkdf2_sha256(const uint8_t *password,
                                   uint32_t password_length,
                                   const uint8_t *salt, uint32_t salt_length,
                                   uint64_t iterations, uint8_t *out,
                                   uint32_t out_length) {
  void *table[AL_OS_CRYPTO_ENTRIES];
  uint8_t zero[1];
  zero[0] = 0u;
  if (!al_os_crypto_entries(table))
    return -1;
  if (password_length == 0u) {
    password = zero;
    password_length = 1u;
  }
  return ((al_bcrypt_pbkdf2_fn)table[5])(
             AL_BCRYPT_HMAC_SHA256_ALG_HANDLE, (uint8_t *)password,
             password_length, salt_length != 0u ? (uint8_t *)salt : zero,
             salt_length, iterations, out, out_length, 0u) < 0
             ? -1
             : 1;
}

/* Windows has no constant-time compare in CNG, so the runtime keeps its
 * XOR-accumulate loop (no early exit, time depends only on the lengths). */
static int32_t al_os_equal_constant_time(const uint8_t *x, const uint8_t *y,
                                         uint32_t length) {
  uint32_t difference = 0u;
  uint32_t i;
  for (i = 0u; i < length; ++i)
    difference |= (uint32_t)(x[i] ^ y[i]);
  return difference == 0u;
}

static int32_t al_os_aes_gcm(int32_t encrypt, const uint8_t *key,
                             const uint8_t *nonce, const uint8_t *aad,
                             uint32_t aad_length, const uint8_t *input,
                             uint32_t length, uint8_t *output, uint8_t *tag) {
  void *table[AL_OS_CRYPTO_ENTRIES];
  void *handle = 0;
  al_bcrypt_auth_info info;
  uint8_t empty[1];
  uint32_t result = 0u;
  int32_t status;
  if (!al_os_crypto_entries(table))
    return -1;
  if (((al_bcrypt_generate_key_fn)table[0])(AL_BCRYPT_AES_GCM_ALG_HANDLE,
                                            &handle, 0, 0u, (uint8_t *)key,
                                            32u, 0u) < 0)
    return -1;
  empty[0] = 0u;
  info.cbSize = (uint32_t)sizeof(info);
  info.dwInfoVersion = 1u;
  info.pbNonce = (uint8_t *)nonce;
  info.cbNonce = 12u;
  info.pbAuthData = aad_length != 0u ? (uint8_t *)aad : 0;
  info.cbAuthData = aad_length;
  info.pbTag = tag;
  info.cbTag = 16u;
  info.pbMacContext = 0;
  info.cbMacContext = 0u;
  info.cbAAD = 0u;
  info.cbData = 0u;
  info.dwFlags = 0u;
  status = ((al_bcrypt_crypt_fn)table[encrypt ? 1 : 2])(
      handle, length != 0u ? (uint8_t *)input : empty, length, &info, 0, 0u,
      length != 0u ? output : empty, length, &result, 0u);
  ((al_bcrypt_destroy_key_fn)table[3])(handle);
  if (status == (int32_t)0xC000A002u) /* STATUS_AUTH_TAG_MISMATCH */
    return 0;
  return status < 0 || result != length ? -1 : 1;
}
#else
/* Linux: OpenSSL libcrypto, weakly referenced so a module that does not link
 * libcrypto still loads; the entry points are then null and every crypto
 * primitive reports the library unavailable. Link with
 * -Wl,--no-as-needed -lcrypto. size_t is unsigned long on LP64 Linux. */
extern const void *EVP_sha1(void) __attribute__((weak));
extern const void *EVP_sha256(void) __attribute__((weak));
extern int EVP_Digest(const void *, unsigned long, uint8_t *, unsigned int *,
                      const void *, void *) __attribute__((weak));
extern uint8_t *HMAC(const void *, const void *, int, const uint8_t *,
                     unsigned long, uint8_t *, unsigned int *)
    __attribute__((weak));
extern int PKCS5_PBKDF2_HMAC(const char *, int, const uint8_t *, int, int,
                             const void *, int, uint8_t *)
    __attribute__((weak));
extern int CRYPTO_memcmp(const void *, const void *, unsigned long)
    __attribute__((weak));
extern void *EVP_CIPHER_CTX_new(void) __attribute__((weak));
extern void EVP_CIPHER_CTX_free(void *) __attribute__((weak));
extern const void *EVP_aes_256_gcm(void) __attribute__((weak));
extern int EVP_EncryptInit_ex(void *, const void *, void *, const uint8_t *,
                              const uint8_t *) __attribute__((weak));
extern int EVP_EncryptUpdate(void *, uint8_t *, int *, const uint8_t *, int)
    __attribute__((weak));
extern int EVP_EncryptFinal_ex(void *, uint8_t *, int *) __attribute__((weak));
extern int EVP_DecryptInit_ex(void *, const void *, void *, const uint8_t *,
                              const uint8_t *) __attribute__((weak));
extern int EVP_DecryptUpdate(void *, uint8_t *, int *, const uint8_t *, int)
    __attribute__((weak));
extern int EVP_DecryptFinal_ex(void *, uint8_t *, int *) __attribute__((weak));
extern int EVP_CIPHER_CTX_ctrl(void *, int, int, void *) __attribute__((weak));

/* Per-worker OpenSSL 3 objects (user approved, report 279). The one-shot
 * EVP_Digest / HMAC() fetch the algorithm and allocate a context on every
 * call (~0.8 us). Each thread instead fetches SHA-256 and HMAC once, keeps
 * one digest context and one HMAC context per digest, and re-keys them per
 * call. The objects are thread-local: never shared across threads, no locks.
 * They live as long as the worker thread. If any entry point is missing or a
 * fetch fails, the thread keeps using the one-shot calls below.
 * OSSL_PARAM mirrors struct ossl_param_st (openssl/core.h). */
typedef struct {
  const char *key;
  unsigned int data_type;
  void *data;
  unsigned long data_size;
  unsigned long return_size;
} al_ossl_param;
#define AL_OSSL_PARAM_UTF8_STRING 4u
extern void *EVP_MD_fetch(void *, const char *, const char *)
    __attribute__((weak));
extern void EVP_MD_free(void *) __attribute__((weak));
extern void *EVP_MD_CTX_new(void) __attribute__((weak));
extern void EVP_MD_CTX_free(void *) __attribute__((weak));
extern int EVP_DigestInit_ex2(void *, const void *, const al_ossl_param *)
    __attribute__((weak));
extern int EVP_DigestUpdate(void *, const void *, unsigned long)
    __attribute__((weak));
extern int EVP_DigestFinal_ex(void *, uint8_t *, unsigned int *)
    __attribute__((weak));
extern void *EVP_MAC_fetch(void *, const char *, const char *)
    __attribute__((weak));
extern void EVP_MAC_free(void *) __attribute__((weak));
extern void *EVP_MAC_CTX_new(void *) __attribute__((weak));
extern void EVP_MAC_CTX_free(void *) __attribute__((weak));
extern int EVP_MAC_CTX_set_params(void *, const al_ossl_param *)
    __attribute__((weak));
extern int EVP_MAC_init(void *, const uint8_t *, unsigned long,
                        const al_ossl_param *) __attribute__((weak));
extern int EVP_MAC_update(void *, const uint8_t *, unsigned long)
    __attribute__((weak));
extern int EVP_MAC_final(void *, uint8_t *, unsigned long *, unsigned long)
    __attribute__((weak));

typedef struct {
  int32_t state; /* 0 untried, 1 ready, -1 one-shot fallback */
  void *sha256;  /* EVP_MD */
  void *digest;  /* EVP_MD_CTX */
  void *hmac[2]; /* EVP_MAC_CTX: [0] HMAC-SHA256, [1] HMAC-SHA1 */
} al_os_crypto_cache;

static _Thread_local al_os_crypto_cache al_os_cache;

static void *al_os_hmac_context(void *mac, const char *digest_name) {
  al_ossl_param params[2];
  void *context = EVP_MAC_CTX_new(mac);
  if (context == 0)
    return 0;
  params[0].key = "digest";
  params[0].data_type = AL_OSSL_PARAM_UTF8_STRING;
  params[0].data = (void *)digest_name;
  params[0].data_size = 0u;
  while (digest_name[params[0].data_size] != '\0')
    ++params[0].data_size;
  params[0].return_size = ~0ul; /* OSSL_PARAM_UNMODIFIED */
  params[1].key = 0;
  params[1].data_type = 0u;
  params[1].data = 0;
  params[1].data_size = 0u;
  params[1].return_size = 0u;
  if (EVP_MAC_CTX_set_params(context, params) != 1) {
    EVP_MAC_CTX_free(context);
    return 0;
  }
  return context;
}

/* The calling thread's cache, created on first use; 0 means one-shot. */
static al_os_crypto_cache *al_os_crypto_cached(void) {
  al_os_crypto_cache *cache = &al_os_cache;
  void *mac;
  if (cache->state == 1)
    return cache;
  if (cache->state < 0)
    return 0;
  cache->state = -1;
  if (EVP_MD_fetch == 0 || EVP_MD_free == 0 || EVP_MD_CTX_new == 0 ||
      EVP_MD_CTX_free == 0 || EVP_DigestInit_ex2 == 0 ||
      EVP_DigestUpdate == 0 || EVP_DigestFinal_ex == 0 ||
      EVP_MAC_fetch == 0 || EVP_MAC_free == 0 || EVP_MAC_CTX_new == 0 ||
      EVP_MAC_CTX_free == 0 || EVP_MAC_CTX_set_params == 0 ||
      EVP_MAC_init == 0 || EVP_MAC_update == 0 || EVP_MAC_final == 0)
    return 0;
  cache->sha256 = EVP_MD_fetch(0, "SHA256", 0);
  cache->digest = EVP_MD_CTX_new();
  mac = EVP_MAC_fetch(0, "HMAC", 0);
  if (mac != 0) {
    /* Each context holds its own reference to the HMAC algorithm. */
    cache->hmac[0] = al_os_hmac_context(mac, "SHA256");
    cache->hmac[1] = al_os_hmac_context(mac, "SHA1");
    EVP_MAC_free(mac);
  }
  if (cache->sha256 == 0 || cache->digest == 0 || cache->hmac[0] == 0 ||
      cache->hmac[1] == 0) {
    if (cache->sha256 != 0)
      EVP_MD_free(cache->sha256);
    if (cache->digest != 0)
      EVP_MD_CTX_free(cache->digest);
    if (cache->hmac[0] != 0)
      EVP_MAC_CTX_free(cache->hmac[0]);
    if (cache->hmac[1] != 0)
      EVP_MAC_CTX_free(cache->hmac[1]);
    cache->sha256 = cache->digest = cache->hmac[0] = cache->hmac[1] = 0;
    return 0;
  }
  cache->state = 1;
  return cache;
}

static int32_t al_os_sha256(const uint8_t *data, uint32_t length,
                            uint8_t out[32]) {
  uint8_t zero[1];
  unsigned int produced = 0u;
  al_os_crypto_cache *cache = al_os_crypto_cached();
  zero[0] = 0u;
  if (cache != 0)
    return EVP_DigestInit_ex2(cache->digest, cache->sha256, 0) == 1 &&
                   EVP_DigestUpdate(cache->digest,
                                    length != 0u ? data : zero, length) == 1 &&
                   EVP_DigestFinal_ex(cache->digest, out, &produced) == 1 &&
                   produced == 32u
               ? 1
               : -1;
  if (EVP_Digest == 0 || EVP_sha256 == 0)
    return -1;
  return EVP_Digest(length != 0u ? data : zero, length, out, &produced,
                    EVP_sha256(), 0) == 1 &&
                 produced == 32u
             ? 1
             : -1;
}

static int32_t al_os_hmac(int32_t sha1, const uint8_t *key,
                          uint32_t key_length, const uint8_t *data,
                          uint32_t length, uint8_t *out) {
  uint8_t zero[1];
  unsigned int produced = 0u;
  al_os_crypto_cache *cache;
  zero[0] = 0u;
  if (key_length > 0x7fffffffu)
    return -1;
  if (key_length == 0u) {
    key = zero;
    key_length = 1u;
  }
  cache = al_os_crypto_cached();
  if (cache != 0) {
    void *context = cache->hmac[sha1 ? 1 : 0];
    unsigned long written = 0u;
    return EVP_MAC_init(context, key, key_length, 0) == 1 &&
                   EVP_MAC_update(context, length != 0u ? data : zero,
                                  length) == 1 &&
                   EVP_MAC_final(context, out, &written, sha1 ? 20u : 32u) ==
                       1 &&
                   written == (sha1 ? 20u : 32u)
               ? 1
               : -1;
  }
  if (HMAC == 0 || EVP_sha1 == 0 || EVP_sha256 == 0)
    return -1;
  return HMAC(sha1 ? EVP_sha1() : EVP_sha256(), key, (int)key_length,
              length != 0u ? data : zero, length, out, &produced) != 0 &&
                 produced == (sha1 ? 20u : 32u)
             ? 1
             : -1;
}

static int32_t al_os_pbkdf2_sha256(const uint8_t *password,
                                   uint32_t password_length,
                                   const uint8_t *salt, uint32_t salt_length,
                                   uint64_t iterations, uint8_t *out,
                                   uint32_t out_length) {
  uint8_t zero[1];
  zero[0] = 0u;
  if (PKCS5_PBKDF2_HMAC == 0 || EVP_sha256 == 0 ||
      password_length > 0x7fffffffu || salt_length > 0x7fffffffu ||
      iterations > 0x7fffffffu || out_length > 0x7fffffffu)
    return -1;
  if (password_length == 0u) {
    password = zero;
    password_length = 1u;
  }
  return PKCS5_PBKDF2_HMAC((const char *)password, (int)password_length,
                           salt_length != 0u ? salt : zero, (int)salt_length,
                           (int)iterations, EVP_sha256(), (int)out_length,
                           out) == 1
             ? 1
             : -1;
}

/* CRYPTO_memcmp when libcrypto is linked; otherwise the same
 * XOR-accumulate loop (a compare, not a cipher). */
static int32_t al_os_equal_constant_time(const uint8_t *x, const uint8_t *y,
                                         uint32_t length) {
  uint32_t difference = 0u;
  uint32_t i;
  if (CRYPTO_memcmp != 0)
    return CRYPTO_memcmp(x, y, length) == 0;
  for (i = 0u; i < length; ++i)
    difference |= (uint32_t)(x[i] ^ y[i]);
  return difference == 0u;
}

static int32_t al_os_aes_gcm(int32_t encrypt, const uint8_t *key,
                             const uint8_t *nonce, const uint8_t *aad,
                             uint32_t aad_length, const uint8_t *input,
                             uint32_t length, uint8_t *output, uint8_t *tag) {
  void *ctx;
  int produced = 0;
  int ok;
  int32_t result = -1;
  if (EVP_CIPHER_CTX_new == 0 || EVP_aes_256_gcm == 0 ||
      EVP_CIPHER_CTX_ctrl == 0 || EVP_CIPHER_CTX_free == 0 ||
      (encrypt && (EVP_EncryptInit_ex == 0 || EVP_EncryptUpdate == 0 ||
                   EVP_EncryptFinal_ex == 0)) ||
      (!encrypt && (EVP_DecryptInit_ex == 0 || EVP_DecryptUpdate == 0 ||
                    EVP_DecryptFinal_ex == 0)) ||
      length > 0x7fffffffu || aad_length > 0x7fffffffu)
    return -1;
  ctx = EVP_CIPHER_CTX_new();
  if (ctx == 0)
    return -1;
  if (encrypt) {
    ok = EVP_EncryptInit_ex(ctx, EVP_aes_256_gcm(), 0, key, nonce) == 1 &&
         (aad_length == 0u ||
          EVP_EncryptUpdate(ctx, 0, &produced, aad, (int)aad_length) == 1) &&
         (length == 0u || EVP_EncryptUpdate(ctx, output, &produced, input,
                                            (int)length) == 1) &&
         EVP_EncryptFinal_ex(ctx, output + length, &produced) == 1 &&
         EVP_CIPHER_CTX_ctrl(ctx, 0x10 /* GCM_GET_TAG */, 16, tag) == 1;
    result = ok ? 1 : -1;
  } else {
    ok = EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), 0, key, nonce) == 1 &&
         (aad_length == 0u ||
          EVP_DecryptUpdate(ctx, 0, &produced, aad, (int)aad_length) == 1) &&
         (length == 0u || EVP_DecryptUpdate(ctx, output, &produced, input,
                                            (int)length) == 1) &&
         EVP_CIPHER_CTX_ctrl(ctx, 0x11 /* GCM_SET_TAG */, 16, tag) == 1;
    if (ok)
      result =
          EVP_DecryptFinal_ex(ctx, output + length, &produced) == 1 ? 1 : 0;
  }
  EVP_CIPHER_CTX_free(ctx);
  return result;
}
#endif

/* ------------------------------------------------------------------ UTF-8 */

static uint32_t al_utf8_encode(uint32_t scalar, uint8_t out[4]) {
  if (scalar <= 0x7fu) {
    out[0] = (uint8_t)scalar;
    return 1u;
  }
  if (scalar <= 0x7ffu) {
    out[0] = (uint8_t)(0xc0u | (scalar >> 6));
    out[1] = (uint8_t)(0x80u | (scalar & 0x3fu));
    return 2u;
  }
  if (scalar <= 0xffffu) {
    out[0] = (uint8_t)(0xe0u | (scalar >> 12));
    out[1] = (uint8_t)(0x80u | ((scalar >> 6) & 0x3fu));
    out[2] = (uint8_t)(0x80u | (scalar & 0x3fu));
    return 3u;
  }
  out[0] = (uint8_t)(0xf0u | (scalar >> 18));
  out[1] = (uint8_t)(0x80u | ((scalar >> 12) & 0x3fu));
  out[2] = (uint8_t)(0x80u | ((scalar >> 6) & 0x3fu));
  out[3] = (uint8_t)(0x80u | (scalar & 0x3fu));
  return 4u;
}

/* The scalar at x[i] of well-formed UTF-8 ending at n; returns its width. */
static uint32_t al_utf8_scalar(const uint8_t *x, uint32_t n, uint32_t i,
                               uint32_t *scalar) {
  const uint8_t b = x[i];
  if (b < 0xc0u || i + 1u >= n) {
    *scalar = b;
    return 1u;
  }
  if (b < 0xe0u) {
    *scalar = ((uint32_t)(b & 0x1fu) << 6) | (uint32_t)(x[i + 1u] & 0x3fu);
    return 2u;
  }
  if (b < 0xf0u) {
    if (i + 2u >= n) {
      *scalar = b;
      return 1u;
    }
    *scalar = ((uint32_t)(b & 0x0fu) << 12) |
              ((uint32_t)(x[i + 1u] & 0x3fu) << 6) |
              (uint32_t)(x[i + 2u] & 0x3fu);
    return 3u;
  }
  if (i + 3u >= n) {
    *scalar = b;
    return 1u;
  }
  *scalar = ((uint32_t)(b & 0x07u) << 18) |
            ((uint32_t)(x[i + 1u] & 0x3fu) << 12) |
            ((uint32_t)(x[i + 2u] & 0x3fu) << 6) |
            (uint32_t)(x[i + 3u] & 0x3fu);
  return 4u;
}

/* ------------------------------------------------------------------- JSON */

#define AL_JSON_MAX_DEPTH 32u

static int32_t al_json_ws(uint8_t b) {
  return b == 0x20u || b == 0x09u || b == 0x0au || b == 0x0du;
}

static uint32_t al_json_skip(const uint8_t *x, uint32_t n, uint32_t i) {
  while (i < n && al_json_ws(x[i]))
    ++i;
  return i;
}

static int32_t al_json_hexdigit(uint8_t b) {
  if (b >= '0' && b <= '9')
    return (int32_t)(b - '0');
  if (b >= 'a' && b <= 'f')
    return (int32_t)(b - 'a') + 10;
  if (b >= 'A' && b <= 'F')
    return (int32_t)(b - 'A') + 10;
  return -1;
}

static int32_t al_json_hex4(const uint8_t *x, uint32_t n, uint32_t at) {
  int32_t value = 0;
  uint32_t k;
  if (at > n || n - at < 4u)
    return -1;
  for (k = 0u; k < 4u; ++k) {
    const int32_t d = al_json_hexdigit(x[at + k]);
    if (d < 0)
      return -1;
    value = value * 16 + d;
  }
  return value;
}

/* End of the string token at x[i] == '"', or -1: no raw control bytes, known
 * escapes only, and surrogate escapes only in valid pairs. */
static int64_t al_json_string_end(const uint8_t *x, uint32_t n, uint32_t i) {
  i += 1u;
  while (i < n) {
    const uint8_t c = x[i];
    if (c == '"')
      return (int64_t)i + 1;
    if (c < 0x20u)
      return -1;
    if (c == '\\') {
      uint8_t e;
      if (i + 1u >= n)
        return -1;
      e = x[i + 1u];
      if (e == '"' || e == '\\' || e == '/' || e == 'b' || e == 'f' ||
          e == 'n' || e == 'r' || e == 't') {
        i += 2u;
      } else if (e == 'u') {
        const int32_t v = al_json_hex4(x, n, i + 2u);
        if (v < 0 || (v >= 0xdc00 && v <= 0xdfff))
          return -1;
        if (v >= 0xd800 && v <= 0xdbff) {
          int32_t low;
          if (!(i + 7u < n && x[i + 6u] == '\\' && x[i + 7u] == 'u'))
            return -1;
          low = al_json_hex4(x, n, i + 8u);
          if (low < 0xdc00 || low > 0xdfff)
            return -1;
          i += 12u;
        } else {
          i += 6u;
        }
      } else {
        return -1;
      }
    } else {
      i += 1u;
    }
  }
  return -1;
}

static int32_t al_json_digit_at(const uint8_t *x, uint32_t n, uint32_t i) {
  return i < n && x[i] >= '0' && x[i] <= '9';
}

static int64_t al_json_number_end(const uint8_t *x, uint32_t n, uint32_t i) {
  if (i < n && x[i] == '-')
    ++i;
  if (!al_json_digit_at(x, n, i))
    return -1;
  if (x[i] == '0') {
    ++i;
  } else {
    while (al_json_digit_at(x, n, i))
      ++i;
  }
  if (i < n && x[i] == '.') {
    ++i;
    if (!al_json_digit_at(x, n, i))
      return -1;
    while (al_json_digit_at(x, n, i))
      ++i;
  }
  if (i < n && (x[i] == 'e' || x[i] == 'E')) {
    ++i;
    if (i < n && (x[i] == '+' || x[i] == '-'))
      ++i;
    if (!al_json_digit_at(x, n, i))
      return -1;
    while (al_json_digit_at(x, n, i))
      ++i;
  }
  return (int64_t)i;
}

static int64_t al_json_literal_end(const uint8_t *x, uint32_t n, uint32_t i,
                                   const char *word, uint32_t length) {
  uint32_t k;
  if (i > n || n - i < length)
    return -1;
  for (k = 0u; k < length; ++k)
    if (x[i + k] != (uint8_t)word[k])
      return -1;
  return (int64_t)i + length;
}

/* End of the JSON value at x[i] (no leading whitespace), or -1. depth counts
 * the enclosing containers; at most AL_JSON_MAX_DEPTH may nest. */
static int64_t al_json_value_end(const uint8_t *x, uint32_t n, uint32_t i,
                                 uint32_t depth) {
  uint8_t closer;
  int32_t is_object;
  if (i >= n)
    return -1;
  switch (x[i]) {
  case '"':
    return al_json_string_end(x, n, i);
  case 't':
    return al_json_literal_end(x, n, i, "true", 4u);
  case 'f':
    return al_json_literal_end(x, n, i, "false", 5u);
  case 'n':
    return al_json_literal_end(x, n, i, "null", 4u);
  case '{':
  case '[':
    break;
  default:
    return al_json_number_end(x, n, i);
  }
  if (depth + 1u > AL_JSON_MAX_DEPTH)
    return -1;
  is_object = x[i] == '{';
  closer = is_object ? (uint8_t)'}' : (uint8_t)']';
  i = al_json_skip(x, n, i + 1u);
  if (i < n && x[i] == closer)
    return (int64_t)i + 1;
  for (;;) {
    int64_t e;
    if (is_object) {
      if (i >= n || x[i] != '"')
        return -1;
      e = al_json_string_end(x, n, i);
      if (e < 0)
        return -1;
      i = al_json_skip(x, n, (uint32_t)e);
      if (i >= n || x[i] != ':')
        return -1;
      i = al_json_skip(x, n, i + 1u);
    }
    e = al_json_value_end(x, n, i, depth + 1u);
    if (e < 0)
      return -1;
    i = al_json_skip(x, n, (uint32_t)e);
    if (i < n && x[i] == ',')
      i = al_json_skip(x, n, i + 1u);
    else if (i < n && x[i] == closer)
      return (int64_t)i + 1;
    else
      return -1;
  }
}

/* The next member (objects: key and value spans) or element (arrays) of a
 * validated container; *cursor starts at al_json_skip(x, n, 1). */
static int32_t al_json_next(const uint8_t *x, uint32_t n, uint32_t *cursor,
                            uint32_t *ks, uint32_t *ke, uint32_t *vs,
                            uint32_t *ve) {
  uint32_t i = *cursor;
  int64_t e;
  if (n < 2u || i >= n - 1u || (x[0] != '{' && x[0] != '['))
    return 0;
  *ks = 0u;
  *ke = 0u;
  if (x[0] == '{') {
    e = al_json_string_end(x, n, i);
    if (e < 0)
      return 0;
    *ks = i;
    *ke = (uint32_t)e;
    i = al_json_skip(x, n, (uint32_t)e);
    i = al_json_skip(x, n, i + 1u);
  }
  e = al_json_value_end(x, n, i, 0u);
  if (e < 0)
    return 0;
  *vs = i;
  *ve = (uint32_t)e;
  i = al_json_skip(x, n, (uint32_t)e);
  if (i < n && x[i] == ',')
    i = al_json_skip(x, n, i + 1u);
  *cursor = i;
  return 1;
}

/* One decoded unit of a validated string token at x[*i] (inside the
 * quotes): writes 1..4 UTF-8 bytes and advances *i. */
static uint32_t al_json_unit(const uint8_t *x, uint32_t n, uint32_t *i,
                             uint8_t out[4]) {
  const uint8_t c = x[*i];
  uint8_t e;
  if (c != '\\') {
    out[0] = c;
    *i += 1u;
    return 1u;
  }
  e = x[*i + 1u];
  if (e == 'u') {
    uint32_t scalar = (uint32_t)al_json_hex4(x, n, *i + 2u);
    if (scalar >= 0xd800u && scalar <= 0xdbffu) {
      scalar = 0x10000u + ((scalar - 0xd800u) << 10) +
               ((uint32_t)al_json_hex4(x, n, *i + 8u) - 0xdc00u);
      *i += 12u;
    } else {
      *i += 6u;
    }
    return al_utf8_encode(scalar, out);
  }
  out[0] = e == 'b'   ? 8u
           : e == 'f' ? 12u
           : e == 'n' ? 10u
           : e == 'r' ? 13u
           : e == 't' ? 9u
                      : e;
  *i += 2u;
  return 1u;
}

/* Writes the decoded text of the string token x[start..end) as a String. */
static void al_json_put_decoded(al_stdlib_writer *w, const uint8_t *x,
                                uint32_t n, uint32_t start, uint32_t end) {
  uint8_t unit[4];
  uint32_t i = start + 1u;
  uint32_t total = 0u;
  while (i < end - 1u)
    total += al_json_unit(x, n, &i, unit);
  al_stdlib_begin_text(w, total);
  i = start + 1u;
  while (i < end - 1u) {
    const uint32_t count = al_json_unit(x, n, &i, unit);
    al_stdlib_put(w, unit, count);
  }
  al_stdlib_pad(w);
}

static int32_t al_json_key_equals(const uint8_t *x, uint32_t n, uint32_t start,
                                  uint32_t end, const uint8_t *name,
                                  uint32_t name_length) {
  uint8_t unit[4];
  uint32_t i = start + 1u;
  uint32_t used = 0u;
  while (i < end - 1u) {
    const uint32_t count = al_json_unit(x, n, &i, unit);
    uint32_t k;
    if (count > name_length - used)
      return 0;
    for (k = 0u; k < count; ++k)
      if (unit[k] != name[used + k])
        return 0;
    used += count;
  }
  return used == name_length;
}

/* A JSON string literal escaped as Rails writes the cookie envelope. */
static void al_json_quote(al_stdlib_writer *w, const uint8_t *x, uint32_t n) {
  static const char digits[] = "0123456789abcdef";
  uint32_t length = 2u;
  uint32_t i;
  for (i = 0u; i < n; ++i) {
    const uint8_t c = x[i];
    if (c == '"' || c == '\\' || c == 8u || c == 12u || c == 10u ||
        c == 13u || c == 9u)
      length += 2u;
    else if (c == '<' || c == '>' || c == '&' || c < 0x20u)
      length += 6u;
    else
      length += 1u;
  }
  al_stdlib_begin_text(w, length);
  al_stdlib_put_byte(w, '"');
  for (i = 0u; i < n; ++i) {
    const uint8_t c = x[i];
    if (c == '"' || c == '\\') {
      al_stdlib_put_byte(w, '\\');
      al_stdlib_put_byte(w, c);
    } else if (c == 8u || c == 12u || c == 10u || c == 13u || c == 9u) {
      al_stdlib_put_byte(w, '\\');
      al_stdlib_put_byte(w, c == 8u    ? (uint8_t)'b'
                            : c == 12u ? (uint8_t)'f'
                            : c == 10u ? (uint8_t)'n'
                            : c == 13u ? (uint8_t)'r'
                                       : (uint8_t)'t');
    } else if (c == '<' || c == '>' || c == '&' || c < 0x20u) {
      al_stdlib_put_byte(w, '\\');
      al_stdlib_put_byte(w, 'u');
      al_stdlib_put_byte(w, '0');
      al_stdlib_put_byte(w, '0');
      al_stdlib_put_byte(w, (uint8_t)digits[c >> 4]);
      al_stdlib_put_byte(w, (uint8_t)digits[c & 15u]);
    } else {
      al_stdlib_put_byte(w, c);
    }
  }
  al_stdlib_put_byte(w, '"');
  al_stdlib_pad(w);
}

/* ---------------------------------------------------------------- cookies */

static int32_t al_cookie_whitespace(uint32_t s) {
  return (s >= 0x09u && s <= 0x0du) || s == 0x20u || s == 0x85u ||
         s == 0xa0u || s == 0x1680u || (s >= 0x2000u && s <= 0x200au) ||
         s == 0x2028u || s == 0x2029u || s == 0x202fu || s == 0x205fu ||
         s == 0x3000u;
}

/* The next ';'-separated part from *cursor, trimmed of Unicode whitespace,
 * with its first '=' at *equal (UINT32_MAX when absent). 0 at the end. */
static int32_t al_cookie_next(const uint8_t *x, uint32_t n, uint32_t *cursor,
                              uint32_t *begin, uint32_t *end,
                              uint32_t *equal) {
  uint32_t b = *cursor;
  uint32_t e = b;
  uint32_t k;
  if (b >= n)
    return 0;
  while (e < n && x[e] != ';')
    ++e;
  *cursor = e + 1u;
  while (b < e) {
    uint32_t scalar;
    const uint32_t width = al_utf8_scalar(x, e, b, &scalar);
    if (!al_cookie_whitespace(scalar))
      break;
    b += width;
  }
  while (e > b) {
    uint32_t start = e - 1u;
    uint32_t scalar;
    while (start > b && (x[start] & 0xc0u) == 0x80u)
      --start;
    (void)al_utf8_scalar(x, e, start, &scalar);
    if (!al_cookie_whitespace(scalar))
      break;
    e = start;
  }
  *begin = b;
  *end = e;
  *equal = UINT32_MAX;
  for (k = b; k < e; ++k) {
    if (x[k] == '=') {
      *equal = k;
      break;
    }
  }
  return 1;
}

static int32_t al_cookie_name_is(const uint8_t *x, uint32_t begin,
                                 uint32_t equal, const uint8_t *name,
                                 uint32_t name_length) {
  uint32_t k;
  if (equal == UINT32_MAX || equal - begin != name_length)
    return 0;
  for (k = 0u; k < name_length; ++k)
    if (x[begin + k] != name[k])
      return 0;
  return 1;
}

static int32_t al_cookie_safe(uint8_t b) {
  return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') ||
         (b >= '0' && b <= '9') || b == '-' || b == '.' || b == '_' ||
         b == '*';
}

static uint64_t al_cookie_escaped_length(const uint8_t *x, uint32_t n) {
  uint64_t total = 0u;
  uint32_t i;
  for (i = 0u; i < n; ++i)
    total += al_cookie_safe(x[i]) || x[i] == ' ' ? 1u : 3u;
  return total;
}

static void al_cookie_put_escaped(al_stdlib_writer *w, const uint8_t *x,
                                  uint32_t n) {
  static const char digits[] = "0123456789ABCDEF";
  uint32_t i;
  for (i = 0u; i < n; ++i) {
    if (al_cookie_safe(x[i])) {
      al_stdlib_put_byte(w, x[i]);
    } else if (x[i] == ' ') {
      al_stdlib_put_byte(w, '+');
    } else {
      al_stdlib_put_byte(w, '%');
      al_stdlib_put_byte(w, (uint8_t)digits[x[i] >> 4]);
      al_stdlib_put_byte(w, (uint8_t)digits[x[i] & 15u]);
    }
  }
}

/* The decoded byte at wire position *pos of a well-formed escaped value
 * ('+' is space, %XX a byte); advances *pos. */
static uint8_t al_cookie_take(const uint8_t *x, uint32_t *pos) {
  const uint8_t b = x[*pos];
  if (b == '%') {
    const uint8_t value = (uint8_t)(al_json_hexdigit(x[*pos + 1u]) * 16 +
                                    al_json_hexdigit(x[*pos + 2u]));
    *pos += 3u;
    return value;
  }
  *pos += 1u;
  return b == '+' ? (uint8_t)' ' : b;
}

static int32_t al_cookie_continuation(uint8_t b) {
  return (b & 0xc0u) == 0x80u;
}

/* One scalar of the decoded stream at wire position *pos with .NET's
 * replacing decoder (each maximal ill-formed subpart is one U+FFFD), as
 * cf_utf8_decode_replacing_v1. */
static uint32_t al_cookie_scalar(const uint8_t *x, uint32_t n,
                                 uint32_t *pos) {
  uint8_t look[4];
  uint32_t ends[4];
  uint32_t available = 0u;
  uint32_t probe = *pos;
  uint32_t scalar = 0xfffdu;
  uint32_t consumed = 1u;
  uint8_t first;
  while (available < 4u && probe < n) {
    look[available] = al_cookie_take(x, &probe);
    ends[available] = probe;
    ++available;
  }
  first = look[0];
  if (first <= 0x7fu) {
    scalar = first;
  } else if (first >= 0xc2u && first <= 0xdfu) {
    if (available > 1u && al_cookie_continuation(look[1])) {
      scalar = ((uint32_t)(first & 0x1fu) << 6) | (uint32_t)(look[1] & 0x3fu);
      consumed = 2u;
    }
  } else if (first >= 0xe0u && first <= 0xefu) {
    if (available > 1u && al_cookie_continuation(look[1]) &&
        !(first == 0xe0u && look[1] < 0xa0u) &&
        !(first == 0xedu && look[1] > 0x9fu)) {
      if (available > 2u && al_cookie_continuation(look[2])) {
        scalar = ((uint32_t)(first & 0x0fu) << 12) |
                 ((uint32_t)(look[1] & 0x3fu) << 6) |
                 (uint32_t)(look[2] & 0x3fu);
        consumed = 3u;
      } else {
        consumed = 2u;
      }
    }
  } else if (first >= 0xf0u && first <= 0xf4u) {
    if (available > 1u && al_cookie_continuation(look[1]) &&
        !(first == 0xf0u && look[1] < 0x90u) &&
        !(first == 0xf4u && look[1] > 0x8fu)) {
      if (available > 2u && al_cookie_continuation(look[2])) {
        if (available > 3u && al_cookie_continuation(look[3])) {
          scalar = ((uint32_t)(first & 0x07u) << 18) |
                   ((uint32_t)(look[1] & 0x3fu) << 12) |
                   ((uint32_t)(look[2] & 0x3fu) << 6) |
                   (uint32_t)(look[3] & 0x3fu);
          consumed = 4u;
        } else {
          consumed = 3u;
        }
      } else {
        consumed = 2u;
      }
    }
  }
  *pos = ends[consumed - 1u];
  return scalar;
}

/* Rack's cookie unescape: a malformed '%' keeps the whole value. */
static void al_cookie_unescape(al_stdlib_writer *w, const uint8_t *x,
                               uint32_t n) {
  uint8_t out[4];
  uint64_t total = 0u;
  uint32_t pos;
  uint32_t i;
  for (i = 0u; i < n;) {
    if (x[i] == '%') {
      if (i + 2u >= n || al_json_hexdigit(x[i + 1u]) < 0 ||
          al_json_hexdigit(x[i + 2u]) < 0) {
        al_stdlib_text(w, x, n);
        return;
      }
      i += 3u;
    } else {
      i += 1u;
    }
  }
  for (pos = 0u; pos < n;)
    total += al_utf8_encode(al_cookie_scalar(x, n, &pos), out);
  al_stdlib_begin_text(w, (uint32_t)total);
  for (pos = 0u; pos < n;) {
    const uint32_t count = al_utf8_encode(al_cookie_scalar(x, n, &pos), out);
    al_stdlib_put(w, out, count);
  }
  al_stdlib_pad(w);
}

static int32_t al_cookie_token_byte(uint8_t b) {
  static const char separators[] = "()<>@,;:\\\"/[]?={}";
  uint32_t k;
  if (b <= 0x20u || b >= 0x7fu)
    return 0;
  for (k = 0u; k < sizeof(separators) - 1u; ++k)
    if (b == (uint8_t)separators[k])
      return 0;
  return 1;
}

static int32_t al_cookie_attribute_text(const uint8_t *x, uint32_t n) {
  uint32_t k;
  for (k = 0u; k < n; ++k)
    if (x[k] < 0x20u || x[k] >= 0x7fu || x[k] == ';')
      return 0;
  return 1;
}

static uint32_t al_stdlib_ascii_length(const char *text) {
  uint32_t length = 0u;
  while (text[length] != 0)
    ++length;
  return length;
}

static void al_stdlib_put_ascii(al_stdlib_writer *w, const char *text) {
  al_stdlib_put(w, (const uint8_t *)text, al_stdlib_ascii_length(text));
}

/* ------------------------------------------------------------------- time */

#define AL_TIME_MIN_US (-INT64_C(62135596800000000))
#define AL_TIME_MAX_US INT64_C(253402300799999999)
#define AL_TIME_US_PER_DAY INT64_C(86400000000)

static int32_t al_time_leap(int64_t y) {
  return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
}

static int64_t al_time_month_days(int64_t y, int64_t m) {
  static const uint8_t days[12] = {31u, 28u, 31u, 30u, 31u, 30u,
                                   31u, 31u, 30u, 31u, 30u, 31u};
  return m == 2 && al_time_leap(y) ? 29 : (int64_t)days[m - 1];
}

/* Days since 1970-01-01 (H. Hinnant's civil calendar algorithms). */
static int64_t al_time_days_from_civil(int64_t y, int64_t m, int64_t d) {
  int64_t era;
  int64_t yoe;
  int64_t doy;
  y -= m <= 2 ? 1 : 0;
  era = (y >= 0 ? y : y - 399) / 400;
  yoe = y - era * 400;
  doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
  return era * 146097 + yoe * 365 + yoe / 4 - yoe / 100 + doy - 719468;
}

static void al_time_civil_from_days(int64_t z, int64_t *y, int64_t *m,
                                    int64_t *d) {
  int64_t era;
  int64_t doe;
  int64_t yoe;
  int64_t doy;
  int64_t mp;
  z += 719468;
  era = (z >= 0 ? z : z - 146096) / 146097;
  doe = z - era * 146097;
  yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  mp = (5 * doy + 2) / 153;
  *d = doy - (153 * mp + 2) / 5 + 1;
  *m = mp < 10 ? mp + 3 : mp - 9;
  *y = yoe + era * 400 + (*m <= 2 ? 1 : 0);
}

/* Splits Unix microseconds (DateTime range) into days since 1970, a civil
 * date and microseconds of the day. */
static int32_t al_time_split(int64_t us, int64_t *days, int64_t *y,
                             int64_t *m, int64_t *d, int64_t *rem) {
  if (us < AL_TIME_MIN_US || us > AL_TIME_MAX_US)
    return 0;
  *days = us / AL_TIME_US_PER_DAY;
  *rem = us % AL_TIME_US_PER_DAY;
  if (*rem < 0) {
    *rem += AL_TIME_US_PER_DAY;
    *days -= 1;
  }
  al_time_civil_from_days(*days, y, m, d);
  return 1;
}

static void al_time_put_digits(al_stdlib_writer *w, int64_t value,
                               uint32_t width) {
  uint8_t text[8];
  uint32_t k;
  for (k = 0u; k < width; ++k) {
    text[width - 1u - k] = (uint8_t)('0' + value % 10);
    value /= 10;
  }
  al_stdlib_put(w, text, width);
}

static int32_t al_time_digit(uint8_t b) { return b >= '0' && b <= '9'; }

static int64_t al_time_d2(const uint8_t *x) {
  return (int64_t)(x[0] - '0') * 10 + (int64_t)(x[1] - '0');
}

/* ISO 8601 as Rails writes "exp": yyyy-MM-ddTHH:mm:ss[.f{1,7}](Z|z|+HH:mm|-HH:mm).
 * Unix microseconds; sub-microsecond digits
 * round up so `now >= exp` equals the tick comparison. */
static int32_t al_time_parse_iso8601(const uint8_t *x, uint32_t n,
                                     int64_t *out) {
  static const uint8_t positions[14] = {0u,  1u,  2u,  3u,  5u,  6u,  8u,
                                        9u,  11u, 12u, 14u, 15u, 17u, 18u};
  uint32_t zone;
  uint32_t date_time_length;
  int64_t offset_sign = 0;
  int64_t offset_minutes = 0;
  int64_t fractional = 0;
  int64_t year, month, day, hour, minute, second, ticks, relative;
  uint32_t k;
  if (n < 20u)
    return 0;
  if (x[n - 1u] == 'Z' || x[n - 1u] == 'z') {
    zone = n - 1u;
    date_time_length = zone;
  } else {
    uint32_t last = UINT32_MAX;
    int64_t hours;
    int64_t minutes;
    for (k = 0u; k < n; ++k)
      if (x[k] == '+' || x[k] == '-')
        last = k;
    if (last == UINT32_MAX)
      return 0;
    zone = last;
    if (zone < 19u || n - zone != 6u || x[zone + 3u] != ':' ||
        !al_time_digit(x[zone + 1u]) || !al_time_digit(x[zone + 2u]) ||
        !al_time_digit(x[zone + 4u]) || !al_time_digit(x[zone + 5u]))
      return 0;
    hours = al_time_d2(x + zone + 1u);
    minutes = al_time_d2(x + zone + 4u);
    if (minutes > 59 || hours > 14 || (hours == 14 && minutes != 0))
      return 0;
    offset_sign = x[zone] == '+' ? 1 : -1;
    offset_minutes = hours * 60 + minutes;
    date_time_length = zone;
  }
  if (date_time_length < 19u || x[4] != '-' || x[7] != '-' ||
      (x[10] != 'T' && x[10] != 't') || x[13] != ':' || x[16] != ':')
    return 0;
  for (k = 0u; k < 14u; ++k)
    if (!al_time_digit(x[positions[k]]))
      return 0;
  if (date_time_length != 19u) {
    uint32_t digits;
    if (x[19] != '.' || date_time_length < 21u || date_time_length > 27u)
      return 0;
    digits = date_time_length - 20u;
    for (k = 0u; k < digits; ++k) {
      if (!al_time_digit(x[20u + k]))
        return 0;
      fractional = fractional * 10 + (int64_t)(x[20u + k] - '0');
    }
    for (; digits < 7u; ++digits)
      fractional *= 10;
  }
  year = al_time_d2(x) * 100 + al_time_d2(x + 2);
  month = al_time_d2(x + 5);
  day = al_time_d2(x + 8);
  hour = al_time_d2(x + 11);
  minute = al_time_d2(x + 14);
  second = al_time_d2(x + 17);
  if (year == 0 || month == 0 || month > 12 || hour > 23 || minute > 59 ||
      second > 59 || day == 0 || day > al_time_month_days(year, month))
    return 0;
  ticks = (al_time_days_from_civil(year, month, day) + 719162) *
              INT64_C(864000000000) +
          (hour * 3600 + minute * 60 + second) * INT64_C(10000000) +
          fractional - offset_sign * offset_minutes * INT64_C(600000000);
  if (ticks < 0 || ticks > INT64_C(3155378975999999999))
    return 0;
  relative = ticks - INT64_C(621355968000000000);
  *out = relative >= 0 ? (relative + 9) / 10 : relative / 10;
  return 1;
}

/* ------------------------------------------------------------ dispatch */

/* Decrypts into the free stack above the cursor (both passes), so the plan
 * pass learns Some or None and no output is written past a None extent.
 * Returns 1 / 0 (tag mismatch) / -1 (failure already set). */
static int32_t al_stdlib2_aes_open(al_owning_stack_context *ctx,
                                   const al_stdlib_argument *a,
                                   const uint32_t *n, uint32_t error_id,
                                   uint8_t **plaintext) {
  uint8_t tag[16];
  uint8_t *scratch;
  uint32_t k;
  int32_t result;
  if ((uint64_t)ctx->cursor_bytes + n[2] > ctx->stack_capacity_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_STACK_CAPACITY, error_id,
                          n[2], ctx->stack_capacity_bytes);
    return -1;
  }
  for (k = 0u; k < 16u; ++k)
    tag[k] = al_stdlib_data(ctx, a[3])[k];
  scratch = ctx->stack_data + ctx->cursor_bytes;
  result = al_os_aes_gcm(0, al_stdlib_data(ctx, a[0]), al_stdlib_data(ctx, a[1]),
                         al_stdlib_data(ctx, a[4]), n[4],
                         al_stdlib_data(ctx, a[2]), n[2], scratch, tag);
  *plaintext = scratch;
  if (result < 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 1u, 0u);
    return -1;
  }
  return result;
}

static void al_stdlib2_scrub(uint8_t *data, uint32_t length) {
  volatile uint8_t *bytes = data;
  uint32_t k;
  for (k = 0u; k < length; ++k)
    bytes[k] = 0u;
}

/* .NET Char.IsWhiteSpace for one code point: the set String.Trim removes. */
static int32_t al_stdlib2_white_space(uint32_t c) {
  return (c >= 0x09u && c <= 0x0Du) || c == 0x20u || c == 0x85u || c == 0xA0u ||
         c == 0x1680u || (c >= 0x2000u && c <= 0x200Au) || c == 0x2028u ||
         c == 0x2029u || c == 0x202Fu || c == 0x205Fu || c == 0x3000u;
}

/* The code point of the well-formed UTF-8 sequence at x[k], and its length. */
static uint32_t al_stdlib2_decode(const uint8_t *x, uint32_t k, uint32_t *size) {
  const uint8_t lead = x[k];
  if (lead < 0x80u) { *size = 1u; return lead; }
  if (lead < 0xE0u) { *size = 2u; return ((uint32_t)(lead & 0x1Fu) << 6) | (x[k + 1u] & 0x3Fu); }
  if (lead < 0xF0u) { *size = 3u; return ((uint32_t)(lead & 0x0Fu) << 12) | ((uint32_t)(x[k + 1u] & 0x3Fu) << 6) | (x[k + 2u] & 0x3Fu); }
  *size = 4u;
  return ((uint32_t)(lead & 0x07u) << 18) | ((uint32_t)(x[k + 1u] & 0x3Fu) << 12) | ((uint32_t)(x[k + 2u] & 0x3Fu) << 6) | (x[k + 3u] & 0x3Fu);
}

/* string.trim: the byte range left after removing white space at both ends. */
static void al_stdlib2_trim_range(const uint8_t *x, uint32_t n, uint32_t *first, uint32_t *end) {
  uint32_t start = 0u, stop = n, size = 0u;
  while (start < n && al_stdlib2_white_space(al_stdlib2_decode(x, start, &size)))
    start += size;
  while (stop > start) {
    uint32_t lead = stop - 1u;
    while (lead > start && (x[lead] & 0xC0u) == 0x80u)
      --lead;
    if (!al_stdlib2_white_space(al_stdlib2_decode(x, lead, &size)))
      break;
    stop = lead;
  }
  *first = start;
  *end = stop;
}

/* Slice 2 operations; returns 1 on success, 0 after setting a failure or for
 * an unknown id (the caller reports it). */
static int32_t al_stdlib2_emit(al_owning_stack_context *ctx, int32_t op,
                               const al_stdlib_argument *a, const uint32_t *n,
                               al_stdlib_writer *w, uint32_t error_id) {
  const uint8_t *x = al_stdlib_data(ctx, a[0]);
  const uint8_t *y = al_stdlib_data(ctx, a[1]);
  uint32_t ks, ke, vs, ve, cursor;
  switch (op) {
  case AL_STDLIB_STRING_TRIM: {
    uint32_t first, end;
    al_stdlib2_trim_range(x, n[0], &first, &end);
    al_stdlib_text(w, x + first, end - first);
    return 1;
  }
  case AL_STDLIB_HMAC_SHA1:
    al_stdlib_begin_text(w, 20u);
    if (w->base != 0 &&
        al_os_hmac(1, x, n[0], y, n[1], w->base + w->extent) != 1) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 1u, 0u);
      return 0;
    }
    w->extent += 20u;
    al_stdlib_pad(w);
    return 1;
  case AL_STDLIB_AES_SEAL: {
    if (n[0] != 32u || n[1] != 12u) {
      al_stdlib_none(w);
      return 1;
    }
    if ((uint64_t)n[2] + 16u > (uint64_t)INT32_MAX)
      break;
    al_stdlib_some(w);
    al_stdlib_begin_text(w, n[2] + 16u);
    if (w->base != 0) {
      uint8_t *out = w->base + w->extent;
      if (al_os_aes_gcm(1, x, y, al_stdlib_data(ctx, a[3]), n[3],
                        al_stdlib_data(ctx, a[2]), n[2], out,
                        out + n[2]) != 1) {
        al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 1u,
                              0u);
        return 0;
      }
    }
    w->extent += (uint64_t)n[2] + 16u;
    al_stdlib_pad(w);
    return 1;
  }
  case AL_STDLIB_AES_OPEN: {
    uint8_t *plaintext = 0;
    int32_t result;
    if (n[0] != 32u || n[1] != 12u || n[3] != 16u) {
      al_stdlib_none(w);
      return 1;
    }
    result = al_stdlib2_aes_open(ctx, a, n, error_id, &plaintext);
    if (result < 0)
      return 0;
    if (result == 0) {
      al_stdlib_none(w);
    } else {
      al_stdlib_some(w);
      al_stdlib_text(w, plaintext, n[2]);
    }
    al_stdlib2_scrub(plaintext, n[2]);
    return 1;
  }
  case AL_STDLIB_JSON_PARSE: {
    const uint32_t start = al_json_skip(x, n[0], 0u);
    const int64_t end = al_json_value_end(x, n[0], start, 0u);
    if (end < 0 || al_json_skip(x, n[0], (uint32_t)end) != n[0]) {
      al_stdlib_none(w);
    } else {
      al_stdlib_some(w);
      al_stdlib_text(w, x + start, (uint32_t)end - start);
    }
    return 1;
  }
  case AL_STDLIB_JSON_FIELD: {
    uint32_t found = 0u;
    uint32_t fs = 0u;
    uint32_t fe = 0u;
    if (n[0] > 0u && x[0] == '{') {
      cursor = al_json_skip(x, n[0], 1u);
      while (al_json_next(x, n[0], &cursor, &ks, &ke, &vs, &ve)) {
        if (al_json_key_equals(x, n[0], ks, ke, y, n[1])) {
          found = 1u; /* the last duplicate wins, as Ruby's JSON */
          fs = vs;
          fe = ve;
        }
      }
    }
    if (found) {
      al_stdlib_some(w);
      al_stdlib_text(w, x + fs, fe - fs);
    } else {
      al_stdlib_none(w);
    }
    return 1;
  }
  case AL_STDLIB_JSON_INDEX:
  case AL_STDLIB_JSON_KEY_AT: {
    const int64_t wanted = al_stdlib_int(ctx, a[1]);
    int64_t index = 0;
    if (wanted >= 0 && n[0] > 0u &&
        (x[0] == '{' || (x[0] == '[' && op == AL_STDLIB_JSON_INDEX))) {
      cursor = al_json_skip(x, n[0], 1u);
      while (al_json_next(x, n[0], &cursor, &ks, &ke, &vs, &ve)) {
        if (index++ == wanted) {
          al_stdlib_some(w);
          if (op == AL_STDLIB_JSON_INDEX)
            al_stdlib_text(w, x + vs, ve - vs);
          else
            al_json_put_decoded(w, x, n[0], ks, ke);
          return 1;
        }
      }
    }
    al_stdlib_none(w);
    return 1;
  }
  case AL_STDLIB_JSON_COUNT: {
    int64_t count = 0;
    if (n[0] == 0u || (x[0] != '{' && x[0] != '[')) {
      al_stdlib_none(w);
      return 1;
    }
    cursor = al_json_skip(x, n[0], 1u);
    while (al_json_next(x, n[0], &cursor, &ks, &ke, &vs, &ve))
      ++count;
    al_stdlib_some_int(w, count);
    return 1;
  }
  case AL_STDLIB_JSON_TEXT:
    if (n[0] >= 2u && x[0] == '"') {
      al_stdlib_some(w);
      al_json_put_decoded(w, x, n[0], 0u, n[0]);
    } else {
      al_stdlib_none(w);
    }
    return 1;
  case AL_STDLIB_JSON_INT: {
    uint32_t i = 0u;
    int32_t negative = 0;
    uint64_t magnitude = 0u;
    if (n[0] > 0u && x[0] == '-') {
      negative = 1;
      i = 1u;
    }
    if (i == n[0]) {
      al_stdlib_none(w);
      return 1;
    }
    for (; i < n[0]; ++i) {
      if (x[i] < '0' || x[i] > '9' || magnitude > 922337203685477580ull ||
          (magnitude == 922337203685477580ull &&
           (uint64_t)(x[i] - '0') > (negative ? 8u : 7u))) {
        al_stdlib_none(w);
        return 1;
      }
      magnitude = magnitude * 10u + (uint64_t)(x[i] - '0');
    }
    al_stdlib_some_int(w, negative ? (int64_t)(0u - magnitude)
                                   : (int64_t)magnitude);
    return 1;
  }
  case AL_STDLIB_JSON_BOOL:
    if (n[0] == 4u && x[0] == 't')
      al_stdlib_some_int(w, 1);
    else if (n[0] == 5u && x[0] == 'f')
      al_stdlib_some_int(w, 0);
    else
      al_stdlib_none(w);
    return 1;
  case AL_STDLIB_JSON_ENCODE:
    al_stdlib_text(w, x, n[0]);
    return 1;
  case AL_STDLIB_JSON_QUOTE:
    if ((uint64_t)n[0] * 6u + 2u > (uint64_t)INT32_MAX)
      break;
    al_json_quote(w, x, n[0]);
    return 1;
  case AL_STDLIB_COOKIE_FIND: {
    uint32_t begin, end, equal;
    cursor = 0u;
    while (al_cookie_next(x, n[0], &cursor, &begin, &end, &equal)) {
      if (al_cookie_name_is(x, begin, equal, y, n[1])) {
        al_stdlib_some(w);
        al_stdlib_text(w, x + equal + 1u, end - equal - 1u);
        return 1;
      }
    }
    al_stdlib_none(w);
    return 1;
  }
  case AL_STDLIB_COOKIE_ESCAPE: {
    const uint64_t length = al_cookie_escaped_length(x, n[0]);
    if (length > (uint64_t)INT32_MAX)
      break;
    al_stdlib_begin_text(w, (uint32_t)length);
    al_cookie_put_escaped(w, x, n[0]);
    al_stdlib_pad(w);
    return 1;
  }
  case AL_STDLIB_COOKIE_UNESCAPE:
    al_cookie_unescape(w, x, n[0]);
    return 1;
  case AL_STDLIB_SET_COOKIE: {
    const uint8_t *path = al_stdlib_data(ctx, a[2]);
    const uint8_t *expires = al_stdlib_data(ctx, a[3]);
    const uint8_t *same_site = al_stdlib_data(ctx, a[4]);
    const int64_t secure = al_stdlib_int(ctx, a[5]);
    const int64_t http_only = al_stdlib_int(ctx, a[6]);
    uint64_t length;
    uint32_t k;
    int32_t valid = n[0] > 0u && al_cookie_attribute_text(path, n[2]) &&
                    al_cookie_attribute_text(expires, n[3]) &&
                    al_cookie_attribute_text(same_site, n[4]);
    for (k = 0u; valid && k < n[0]; ++k)
      valid = al_cookie_token_byte(x[k]);
    if (!valid) {
      al_stdlib_none(w);
      return 1;
    }
    length = (uint64_t)n[0] + 1u + al_cookie_escaped_length(y, n[1]) +
             (n[2] != 0u ? 7u + (uint64_t)n[2] : 0u) +
             (n[4] != 0u ? 11u + (uint64_t)n[4] : 0u) +
             (n[3] != 0u ? 10u + (uint64_t)n[3] : 0u) +
             (secure != 0 ? 8u : 0u) + (http_only != 0 ? 10u : 0u);
    if (length > (uint64_t)INT32_MAX)
      break;
    al_stdlib_some(w);
    al_stdlib_begin_text(w, (uint32_t)length);
    al_stdlib_put(w, x, n[0]);
    al_stdlib_put_byte(w, '=');
    al_cookie_put_escaped(w, y, n[1]);
    if (n[2] != 0u) {
      al_stdlib_put_ascii(w, "; Path=");
      al_stdlib_put(w, path, n[2]);
    }
    if (n[4] != 0u) {
      al_stdlib_put_ascii(w, "; SameSite=");
      al_stdlib_put(w, same_site, n[4]);
    }
    if (n[3] != 0u) {
      al_stdlib_put_ascii(w, "; Expires=");
      al_stdlib_put(w, expires, n[3]);
    }
    if (secure != 0)
      al_stdlib_put_ascii(w, "; Secure");
    if (http_only != 0)
      al_stdlib_put_ascii(w, "; HttpOnly");
    al_stdlib_pad(w);
    return 1;
  }
  case AL_STDLIB_TIME_ADD_YEARS: {
    const int64_t years = al_stdlib_int(ctx, a[1]);
    int64_t days, yy, mm, dd, rem;
    if (!al_time_split(al_stdlib_int(ctx, a[0]), &days, &yy, &mm, &dd,
                       &rem) ||
        years < -10000 || years > 10000 || yy + years < 1 ||
        yy + years > 9999) {
      al_stdlib_none(w);
      return 1;
    }
    yy += years;
    if (mm == 2 && dd == 29 && !al_time_leap(yy))
      dd = 28;
    al_stdlib_some_int(w, al_time_days_from_civil(yy, mm, dd) *
                                  AL_TIME_US_PER_DAY +
                              rem);
    return 1;
  }
  case AL_STDLIB_TIME_RFC1123:
  case AL_STDLIB_TIME_ISO8601: {
    static const char weekdays[] = "SunMonTueWedThuFriSat";
    static const char months[] = "JanFebMarAprMayJunJulAugSepOctNovDec";
    int64_t days, yy, mm, dd, rem;
    if (!al_time_split(al_stdlib_int(ctx, a[0]), &days, &yy, &mm, &dd,
                       &rem)) {
      al_stdlib_none(w);
      return 1;
    }
    al_stdlib_some(w);
    if (op == AL_STDLIB_TIME_RFC1123) {
      const int64_t weekday = ((days % 7) + 7 + 4) % 7;
      al_stdlib_begin_text(w, 29u);
      al_stdlib_put(w, (const uint8_t *)weekdays + weekday * 3, 3u);
      al_stdlib_put_ascii(w, ", ");
      al_time_put_digits(w, dd, 2u);
      al_stdlib_put_byte(w, ' ');
      al_stdlib_put(w, (const uint8_t *)months + (mm - 1) * 3, 3u);
      al_stdlib_put_byte(w, ' ');
      al_time_put_digits(w, yy, 4u);
      al_stdlib_put_byte(w, ' ');
    } else {
      al_stdlib_begin_text(w, 24u);
      al_time_put_digits(w, yy, 4u);
      al_stdlib_put_byte(w, '-');
      al_time_put_digits(w, mm, 2u);
      al_stdlib_put_byte(w, '-');
      al_time_put_digits(w, dd, 2u);
      al_stdlib_put_byte(w, 'T');
    }
    al_time_put_digits(w, rem / INT64_C(3600000000), 2u);
    al_stdlib_put_byte(w, ':');
    al_time_put_digits(w, rem / INT64_C(60000000) % 60, 2u);
    al_stdlib_put_byte(w, ':');
    al_time_put_digits(w, rem / INT64_C(1000000) % 60, 2u);
    if (op == AL_STDLIB_TIME_RFC1123) {
      al_stdlib_put_ascii(w, " GMT");
    } else {
      al_stdlib_put_byte(w, '.');
      al_time_put_digits(w, rem / 1000 % 1000, 3u);
      al_stdlib_put_byte(w, 'Z');
    }
    al_stdlib_pad(w);
    return 1;
  }
  case AL_STDLIB_TIME_PARSE_ISO8601: {
    int64_t micros = 0;
    if (al_time_parse_iso8601(x, n[0], &micros))
      al_stdlib_some_int(w, micros);
    else
      al_stdlib_none(w);
    return 1;
  }
  default:
    break;
  }
  return 0;
}

/* -1, 0 or 1 by UTF-8 bytes, unsigned: the order of Unicode code points and
 * of Ruby's String#<=>. */
static int64_t al_stdlib2_compare(const uint8_t *x, uint32_t nx,
                                  const uint8_t *y, uint32_t ny) {
  const uint32_t shorter = nx < ny ? nx : ny;
  uint32_t k;
  for (k = 0u; k < shorter; ++k)
    if (x[k] != y[k])
      return x[k] < y[k] ? -1 : 1;
  return nx == ny ? 0 : (nx < ny ? -1 : 1);
}

/* string.url-safe? (TrustedValues.isSafeUrl): no C0 control or DEL, no leading
 * or trailing space, and a scheme before any '/', '?' or '#' only if it is
 * http, https, mailto or tel (ASCII case-insensitive). */
static int64_t al_stdlib2_url_safe(const uint8_t *x, uint32_t n) {
  uint32_t k, stop = n;
  for (k = 0u; k < n; ++k)
    if (x[k] < 0x20u || x[k] == 0x7fu)
      return 0;
  if (n > 0u && (x[0] == ' ' || x[n - 1u] == ' '))
    return 0;
  for (k = 0u; k < n; ++k)
    if (x[k] == ':' || x[k] == '/' || x[k] == '?' || x[k] == '#') {
      stop = k;
      break;
    }
  if (stop == n || x[stop] != ':')
    return 1;
  {
    static const char *const schemes[4] = {"http", "https", "mailto", "tel"};
    uint32_t s;
    for (s = 0u; s < 4u; ++s) {
      const char *scheme = schemes[s];
      uint32_t j = 0u;
      while (j < stop && scheme[j] != 0 &&
             (x[j] >= 'A' && x[j] <= 'Z' ? x[j] + 32u : x[j]) == (uint8_t)scheme[j])
        ++j;
      if (j == stop && scheme[j] == 0)
        return 1;
    }
  }
  return 0;
}

/* Int and Bool results: json.null?, cookie.count, string ordering and
 * string.url-safe?. */
static int64_t al_stdlib2_scalar(al_owning_stack_context *ctx, int32_t op,
                                 const al_stdlib_argument *a,
                                 const uint32_t *n) {
  const uint8_t *x = al_stdlib_data(ctx, a[0]);
  if (op == AL_STDLIB_JSON_NULL)
    return n[0] == 4u && x[0] == 'n' ? 1 : 0;
  if (op == AL_STDLIB_STRING_URL_SAFE)
    return al_stdlib2_url_safe(x, n[0]);
  if (op >= AL_STDLIB_STRING_COMPARE &&
      op <= AL_STDLIB_STRING_GREATER_OR_EQUAL) {
    const int64_t order = al_stdlib2_compare(x, n[0], al_stdlib_data(ctx, a[1]), n[1]);
    switch (op) {
    case AL_STDLIB_STRING_LESS_THAN:
      return order < 0;
    case AL_STDLIB_STRING_LESS_OR_EQUAL:
      return order <= 0;
    case AL_STDLIB_STRING_GREATER_THAN:
      return order > 0;
    case AL_STDLIB_STRING_GREATER_OR_EQUAL:
      return order >= 0;
    default:
      return order;
    }
  }
  {
    const uint8_t *name = al_stdlib_data(ctx, a[1]);
    uint32_t cursor = 0u;
    uint32_t begin, end, equal;
    int64_t count = 0;
    while (al_cookie_next(x, n[0], &cursor, &begin, &end, &equal))
      if (al_cookie_name_is(x, begin, equal, name, n[1]))
        ++count;
    return count;
  }
}

#endif

/* Portable native HTTP host for Flow programs (docs/LINUX-SERVER-PLAN.md A).
 *
 * The generated module exports two owning-stack entries:
 *   flow_http_worker_init(config: List<HttpHeader>) -> Context   once per worker
 *   flow_http_handle(request: HttpRequest, context) -> HttpResponse  per request
 * (the context is kept at the bottom of the worker arena and borrowed, not copied)
 * Each worker thread owns its listening socket (Linux: SO_REUSEPORT; Windows:
 * workers accept from one shared listening socket, the only shared kernel
 * object), its connections, arena, SQLite connection and context bytes. The
 * only shared mutable state is the lock-free cache.get/cache.put cache
 * (flow_http_cache.c, --cache-mb); there is no lock: the main thread only
 * writes each worker's own stop flag at shutdown, and workers only write their
 * own state and the cache.
 *
 * HTTP/1.1 with keep-alive and pipelining, Content-Length request bodies (no
 * chunked request bodies), Content-Length responses, no compression. Listens
 * on 127.0.0.1 only. Prints "READY <port>" once every worker has initialized.
 * Stops on SIGINT/SIGTERM (Ctrl+C / Ctrl+Break on Windows) or, with
 * --allow-control, on POST /__flow_http/shutdown. Config values (which may be
 * secrets) are passed to worker-init and never logged. */
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef SOCKET fh_socket;
#define FH_INVALID_SOCKET INVALID_SOCKET
#define fh_close_socket closesocket
#else
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <sys/epoll.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/uio.h>
#include <time.h>
#include <unistd.h>
typedef int fh_socket;
#define FH_INVALID_SOCKET (-1)
#define fh_close_socket close
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flow_http.h"
#include "flow_http_cache.h"

int32_t flow_http_worker_init(al_owning_stack_context *ctx, void *provider,
                              uint8_t *input, uint32_t input_bytes,
                              uint32_t *input_extents, uint32_t input_count,
                              uint8_t *retained, uint32_t retained_capacity);
int32_t flow_http_handle(al_owning_stack_context *ctx, void *provider,
                         uint8_t *input, uint32_t input_bytes,
                         uint32_t *input_extents, uint32_t input_count,
                         uint8_t *retained, uint32_t retained_capacity);

#define FH_MAX_WORKERS 256
#define FH_MAX_CONNECTIONS 1024
#define FH_MAX_HEADER_BYTES 65536u
#define FH_MAX_HEADERS 100u
#define FH_READ_CHUNK 65536u
#define FH_CONTROL_SHUTDOWN "/__flow_http/shutdown"

typedef struct fh_options {
  int port;
  int workers;
  int pin;
  int allow_control;
  uint32_t arena_bytes;
  uint32_t retained_bytes;
  uint64_t store_bytes;   /* per worker, a multiple of 16 KB; 0 = no store */
  uint32_t store_mb;      /* the --store-mb total, split between workers */
  uint32_t max_body_bytes;
  uint32_t cache_mb;
  const char *db_path;
  const char *config_path;
  uint8_t *config_value; /* encoded List<HttpHeader>, shared read-only after startup */
  uint32_t config_extent;
} fh_options;

typedef struct fh_connection {
  fh_socket socket;
  char remote_ip[48];
  uint8_t *in;
  uint32_t in_used, in_capacity;
  uint8_t *out;
  uint32_t out_used, out_sent, out_capacity;
  int close_after_write;
  int want_write;
} fh_connection;

typedef struct fh_worker {
  int index;
  const fh_options *options;
  fh_socket listener;
#if defined(_WIN32)
  HANDLE thread;
#else
  pthread_t thread;
  int epoll_fd;
#endif
  int started;
  volatile long stop;   /* written only by the main thread */
  volatile long state;  /* written only by this worker: 1 ready, -1 failed */
  fh_db *db;
  fh_provider provider; /* db + worker index, the generated module's provider context */
  uint8_t *stack;
  uint8_t *init_bitmap;
  uint8_t *poison_bitmap;
  uint8_t *input;
  uint32_t input_capacity;
  uint8_t *retained;
  uint8_t *store;  /* this worker's store (--store-mb total / workers), or NULL */
  uint8_t *stream; /* this worker's response stream, retained_bytes long */
  uint32_t context_bytes;
  fh_connection *connections[FH_MAX_CONNECTIONS];
  uint32_t connection_count;
  uint64_t requests;
  uint64_t failures;
  uint32_t peak_arena;    /* largest per-request arena cursor high-water */
  uint32_t peak_response; /* largest encoded HttpResponse */
  uint32_t peak_input;    /* largest encoded request + context */
} fh_worker;

/* ---------------------------------------------------------------- utility */

static void fh_log(const char *text) { fputs(text, stderr); fflush(stderr); }

static int64_t fh_now_microseconds(void) {
#if defined(_WIN32)
  FILETIME ft;
  ULARGE_INTEGER value;
  GetSystemTimePreciseAsFileTime(&ft);
  value.LowPart = ft.dwLowDateTime;
  value.HighPart = ft.dwHighDateTime;
  return (int64_t)(value.QuadPart / 10u) - 11644473600000000LL;
#else
  struct timespec ts;
  clock_gettime(CLOCK_REALTIME, &ts);
  return (int64_t)ts.tv_sec * 1000000LL + ts.tv_nsec / 1000;
#endif
}

/* Worker buffers (arena, input, retained output) are reservations sized for
 * the largest legal request, committed lazily: fresh pages read as zero and
 * only the pages a request actually touches become resident (Windows commits
 * on first touch too). Resident size is therefore each buffer's high-water
 * mark, reported per worker at shutdown. */
static void *fh_alloc(size_t bytes) {
#if defined(_WIN32)
  return VirtualAlloc(NULL, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
#else
  void *p = mmap(0, bytes, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
  return p == MAP_FAILED ? 0 : p;
#endif
}

static void fh_free(void *p, size_t bytes) {
  if (p == 0) return;
#if defined(_WIN32)
  (void)bytes;
  VirtualFree(p, 0, MEM_RELEASE);
#else
  munmap(p, bytes);
#endif
}

static int fh_set_nonblocking(fh_socket s) {
#if defined(_WIN32)
  u_long on = 1;
  return ioctlsocket(s, FIONBIO, &on) == 0;
#else
  int flags = fcntl(s, F_GETFL, 0);
  return flags >= 0 && fcntl(s, F_SETFL, flags | O_NONBLOCK) == 0;
#endif
}

static int fh_would_block(void) {
#if defined(_WIN32)
  return WSAGetLastError() == WSAEWOULDBLOCK;
#else
  return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR;
#endif
}

static fh_socket fh_listen(int port, int reuse_port) {
  struct sockaddr_in address;
  fh_socket s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  int on = 1;
  if (s == FH_INVALID_SOCKET) return FH_INVALID_SOCKET;
#if defined(_WIN32)
  (void)reuse_port;
  setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&on, sizeof(on));
#else
  setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &on, sizeof(on));
  if (reuse_port && setsockopt(s, SOL_SOCKET, SO_REUSEPORT, &on, sizeof(on)) != 0) {
    fh_close_socket(s);
    return FH_INVALID_SOCKET;
  }
#endif
  memset(&address, 0, sizeof(address));
  address.sin_family = AF_INET;
  address.sin_port = htons((unsigned short)port);
  address.sin_addr.s_addr = htonl(INADDR_LOOPBACK); /* loopback only */
  if (bind(s, (struct sockaddr *)&address, sizeof(address)) != 0 || listen(s, 1024) != 0 ||
      !fh_set_nonblocking(s)) {
    fh_close_socket(s);
    return FH_INVALID_SOCKET;
  }
  return s;
}

static void fh_pin_worker(int index) {
#if defined(_WIN32)
  DWORD_PTR process_mask = 0, system_mask = 0, chosen = 0;
  int seen = 0, bit, count = 0;
  if (!GetProcessAffinityMask(GetCurrentProcess(), &process_mask, &system_mask)) return;
  for (bit = 0; bit < 64; ++bit) if (process_mask & ((DWORD_PTR)1 << bit)) ++count;
  if (count == 0) return;
  for (bit = 0; bit < 64; ++bit) {
    if (!(process_mask & ((DWORD_PTR)1 << bit))) continue;
    if (seen++ == index % count) { chosen = (DWORD_PTR)1 << bit; break; }
  }
  if (chosen) SetThreadAffinityMask(GetCurrentThread(), chosen);
#else
  cpu_set_t allowed, chosen;
  int cpu, seen = 0, count;
  if (sched_getaffinity(0, sizeof(allowed), &allowed) != 0) return;
  count = CPU_COUNT(&allowed);
  if (count == 0) return;
  CPU_ZERO(&chosen);
  for (cpu = 0; cpu < CPU_SETSIZE; ++cpu) {
    if (!CPU_ISSET(cpu, &allowed)) continue;
    if (seen++ == index % count) { CPU_SET(cpu, &chosen); break; }
  }
  pthread_setaffinity_np(pthread_self(), sizeof(chosen), &chosen);
#endif
}

/* ------------------------------------------------------- module execution */

static void fh_context_reset(fh_worker *worker, al_owning_stack_context *ctx) {
  memset(ctx, 0, sizeof(*ctx));
  ctx->abi_version = AL_OWNING_STACK_ABI_VERSION;
  ctx->stack_capacity_bytes = worker->options->arena_bytes;
  ctx->stack_data = worker->stack;
#if !AL_OWNING_TRUSTED_GENERATED
  ctx->init_bitmap = worker->init_bitmap;
  ctx->poison_bitmap = worker->poison_bitmap;
  ctx->init_bitmap_bytes = worker->options->arena_bytes / 8u;
#endif
}

static int fh_worker_init(fh_worker *worker) {
  al_owning_stack_context ctx;
  uint32_t extent = worker->options->config_extent;
  int32_t status;
  char reason[160];
  memcpy(worker->input, worker->options->config_value, extent);
  fh_context_reset(worker, &ctx);
  fh_cache_enter((uint32_t)worker->index);
  status = flow_http_worker_init(&ctx, &worker->provider, worker->input, extent, &extent, 1u,
                                 worker->retained, worker->options->retained_bytes);
  fh_cache_leave((uint32_t)worker->index);
  if (status != 0 || ctx.status != 0) {
    snprintf(reason, sizeof(reason), "worker %d: worker-init failed status=%u error_id=%u\n",
             worker->index, ctx.status, ctx.error_id);
    fh_log(reason);
    return 0;
  }
  /* The context lives in the worker's arena for its lifetime, at [0, extent):
   * the protected prefix below every request's mark. flow_http_handle borrows
   * it in place as its second input (extent in the extents table) instead of
   * copying it into each request. */
  worker->context_bytes = (uint32_t)ctx.retained_copy_bytes;
  if (worker->context_bytes == 0u || (worker->context_bytes & 7u) != 0u ||
      worker->context_bytes > worker->options->arena_bytes / 2u) {
    fh_log("worker-init result has an unusable extent\n");
    return 0;
  }
  memcpy(worker->stack, worker->retained, worker->context_bytes);
  memset(worker->retained, 0, worker->context_bytes);
  return 1;
}

/* ------------------------------------------------------------- HTTP layer */

static const char *fh_reason(int status) {
  switch (status) {
    case 200: return "OK"; case 201: return "Created"; case 202: return "Accepted";
    case 204: return "No Content"; case 301: return "Moved Permanently"; case 302: return "Found";
    case 303: return "See Other"; case 304: return "Not Modified"; case 307: return "Temporary Redirect";
    case 400: return "Bad Request"; case 401: return "Unauthorized"; case 403: return "Forbidden";
    case 404: return "Not Found"; case 405: return "Method Not Allowed"; case 409: return "Conflict";
    case 413: return "Content Too Large"; case 422: return "Unprocessable Content";
    case 431: return "Request Header Fields Too Large"; case 500: return "Internal Server Error";
    case 501: return "Not Implemented"; case 503: return "Service Unavailable";
    default: return "Status";
  }
}

static int fh_out_reserve(fh_connection *c, uint32_t extra) {
  uint32_t need = c->out_used + extra, capacity = c->out_capacity ? c->out_capacity : 16384u;
  uint8_t *grown;
  if (need <= c->out_capacity) return 1;
  while (capacity < need) capacity *= 2u;
  grown = (uint8_t *)realloc(c->out, capacity);
  if (grown == 0) return 0;
  c->out = grown;
  c->out_capacity = capacity;
  return 1;
}

static void fh_out_append(fh_connection *c, const void *data, uint32_t length) {
  if (!fh_out_reserve(c, length)) { c->close_after_write = 1; return; }
  memcpy(c->out + c->out_used, data, length);
  c->out_used += length;
}

#define FH_DIRECT_BODY_BYTES 16384u

/* Queues a response body. A large body is sent straight from the worker's
 * retained buffer together with the pending output (one gather send) when the
 * connection is not already waiting to write; only the part the socket does
 * not take is copied into the connection's buffer, since the retained buffer
 * is reused by the next request. */
static void fh_out_body(fh_connection *c, const uint8_t *body, uint32_t length) {
  uint32_t pending = c->out_used - c->out_sent;
  long sent;
  if (length < FH_DIRECT_BODY_BYTES || c->want_write) { fh_out_append(c, body, length); return; }
#if defined(_WIN32)
  {
    WSABUF buffers[2];
    DWORD count = 0;
    buffers[0].buf = (char *)c->out + c->out_sent; buffers[0].len = pending;
    buffers[1].buf = (char *)body; buffers[1].len = length;
    sent = WSASend(c->socket, buffers, 2, &count, 0, 0, 0) == 0 ? (long)count : -1;
  }
#else
  {
    struct iovec buffers[2];
    buffers[0].iov_base = c->out + c->out_sent; buffers[0].iov_len = pending;
    buffers[1].iov_base = (void *)body; buffers[1].iov_len = length;
    sent = (long)sendmsg(c->socket, &(struct msghdr){.msg_iov = buffers, .msg_iovlen = 2}, MSG_NOSIGNAL);
  }
#endif
  /* Errors other than would-block surface on the next fh_flush. */
  if (sent < 0) sent = 0;
  if ((uint32_t)sent < pending) {
    c->out_sent += (uint32_t)sent;
    fh_out_append(c, body, length);
    return;
  }
  c->out_used = c->out_sent = 0u;
  fh_out_append(c, body + ((uint32_t)sent - pending), length - ((uint32_t)sent - pending));
}

static void fh_simple_response(fh_connection *c, int status, const char *body, int close) {
  char head[256];
  int n = snprintf(head, sizeof(head), "HTTP/1.1 %d %s\r\ncontent-type: text/plain\r\ncontent-length: %u\r\n%s\r\n",
                   status, fh_reason(status), (unsigned)strlen(body), close ? "connection: close\r\n" : "");
  fh_out_append(c, head, (uint32_t)n);
  fh_out_append(c, body, (uint32_t)strlen(body));
  if (close) c->close_after_write = 1;
}

static int fh_header_text_ok(const uint8_t *text, uint32_t length) {
  uint32_t i;
  for (i = 0u; i < length; ++i) if (text[i] == '\r' || text[i] == '\n' || text[i] == 0) return 0;
  return 1;
}

static int fh_ieq(const uint8_t *a, uint32_t length, const char *b) {
  uint32_t i;
  if (strlen(b) != length) return 0;
  for (i = 0u; i < length; ++i) {
    uint8_t x = a[i];
    if (x >= 'A' && x <= 'Z') x = (uint8_t)(x + 32);
    if (x != (uint8_t)b[i]) return 0;
  }
  return 1;
}

/* Writes the decoded HttpResponse; returns 0 when the value is malformed. */
/* HttpResponse is status, headers, body, streamed. A streamed response's body
 * is the request's response stream followed by `body`; otherwise the stream
 * is dropped. */
static int fh_write_response(fh_connection *c, const uint8_t *value, uint32_t extent,
                             const uint8_t *stream, uint32_t stream_used, int head_only, int close) {
  fh_reader r = {value, extent, 0u, 0};
  int64_t status = fh_get_i64(&r);
  uint32_t headers = fh_get_count(&r), i, body_length;
  const uint8_t *body;
  int64_t streamed;
  char line[128];
  int n;
  uint32_t mark = c->out_used;
  if (r.failed || status < 100 || status > 999) return 0;
  n = snprintf(line, sizeof(line), "HTTP/1.1 %d %s\r\n", (int)status, fh_reason((int)status));
  fh_out_append(c, line, (uint32_t)n);
  for (i = 0u; i < headers; ++i) {
    uint32_t name_length, value_length;
    const uint8_t *name = fh_get_bytes(&r, &name_length);
    const uint8_t *text = fh_get_bytes(&r, &value_length);
    if (r.failed || name_length == 0u || !fh_header_text_ok(name, name_length) || !fh_header_text_ok(text, value_length)) {
      c->out_used = mark;
      return 0;
    }
    if (fh_ieq(name, name_length, "content-length") || fh_ieq(name, name_length, "connection") ||
        fh_ieq(name, name_length, "transfer-encoding"))
      continue; /* the host owns framing */
    fh_out_append(c, name, name_length);
    fh_out_append(c, ": ", 2u);
    fh_out_append(c, text, value_length);
    fh_out_append(c, "\r\n", 2u);
  }
  body = fh_get_bytes(&r, &body_length);
  streamed = fh_get_i64(&r);
  if (r.failed || (streamed != 0 && streamed != 1) || (streamed && body_length > 0xffffffffu - stream_used)) {
    c->out_used = mark;
    return 0;
  }
  if (!streamed) stream_used = 0u;
  n = snprintf(line, sizeof(line), "content-length: %u\r\n%s\r\n", stream_used + body_length, close ? "connection: close\r\n" : "");
  fh_out_append(c, line, (uint32_t)n);
  if (!head_only) {
    if (stream_used > 0u) fh_out_body(c, stream, stream_used);
    fh_out_body(c, body, body_length);
  }
  return 1;
}

typedef struct fh_header_span { const uint8_t *name; uint32_t name_length; const uint8_t *value; uint32_t value_length; } fh_header_span;

static void fh_request_shutdown(void);

/* Parses one complete request at the start of c->in. Returns the bytes it
 * consumed, 0 when more input is needed, or -1 after queuing an error
 * response that closes the connection. */
static long fh_handle_one(fh_worker *worker, fh_connection *c) {
  const uint8_t *data = c->in, *end_of_head = 0, *p, *line_end, *method, *target;
  uint32_t used = c->in_used, method_length, target_length, header_count = 0u, i;
  uint32_t head_length, body_length = 0u;
  fh_header_span headers[FH_MAX_HEADERS];
  int keep_alive, http10, chunked = 0, has_length = 0;
  const uint8_t *version;
  fh_writer w;
  uint32_t extents[2];
  al_owning_stack_context ctx;
  int32_t status;
  for (i = 0u; i + 3u < used; ++i) {
    if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') { end_of_head = data + i; break; }
  }
  if (end_of_head == 0) {
    if (used > FH_MAX_HEADER_BYTES) { fh_simple_response(c, 431, "request head too large", 1); return -1; }
    return 0;
  }
  head_length = (uint32_t)(end_of_head - data) + 4u;
  /* request line */
  line_end = (const uint8_t *)memchr(data, '\r', (size_t)(end_of_head - data) + 1u);
  method = data;
  p = (const uint8_t *)memchr(method, ' ', (size_t)(line_end - method));
  if (p == 0) { fh_simple_response(c, 400, "bad request line", 1); return -1; }
  method_length = (uint32_t)(p - method);
  target = p + 1;
  p = (const uint8_t *)memchr(target, ' ', (size_t)(line_end - target));
  if (p == 0 || method_length == 0u) { fh_simple_response(c, 400, "bad request line", 1); return -1; }
  target_length = (uint32_t)(p - target);
  version = p + 1;
  if (line_end - version != 8 || memcmp(version, "HTTP/1.", 7) != 0 || target_length == 0u) {
    fh_simple_response(c, 400, "bad request line", 1);
    return -1;
  }
  http10 = version[7] == '0';
  keep_alive = !http10;
  /* headers */
  p = line_end + 2;
  while (p < end_of_head + 2) {
    const uint8_t *colon, *value, *value_end;
    line_end = (const uint8_t *)memchr(p, '\r', (size_t)(end_of_head + 2 - p));
    if (line_end == 0 || line_end == p) break;
    colon = (const uint8_t *)memchr(p, ':', (size_t)(line_end - p));
    if (colon == 0 || colon == p || header_count == FH_MAX_HEADERS) { fh_simple_response(c, 400, "bad header", 1); return -1; }
    value = colon + 1;
    while (value < line_end && (*value == ' ' || *value == '\t')) ++value;
    value_end = line_end;
    while (value_end > value && (value_end[-1] == ' ' || value_end[-1] == '\t')) --value_end;
    headers[header_count].name = p;
    headers[header_count].name_length = (uint32_t)(colon - p);
    headers[header_count].value = value;
    headers[header_count].value_length = (uint32_t)(value_end - value);
    if (fh_ieq(p, (uint32_t)(colon - p), "content-length")) {
      uint64_t n = 0u;
      const uint8_t *d;
      if (value == value_end) { fh_simple_response(c, 400, "bad content-length", 1); return -1; }
      for (d = value; d < value_end; ++d) {
        if (*d < '0' || *d > '9' || n > 0xffffffffull) { fh_simple_response(c, 400, "bad content-length", 1); return -1; }
        n = n * 10u + (uint64_t)(*d - '0');
      }
      if (has_length && n != body_length) { fh_simple_response(c, 400, "conflicting content-length", 1); return -1; }
      if (n > worker->options->max_body_bytes) { fh_simple_response(c, 413, "body too large", 1); return -1; }
      has_length = 1;
      body_length = (uint32_t)n;
    } else if (fh_ieq(p, (uint32_t)(colon - p), "transfer-encoding")) {
      chunked = 1;
    } else if (fh_ieq(p, (uint32_t)(colon - p), "connection")) {
      if (fh_ieq(value, (uint32_t)(value_end - value), "close")) keep_alive = 0;
      else if (fh_ieq(value, (uint32_t)(value_end - value), "keep-alive")) keep_alive = 1;
    }
    ++header_count;
    p = line_end + 2;
  }
  if (chunked) { fh_simple_response(c, 501, "chunked request bodies are not supported", 1); return -1; }
  if (used - head_length < body_length) return 0;

  if (worker->options->allow_control && method_length == 4u && memcmp(method, "POST", 4) == 0 &&
      target_length == (uint32_t)strlen(FH_CONTROL_SHUTDOWN) && memcmp(target, FH_CONTROL_SHUTDOWN, target_length) == 0) {
    fh_simple_response(c, 200, "stopping", 1);
    fh_request_shutdown();
    return (long)(head_length + body_length);
  }

  /* Encode HttpRequest; the context is borrowed from the arena prefix. */
  w.data = worker->input;
  w.capacity = worker->input_capacity;
  w.used = 0u;
  w.failed = 0;
  fh_put_bytes(&w, method, method_length);
  fh_put_bytes(&w, target, target_length);
  fh_put_list_header(&w, header_count);
  for (i = 0u; i < header_count; ++i) {
    uint32_t k, at = w.used;
    fh_put_bytes(&w, headers[i].name, headers[i].name_length);
    if (!w.failed)
      for (k = 0u; k < headers[i].name_length; ++k) {
        uint8_t *ch = w.data + at + 8u + k;
        if (*ch >= 'A' && *ch <= 'Z') *ch = (uint8_t)(*ch + 32);
      }
    fh_put_bytes(&w, headers[i].value, headers[i].value_length);
  }
  fh_put_bytes(&w, data + head_length, body_length);
  fh_put_bytes(&w, c->remote_ip, (uint32_t)strlen(c->remote_ip));
  fh_put_i64(&w, fh_now_microseconds());
  if (w.failed) { fh_simple_response(c, 413, "request too large", 1); return -1; }
  extents[0] = w.used;
  extents[1] = worker->context_bytes;

  fh_context_reset(worker, &ctx);
  worker->provider.stream_used = 0u; /* every request starts with an empty response stream */
  /* The cache epoch covers the whole handle call: cache.get copies a hit into
   * the arena inside it; the response is then in the worker's own buffer. */
  fh_cache_enter((uint32_t)worker->index);
  status = flow_http_handle(&ctx, &worker->provider, worker->input, w.used, extents, 2u,
                            worker->retained, worker->options->retained_bytes);
  fh_cache_leave((uint32_t)worker->index);
  ++worker->requests;
  if (ctx.peak_cursor_bytes > worker->peak_arena) worker->peak_arena = ctx.peak_cursor_bytes;
  if (ctx.retained_copy_bytes > worker->peak_response) worker->peak_response = (uint32_t)ctx.retained_copy_bytes;
  if (w.used > worker->peak_input) worker->peak_input = w.used;
  if (status != 0 || ctx.status != 0 ||
      !fh_write_response(c, worker->retained, (uint32_t)ctx.retained_copy_bytes,
                         worker->stream, worker->provider.stream_used,
                         method_length == 4u && memcmp(method, "HEAD", 4) == 0, !keep_alive)) {
    char line[160];
    ++worker->failures;
    snprintf(line, sizeof(line), "worker %d: request failed status=%u error_id=%u\n", worker->index, ctx.status, ctx.error_id);
    fh_log(line);
    fh_simple_response(c, 500, "internal error", !keep_alive);
  }
  if (!keep_alive) c->close_after_write = 1;
  return (long)(head_length + body_length);
}

/* --------------------------------------------------------- event handling */

static void fh_connection_free(fh_connection *c) {
  if (c == 0) return;
  if (c->socket != FH_INVALID_SOCKET) fh_close_socket(c->socket);
  free(c->in);
  free(c->out);
  free(c);
}

/* Sends pending output. Returns 0 when the connection should close. */
static int fh_flush(fh_connection *c) {
  while (c->out_sent < c->out_used) {
#if defined(_WIN32)
    int sent = send(c->socket, (const char *)c->out + c->out_sent, (int)(c->out_used - c->out_sent), 0);
#else
    ssize_t sent = send(c->socket, c->out + c->out_sent, c->out_used - c->out_sent, MSG_NOSIGNAL);
#endif
    if (sent < 0) {
      if (fh_would_block()) { c->want_write = 1; return 1; }
      return 0;
    }
    c->out_sent += (uint32_t)sent;
  }
  c->out_used = c->out_sent = 0u;
  c->want_write = 0;
  return !c->close_after_write;
}

/* Reads and handles everything available. Returns 0 to close. */
static int fh_on_readable(fh_worker *worker, fh_connection *c) {
  for (;;) {
    long consumed;
    if (c->in_capacity - c->in_used < FH_READ_CHUNK / 4u) {
      uint32_t capacity = c->in_capacity ? c->in_capacity * 2u : FH_READ_CHUNK;
      uint8_t *grown;
      if (capacity > FH_MAX_HEADER_BYTES * 2u + worker->options->max_body_bytes) return 0;
      grown = (uint8_t *)realloc(c->in, capacity);
      if (grown == 0) return 0;
      c->in = grown;
      c->in_capacity = capacity;
    }
    {
#if defined(_WIN32)
      int got = recv(c->socket, (char *)c->in + c->in_used, (int)(c->in_capacity - c->in_used), 0);
#else
      ssize_t got = recv(c->socket, c->in + c->in_used, c->in_capacity - c->in_used, 0);
#endif
      if (got == 0) return 0;
      if (got < 0) {
        if (fh_would_block()) break;
        return 0;
      }
      c->in_used += (uint32_t)got;
    }
    while ((consumed = fh_handle_one(worker, c)) > 0) {
      memmove(c->in, c->in + consumed, c->in_used - (uint32_t)consumed);
      c->in_used -= (uint32_t)consumed;
      if (c->close_after_write) break;
    }
    if (consumed < 0 || c->close_after_write) { c->in_used = 0u; break; }
  }
  return fh_flush(c) || c->want_write;
}

static void fh_accept_all(fh_worker *worker) {
  for (;;) {
    struct sockaddr_in peer;
#if defined(_WIN32)
    int peer_length = sizeof(peer);
#else
    socklen_t peer_length = sizeof(peer);
#endif
    fh_socket s = accept(worker->listener, (struct sockaddr *)&peer, &peer_length);
    fh_connection *c;
    int on = 1;
    if (s == FH_INVALID_SOCKET) return;
    if (worker->connection_count == FH_MAX_CONNECTIONS || !fh_set_nonblocking(s)) { fh_close_socket(s); continue; }
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&on, sizeof(on));
    c = (fh_connection *)calloc(1u, sizeof(fh_connection));
    if (c == 0) { fh_close_socket(s); continue; }
    c->socket = s;
    inet_ntop(AF_INET, &peer.sin_addr, c->remote_ip, sizeof(c->remote_ip));
#if !defined(_WIN32)
    {
      struct epoll_event event;
      event.events = EPOLLIN | EPOLLRDHUP;
      event.data.ptr = c;
      if (epoll_ctl(worker->epoll_fd, EPOLL_CTL_ADD, s, &event) != 0) { fh_connection_free(c); continue; }
    }
#endif
    worker->connections[worker->connection_count++] = c;
  }
}

static void fh_drop_connection(fh_worker *worker, fh_connection *c) {
  uint32_t i;
  for (i = 0u; i < worker->connection_count; ++i) {
    if (worker->connections[i] == c) {
      worker->connections[i] = worker->connections[--worker->connection_count];
      break;
    }
  }
#if !defined(_WIN32)
  epoll_ctl(worker->epoll_fd, EPOLL_CTL_DEL, c->socket, 0);
#endif
  fh_connection_free(c);
}

static void fh_worker_loop(fh_worker *worker) {
#if defined(_WIN32)
  WSAPOLLFD fds[FH_MAX_CONNECTIONS + 1];
  while (!worker->stop) {
    uint32_t i, n = worker->connection_count;
    int ready;
    fds[0].fd = worker->listener;
    fds[0].events = POLLRDNORM;
    fds[0].revents = 0;
    for (i = 0u; i < n; ++i) {
      fds[i + 1].fd = worker->connections[i]->socket;
      fds[i + 1].events = (SHORT)(worker->connections[i]->want_write ? POLLWRNORM : POLLRDNORM);
      fds[i + 1].revents = 0;
    }
    ready = WSAPoll(fds, n + 1u, 100);
    if (ready <= 0) continue;
    /* Walk from the end so dropping (swap with last) keeps indexes valid. */
    for (i = n; i > 0u; --i) {
      fh_connection *c = worker->connections[i - 1u];
      SHORT ev = fds[i].revents;
      int keep = 1;
      if (ev == 0) continue;
      if (ev & (POLLERR | POLLNVAL)) keep = 0;
      else if (ev & POLLWRNORM) keep = fh_flush(c) || c->want_write;
      else if (ev & (POLLRDNORM | POLLHUP)) keep = fh_on_readable(worker, c);
      if (!keep) fh_drop_connection(worker, c);
    }
    if (fds[0].revents & POLLRDNORM) fh_accept_all(worker);
  }
#else
  struct epoll_event events[256];
  while (!worker->stop) {
    int ready = epoll_wait(worker->epoll_fd, events, 256, 100), i;
    for (i = 0; i < ready; ++i) {
      fh_connection *c = (fh_connection *)events[i].data.ptr;
      int keep = 1;
      if (c == 0) { fh_accept_all(worker); continue; }
      if (events[i].events & (EPOLLERR | EPOLLHUP)) keep = 0;
      else {
        int was_writing = c->want_write;
        if (events[i].events & EPOLLOUT) keep = fh_flush(c) || c->want_write;
        if (keep && (events[i].events & (EPOLLIN | EPOLLRDHUP))) keep = fh_on_readable(worker, c);
        if (keep && was_writing != c->want_write) {
          struct epoll_event event;
          event.events = (c->want_write ? EPOLLOUT : 0u) | EPOLLIN | EPOLLRDHUP;
          event.data.ptr = c;
          epoll_ctl(worker->epoll_fd, EPOLL_CTL_MOD, c->socket, &event);
        }
      }
      if (!keep) fh_drop_connection(worker, c);
    }
  }
#endif
}

#if defined(_WIN32)
static DWORD WINAPI fh_worker_main(LPVOID argument)
#else
static void *fh_worker_main(void *argument)
#endif
{
  fh_worker *worker = (fh_worker *)argument;
  const fh_options *options = worker->options;
  char reason[160];
  if (options->pin) fh_pin_worker(worker->index);
  worker->stack = (uint8_t *)fh_alloc(options->arena_bytes);
#if !AL_OWNING_TRUSTED_GENERATED
  worker->init_bitmap = (uint8_t *)fh_alloc(options->arena_bytes / 8u);
  worker->poison_bitmap = (uint8_t *)fh_alloc(options->arena_bytes / 8u);
  if (worker->init_bitmap == 0 || worker->poison_bitmap == 0) { worker->state = -1; goto done; }
#endif
  worker->input_capacity = FH_MAX_HEADER_BYTES * 2u + options->max_body_bytes;
  worker->input = (uint8_t *)fh_alloc(worker->input_capacity);
  worker->retained = (uint8_t *)fh_alloc(options->retained_bytes);
  worker->stream = (uint8_t *)fh_alloc(options->retained_bytes);
  worker->store = options->store_bytes > 0u ? (uint8_t *)fh_alloc((size_t)options->store_bytes) : 0;
  if (worker->stream == 0 || (options->store_bytes > 0u && worker->store == 0)) { worker->state = -1; goto done; }
  worker->provider.store = worker->store;
  worker->provider.store_bytes = worker->store == 0 ? 0u : options->store_bytes;
  worker->provider.stream = worker->stream;
  worker->provider.stream_capacity = options->retained_bytes;
  worker->provider.stream_used = 0u;
  if (worker->stack == 0 || worker->input == 0 || worker->retained == 0) { worker->state = -1; goto done; }
  worker->db = fh_db_open(options->db_path, reason, sizeof(reason));
  if (worker->db == 0) {
    fh_log("worker: database open failed: ");
    fh_log(reason);
    fh_log("\n");
    worker->state = -1;
    goto done;
  }
  worker->provider.db = worker->db;
  worker->provider.worker = (uint32_t)worker->index;
#if !defined(_WIN32)
  worker->listener = fh_listen(options->port, 1);
  worker->epoll_fd = epoll_create1(0);
  if (worker->listener == FH_INVALID_SOCKET || worker->epoll_fd < 0) { fh_log("worker: listen failed\n"); worker->state = -1; goto done; }
  {
    struct epoll_event event;
    event.events = EPOLLIN;
    event.data.ptr = 0;
    epoll_ctl(worker->epoll_fd, EPOLL_CTL_ADD, worker->listener, &event);
  }
#endif
  if (!fh_worker_init(worker)) { worker->state = -1; goto done; }
  worker->state = 1;
  fh_worker_loop(worker);
done:
  while (worker->connection_count > 0u) fh_drop_connection(worker, worker->connections[worker->connection_count - 1u]);
#if !defined(_WIN32)
  if (worker->listener != FH_INVALID_SOCKET) fh_close_socket(worker->listener);
  if (worker->epoll_fd > 0) close(worker->epoll_fd);
#endif
  if (worker->state == 1) worker->state = 2; /* stopped cleanly */
#if defined(_WIN32)
  return 0;
#else
  return 0;
#endif
}

/* ------------------------------------------------------------------ main */

#if defined(_WIN32)
static HANDLE g_stop_event; /* set once by Ctrl handler / control request; read by main */
static BOOL WINAPI fh_console_handler(DWORD type) { (void)type; SetEvent(g_stop_event); return TRUE; }
static void fh_request_shutdown(void) { SetEvent(g_stop_event); }
#else
static void fh_request_shutdown(void) { kill(getpid(), SIGTERM); }
#endif

static int fh_load_config(fh_options *options) {
  /* name=value lines; '#' comments. Values are never printed. */
  fh_writer w;
  uint32_t count_at, count = 0u;
  FILE *file = 0;
  char line[8192];
  uint32_t capacity = 1u << 20;
  w.data = (uint8_t *)malloc(capacity);
  w.capacity = capacity;
  w.used = 0u;
  w.failed = w.data == 0;
  count_at = fh_put_list_header(&w, 0u);
  if (options->config_path != 0) {
    file = fopen(options->config_path, "rb");
    if (file == 0) { fh_log("config file not readable\n"); free(w.data); return 0; }
    while (fgets(line, sizeof(line), file) != 0) {
      size_t length = strlen(line);
      char *equals;
      while (length > 0u && (line[length - 1] == '\n' || line[length - 1] == '\r')) line[--length] = 0;
      if (length == 0u || line[0] == '#') continue;
      equals = strchr(line, '=');
      if (equals == 0) continue;
      fh_put_bytes(&w, line, (uint32_t)(equals - line));
      fh_put_bytes(&w, equals + 1, (uint32_t)strlen(equals + 1));
      ++count;
    }
    memset(line, 0, sizeof(line));
    fclose(file);
  }
  fh_patch_count(&w, count_at, count);
  if (w.failed) { fh_log("config too large\n"); free(w.data); return 0; }
  options->config_value = w.data;
  options->config_extent = w.used;
  return 1;
}

static void fh_usage(void) {
  fh_log("usage: flow_http_host --port <p> --db <sqlite path> [--config <file>] [--workers N] [--pin]\n"
         "       [--arena-mb N] [--retained-mb N] [--max-body-kb N] [--cache-mb N] [--store-mb N] [--allow-control]\n"
         "  --cache-mb N  byte budget of the process-wide cache.get/cache.put cache (default 0 = disabled)\n"
         "  --store-mb N  total worker-store size, split evenly between workers (default 0 = no store)\n");
}

int main(int argc, char **argv) {
  static fh_options options;
  static fh_worker workers[FH_MAX_WORKERS];
  int i, failed = 0;
  uint64_t requests = 0u, failures = 0u;
  uint32_t max_prepared = 0u;
#if defined(_WIN32)
  WSADATA wsa;
  fh_socket shared_listener;
#else
  sigset_t signals;
  int received = 0;
#endif
  options.port = 13774;
  options.workers = 1;
  options.arena_bytes = 128u << 20; /* an @message page (81 messages) needs more than 32 MB of cursor */
  options.retained_bytes = 4u << 20;
  options.max_body_bytes = 1u << 20;
  options.cache_mb = 0u; /* off unless asked for: a no-cache run never caches silently */
  options.store_mb = 0u; /* no worker store unless asked for */
  for (i = 1; i < argc; ++i) {
    const char *a = argv[i], *v = i + 1 < argc ? argv[i + 1] : 0;
    if (strcmp(a, "--port") == 0 && v) { options.port = atoi(v); ++i; }
    else if (strcmp(a, "--workers") == 0 && v) { options.workers = atoi(v); ++i; }
    else if (strcmp(a, "--db") == 0 && v) { options.db_path = v; ++i; }
    else if (strcmp(a, "--config") == 0 && v) { options.config_path = v; ++i; }
    else if (strcmp(a, "--arena-mb") == 0 && v) { options.arena_bytes = (uint32_t)atoi(v) << 20; ++i; }
    else if (strcmp(a, "--retained-mb") == 0 && v) { options.retained_bytes = (uint32_t)atoi(v) << 20; ++i; }
    else if (strcmp(a, "--max-body-kb") == 0 && v) { options.max_body_bytes = (uint32_t)atoi(v) << 10; ++i; }
    else if (strcmp(a, "--cache-mb") == 0 && v) { options.cache_mb = (uint32_t)atoi(v); ++i; }
    else if (strcmp(a, "--store-mb") == 0 && v) { options.store_mb = (uint32_t)atoi(v); ++i; }
    else if (strcmp(a, "--pin") == 0) options.pin = 1;
    else if (strcmp(a, "--allow-control") == 0) options.allow_control = 1;
    else { fh_usage(); return 2; }
  }
  if (options.db_path == 0 || options.port <= 0 || options.port > 65535 || options.workers < 1 ||
      options.workers > FH_MAX_WORKERS || options.arena_bytes == 0u || options.arena_bytes > (1u << 30) ||
      options.retained_bytes == 0u || options.max_body_bytes == 0u || options.cache_mb > (1u << 16) || options.store_mb > (1u << 16)) {
    fh_usage();
    return 2;
  }
  /* The store total is split evenly between workers, in whole 16 KB pages. */
  options.store_bytes = (((uint64_t)options.store_mb << 20) / (uint64_t)options.workers) & ~(uint64_t)16383u;
  if (!fh_load_config(&options)) return 2;
  if (fh_cache_init((uint64_t)options.cache_mb << 20, (uint32_t)options.workers) != 0) { fh_log("cache allocation failed\n"); return 3; }
  {
    char reason[160];
    if (!fh_db_provision(options.db_path, reason, sizeof(reason))) {
      fh_log("database provisioning failed: ");
      fh_log(reason);
      fh_log("\n");
      return 3;
    }
  }

#if defined(_WIN32)
  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 3;
  g_stop_event = CreateEventW(NULL, TRUE, FALSE, NULL);
  SetConsoleCtrlHandler(fh_console_handler, TRUE);
  shared_listener = fh_listen(options.port, 0);
  if (shared_listener == FH_INVALID_SOCKET) { fh_log("listen failed\n"); return 3; }
#else
  sigemptyset(&signals);
  sigaddset(&signals, SIGINT);
  sigaddset(&signals, SIGTERM);
  pthread_sigmask(SIG_BLOCK, &signals, 0); /* workers inherit the mask */
  signal(SIGPIPE, SIG_IGN);
#endif

  for (i = 0; i < options.workers; ++i) {
    workers[i].index = i;
    workers[i].options = &options;
#if defined(_WIN32)
    workers[i].listener = shared_listener;
    workers[i].thread = CreateThread(NULL, 0, fh_worker_main, &workers[i], 0, NULL);
    if (workers[i].thread == NULL) { workers[i].state = -1; }
#else
    workers[i].listener = FH_INVALID_SOCKET;
    workers[i].epoll_fd = -1;
    if (pthread_create(&workers[i].thread, 0, fh_worker_main, &workers[i]) != 0) workers[i].state = -1;
    else workers[i].started = 1;
#endif
  }
  /* Wait for every worker to finish init (state is written by its worker). */
  for (;;) {
    int pending = 0;
    for (i = 0; i < options.workers; ++i) {
      if (workers[i].state == 0) pending = 1;
      else if (workers[i].state < 0) failed = 1;
    }
    if (!pending) break;
#if defined(_WIN32)
    Sleep(5);
#else
    { struct timespec pause = {0, 5000000}; nanosleep(&pause, 0); }
#endif
  }
  if (failed) {
    fh_log("startup failed\n");
    for (i = 0; i < options.workers; ++i) workers[i].stop = 1;
  } else {
    printf("READY %d\n", options.port);
    fflush(stdout);
#if defined(_WIN32)
    WaitForSingleObject(g_stop_event, INFINITE);
#else
    sigwait(&signals, &received);
#endif
    for (i = 0; i < options.workers; ++i) workers[i].stop = 1;
  }
  for (i = 0; i < options.workers; ++i) {
#if defined(_WIN32)
    if (workers[i].thread != NULL) { WaitForSingleObject(workers[i].thread, INFINITE); CloseHandle(workers[i].thread); }
#else
    if (workers[i].started) pthread_join(workers[i].thread, 0);
#endif
    requests += workers[i].requests;
    failures += workers[i].failures;
    if (fh_db_prepared_count(workers[i].db) > max_prepared) max_prepared = fh_db_prepared_count(workers[i].db);
    fh_db_close(workers[i].db);
    if (workers[i].requests > 0u) {
      char line[260];
      uint64_t pages = 0u, page, at;
      /* Store pages holding any non-zero byte (the store starts zero-filled). */
      for (page = 0u; workers[i].store != 0 && page < options.store_bytes / 16384u; ++page)
        for (at = 0u; at < 16384u; at += 8u) {
          uint64_t word;
          memcpy(&word, workers[i].store + page * 16384u + at, 8);
          if (word != 0u) { ++pages; break; }
        }
      snprintf(line, sizeof(line), "worker %d: requests=%llu peak-arena-bytes=%u peak-response-bytes=%u peak-input-bytes=%u context-bytes=%u store-bytes=%llu store-pages-used=%llu\n",
               i, (unsigned long long)workers[i].requests, workers[i].peak_arena, workers[i].peak_response,
               workers[i].peak_input, workers[i].context_bytes, (unsigned long long)options.store_bytes, (unsigned long long)pages);
      fh_log(line);
    }
    if (workers[i].stack && workers[i].context_bytes) memset(workers[i].stack, 0, workers[i].context_bytes); /* keys */
    fh_free(workers[i].stack, options.arena_bytes);
    fh_free(workers[i].init_bitmap, options.arena_bytes / 8u);
    fh_free(workers[i].poison_bitmap, options.arena_bytes / 8u);
    fh_free(workers[i].input, workers[i].input_capacity);
    fh_free(workers[i].retained, options.retained_bytes);
    fh_free(workers[i].stream, options.retained_bytes);
    if (workers[i].store != 0) fh_free(workers[i].store, (size_t)options.store_bytes);
  }
#if defined(_WIN32)
  fh_close_socket(shared_listener);
  WSACleanup();
#endif
  if (options.config_value) { memset(options.config_value, 0, options.config_extent); free(options.config_value); }
  {
    fh_cache_stats cache;
    fh_cache_stats_read(&cache);
    fh_cache_shutdown();
    fprintf(stderr, "STOPPED requests=%llu failures=%llu max-prepared-statements=%u cache-mb=%u cache-hits=%llu cache-misses=%llu cache-puts=%llu cache-dropped=%llu cache-rollovers=%llu\n",
            (unsigned long long)requests, (unsigned long long)failures, max_prepared, options.cache_mb,
            (unsigned long long)cache.hits, (unsigned long long)cache.misses, (unsigned long long)cache.puts,
            (unsigned long long)cache.dropped, (unsigned long long)cache.rollovers);
  }
  return failed ? 1 : 0;
}

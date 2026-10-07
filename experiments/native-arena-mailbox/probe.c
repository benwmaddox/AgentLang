#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>

#include <inttypes.h>
#include <limits.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHUNK_BYTES (16u * 1024u)
#define MAX_CHUNKS 512
#define MAX_ARENAS (8 * 5)
#define MAX_MAILBOXES 8
#define MAX_REQUESTS 256
#define MAX_QUEUE_DEPTH 8
#define MAX_QUEUE_BYTES 4096u
#define MAX_CACHED_CHUNKS 64
#define MAX_GRAPH_BODY_BYTES (CHUNK_BYTES / 2u)
#define MAX_OUTPUT_BYTES_PER_MAILBOX (128u * 1024u)
#define WORKING_SCRATCH_BYTES (512u * 1024u)

#define ARENA_SCRATCH 0
#define ARENA_CONTINUATION 1
#define ARENA_PROVIDER 2
#define ARENA_OUTPUT 3
#define ARENA_INBOX 4

#define TYPE_ROOT 0x524f4f54u
#define TYPE_PAYLOAD 0x5041594cu
#define CHUNK_EMPTY (-2)
#define CHUNK_CACHED (-1)

typedef struct Ref {
    int32_t arena_id;
    int32_t chunk_id;
    uint32_t generation;
    uint32_t size;
    uint32_t offset;
    uint32_t valid;
} Ref;

typedef struct Chunk {
    uint8_t *base;
    size_t cursor;
    int32_t owner_arena;
    int32_t next_arena;
    int32_t next_free;
} Chunk;

typedef struct Arena {
    uint32_t generation;
    uint32_t active;
    int32_t first_chunk;
    int32_t last_chunk;
    uint32_t chunk_count;
    uint64_t used_bytes;
} Arena;

typedef struct Pool {
    Chunk chunks[MAX_CHUNKS];
    int32_t free_head;
    uint32_t cached_count;
    uint64_t reserved_bytes;
    uint64_t allocator_calls;
    uint64_t free_calls;
    uint64_t peak_reserved_bytes;
    uint64_t peak_cached_bytes;
    size_t page_bytes;
} Pool;

typedef struct RootValue {
    uint32_t nominal_type;
    uint32_t resume_pc;
    Ref left;
    Ref right;
    uint64_t input_id;
} RootValue;

typedef struct PayloadValue {
    uint32_t nominal_type;
    uint32_t body_bytes;
    uint64_t value;
    uint64_t work_checksum;
    Ref body;
} PayloadValue;

typedef struct InputPayload {
    uint32_t id;
    uint32_t seed;
    uint64_t value;
} InputPayload;

typedef struct OutputBuffer {
    uint64_t value;
    uint64_t request_id;
    uint32_t payload_bytes;
    uint32_t reserved;
    uint8_t payload[1];
} OutputBuffer;

typedef struct ResumeFrame {
    uint64_t token;
    uint32_t resume_pc;
    uint32_t mailbox_id;
} ResumeFrame;

typedef enum ResultStatus {
    STATUS_NONE = 0,
    STATUS_QUEUED,
    STATUS_RUNNING,
    STATUS_COMPLETED,
    STATUS_CANCELLED,
    STATUS_REJECTED_CAPACITY,
    STATUS_REJECTED_QUEUE,
    STATUS_REJECTED_OUTPUT_CAPACITY,
    STATUS_REJECTED_PENDING,
    STATUS_FAILED
} ResultStatus;

typedef struct Result {
    ResultStatus status;
    uint32_t mailbox_id;
    uint64_t value;
    Ref output_ref;
    uint32_t output_acknowledged;
    uint32_t reserved;
} Result;

typedef struct Message {
    uint32_t request_id;
    uint32_t payload_bytes;
    Ref payload;
} Message;

typedef struct PendingOperation {
    uint32_t active;
    uint32_t ready;
    uint32_t cancel_requested;
    uint32_t provider_released;
    uint32_t request_id;
    uint32_t root_arena_id;
    uint64_t token;
    uint64_t due_tick;
    uint64_t expected_value;
    uint64_t expected_work;
    uint64_t provider_tag;
    uint32_t provider_bytes;
    uint32_t reserved;
    Ref root;
    Ref provider_ref;
} PendingOperation;

typedef struct Mailbox {
    uint64_t static_marker;
    uint64_t static_processed;
    uint64_t static_checksum;
    uint64_t resume_count;
    uint32_t queue_head;
    uint32_t queue_count;
    uint32_t queue_bytes;
    uint32_t output_live_bytes;
    Message queue[MAX_QUEUE_DEPTH];
    PendingOperation pending;
} Mailbox;

typedef struct Runtime {
    Pool pool;
    Arena arenas[MAX_ARENAS];
    Mailbox mailboxes[MAX_MAILBOXES];
    Result results[MAX_REQUESTS];

    uint64_t budget_bytes;
    uint64_t fixed_metadata_bytes;
    uint64_t arena_used_bytes;
    uint64_t arena_peak_used_bytes;
    uint64_t continuation_copy_bytes;
    uint64_t tick;
    uint64_t next_token;
    uint64_t provider_live_before_cancel;
    uint64_t provider_live_after_cancel;
    uint64_t output_live_before_cancel;
    uint64_t output_live_after_cancel;
    uint32_t request_count;
    uint32_t seed;
    uint32_t output_bytes;
    uint32_t provider_bytes;
    uint32_t body_bytes;
    uint32_t working_bytes;
    uint32_t delay_ticks;
    uint32_t async_mode;
    uint32_t hold_output;
    uint32_t cancellation_scenario;
    uint32_t output_limit_bytes;
    uint32_t has_cancel_snapshot;
    uint32_t fail;
    const char *scenario_name;
    const char *mode_name;
    char failure[192];
} Runtime;

typedef struct JobLimit {
    HANDLE handle;
    uint64_t limit_bytes;
    uint32_t enforced;
} JobLimit;

typedef struct CliOptions {
    uint32_t self_test;
    uint32_t has_mode;
    uint32_t has_scenario;
    uint32_t has_seed;
    uint32_t has_requests;
    uint32_t has_budget;
    uint32_t has_process_limit;
    uint32_t seed;
    uint32_t requests;
    uint64_t budget_bytes;
    uint64_t process_limit_bytes;
    const char *mode;
    const char *scenario;
} CliOptions;

static Runtime g_runtime;

static uint64_t output_value_oracle(uint32_t seed, uint32_t request_id) {
    uint64_t mixed = (uint64_t)seed * 1000003ULL;
    mixed += (uint64_t)(request_id + 1u) * 97ULL;
    mixed += 0x41C64E6DULL;
    return mixed & 0xffffffffULL;
}

static uint64_t static_state_checksum(uint64_t marker, uint64_t count) {
    uint64_t x = marker ^ (count * 0x9e3779b97f4a7c15ULL);
    x ^= x >> 30;
    x *= 0xbf58476d1ce4e5b9ULL;
    x ^= x >> 27;
    x *= 0x94d049bb133111ebULL;
    return x ^ (x >> 31);
}

static uint64_t cpu_control_work(uint64_t input) {
    uint64_t x = input ^ 0xd6e8feb86659fd93ULL;
    uint32_t i;
    for (i = 0; i < 50000u; ++i) {
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        x *= 0x2545f4914f6cdd1dULL;
    }
    return x;
}

static void copy_bounded_string(char *destination, size_t destination_size,
                                const char *source) {
    size_t length;
    if (destination == NULL || destination_size == 0u) {
        return;
    }
    if (source == NULL) {
        destination[0] = '\0';
        return;
    }
    length = strlen(source);
    if (length >= destination_size) {
        length = destination_size - 1u;
    }
    memcpy(destination, source, length);
    destination[length] = '\0';
}

static void runtime_fail(Runtime *rt, const char *message) {
    if (rt->fail == 0) {
        rt->fail = 1;
        copy_bounded_string(rt->failure, sizeof(rt->failure), message);
    }
}

static uint64_t current_cached_bytes(const Runtime *rt) {
    return (uint64_t)rt->pool.cached_count * CHUNK_BYTES;
}

static void update_peaks(Runtime *rt) {
    uint64_t cached = current_cached_bytes(rt);
    if (rt->arena_used_bytes > rt->arena_peak_used_bytes) {
        rt->arena_peak_used_bytes = rt->arena_used_bytes;
    }
    if (rt->pool.reserved_bytes > rt->pool.peak_reserved_bytes) {
        rt->pool.peak_reserved_bytes = rt->pool.reserved_bytes;
    }
    if (cached > rt->pool.peak_cached_bytes) {
        rt->pool.peak_cached_bytes = cached;
    }
}

static int pool_release_chunk(Runtime *rt, int32_t chunk_id) {
    Chunk *chunk;
    if (chunk_id < 0 || chunk_id >= MAX_CHUNKS) {
        runtime_fail(rt, "invalid chunk index during release");
        return 0;
    }
    chunk = &rt->pool.chunks[chunk_id];
    if (chunk->base == NULL || chunk->owner_arena < 0) {
        runtime_fail(rt, "chunk ownership mismatch during release");
        return 0;
    }
    chunk->cursor = 0;
    chunk->owner_arena = CHUNK_CACHED;
    chunk->next_arena = -1;
    if (rt->pool.cached_count < MAX_CACHED_CHUNKS) {
        chunk->next_free = rt->pool.free_head;
        rt->pool.free_head = chunk_id;
        rt->pool.cached_count += 1u;
        update_peaks(rt);
        return 1;
    }
    if (!VirtualFree(chunk->base, 0, MEM_RELEASE)) {
        runtime_fail(rt, "VirtualFree failed while trimming arena cache");
        return 0;
    }
    chunk->base = NULL;
    chunk->owner_arena = CHUNK_EMPTY;
    chunk->next_free = -1;
    if (rt->pool.reserved_bytes < CHUNK_BYTES) {
        runtime_fail(rt, "reserved-byte underflow while trimming arena cache");
        return 0;
    }
    rt->pool.reserved_bytes -= CHUNK_BYTES;
    rt->pool.free_calls += 1u;
    update_peaks(rt);
    return 1;
}

static int pool_get_chunk(Runtime *rt, int32_t arena_id, int32_t *out_chunk_id) {
    int32_t chunk_id = -1;
    uint32_t i;
    Chunk *chunk;
    if (rt->fail != 0 || out_chunk_id == NULL) {
        return 0;
    }
    if (rt->pool.free_head >= 0) {
        chunk_id = rt->pool.free_head;
        chunk = &rt->pool.chunks[chunk_id];
        rt->pool.free_head = chunk->next_free;
        if (rt->pool.cached_count == 0u || chunk->owner_arena != CHUNK_CACHED) {
            runtime_fail(rt, "arena free-list metadata is inconsistent");
            return 0;
        }
        rt->pool.cached_count -= 1u;
    } else {
        for (i = 0; i < MAX_CHUNKS; ++i) {
            if (rt->pool.chunks[i].base == NULL) {
                chunk_id = (int32_t)i;
                break;
            }
        }
        if (chunk_id < 0) {
            return 0;
        }
        if (rt->fixed_metadata_bytes > rt->budget_bytes ||
            rt->pool.reserved_bytes > rt->budget_bytes - rt->fixed_metadata_bytes ||
            CHUNK_BYTES > rt->budget_bytes - rt->fixed_metadata_bytes - rt->pool.reserved_bytes) {
            return 0;
        }
        chunk = &rt->pool.chunks[chunk_id];
        chunk->base = (uint8_t *)VirtualAlloc(NULL, CHUNK_BYTES, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (chunk->base == NULL) {
            return 0;
        }
        rt->pool.reserved_bytes += CHUNK_BYTES;
        rt->pool.allocator_calls += 1u;
    }
    chunk = &rt->pool.chunks[chunk_id];
    chunk->cursor = 0;
    chunk->owner_arena = arena_id;
    chunk->next_arena = -1;
    chunk->next_free = -1;
    memset(chunk->base, 0, CHUNK_BYTES);
    update_peaks(rt);
    *out_chunk_id = chunk_id;
    return 1;
}

static void arena_make_inactive(Runtime *rt, int32_t arena_id) {
    Arena *arena;
    int32_t chunk_id;
    if (arena_id < 0 || arena_id >= MAX_ARENAS) {
        runtime_fail(rt, "invalid arena index during release");
        return;
    }
    arena = &rt->arenas[arena_id];
    chunk_id = arena->first_chunk;
    while (chunk_id >= 0) {
        int32_t next = rt->pool.chunks[chunk_id].next_arena;
        if (!pool_release_chunk(rt, chunk_id)) {
            return;
        }
        chunk_id = next;
    }
    if (rt->arena_used_bytes < arena->used_bytes) {
        runtime_fail(rt, "arena used-byte underflow");
        return;
    }
    rt->arena_used_bytes -= arena->used_bytes;
    arena->used_bytes = 0;
    arena->first_chunk = -1;
    arena->last_chunk = -1;
    arena->chunk_count = 0;
    arena->generation += 1u;
    if (arena->generation == 0u) {
        arena->generation = 1u;
    }
}

static int arena_acquire(Runtime *rt, int32_t arena_id) {
    Arena *arena;
    if (arena_id < 0 || arena_id >= MAX_ARENAS || rt->fail != 0) {
        return 0;
    }
    arena = &rt->arenas[arena_id];
    if (arena->active != 0u) {
        runtime_fail(rt, "attempted to acquire an active arena");
        return 0;
    }
    arena->generation += 1u;
    if (arena->generation == 0u) {
        arena->generation = 1u;
    }
    arena->active = 1u;
    arena->first_chunk = -1;
    arena->last_chunk = -1;
    arena->chunk_count = 0;
    arena->used_bytes = 0;
    return 1;
}

static void arena_release(Runtime *rt, int32_t arena_id) {
    Arena *arena;
    if (arena_id < 0 || arena_id >= MAX_ARENAS) {
        runtime_fail(rt, "invalid arena index");
        return;
    }
    arena = &rt->arenas[arena_id];
    if (arena->active == 0u) {
        return;
    }
    arena_make_inactive(rt, arena_id);
    arena->active = 0u;
}

static int arena_reset(Runtime *rt, int32_t arena_id) {
    Arena *arena;
    if (arena_id < 0 || arena_id >= MAX_ARENAS) {
        runtime_fail(rt, "invalid arena index");
        return 0;
    }
    arena = &rt->arenas[arena_id];
    if (arena->active == 0u) {
        return arena_acquire(rt, arena_id);
    }
    arena_make_inactive(rt, arena_id);
    arena->active = 1u;
    return rt->fail == 0;
}

static Ref arena_allocate(Runtime *rt, int32_t arena_id, size_t bytes, size_t alignment) {
    Ref invalid = { -1, -1, 0, 0, 0, 0 };
    Arena *arena;
    Chunk *chunk;
    int32_t chunk_id;
    size_t aligned;
    if (rt->fail != 0 || arena_id < 0 || arena_id >= MAX_ARENAS || bytes == 0u ||
        bytes > CHUNK_BYTES || alignment == 0u || (alignment & (alignment - 1u)) != 0u) {
        return invalid;
    }
    arena = &rt->arenas[arena_id];
    if (arena->active == 0u) {
        runtime_fail(rt, "allocation attempted in inactive arena");
        return invalid;
    }
    chunk_id = arena->last_chunk;
    if (chunk_id >= 0) {
        chunk = &rt->pool.chunks[chunk_id];
        aligned = (chunk->cursor + (alignment - 1u)) & ~(alignment - 1u);
        if (aligned > CHUNK_BYTES || bytes > CHUNK_BYTES - aligned) {
            chunk_id = -1;
        }
    }
    if (chunk_id < 0) {
        if (!pool_get_chunk(rt, arena_id, &chunk_id)) {
            return invalid;
        }
        chunk = &rt->pool.chunks[chunk_id];
        aligned = 0u;
        if (arena->last_chunk < 0) {
            arena->first_chunk = chunk_id;
        } else {
            rt->pool.chunks[arena->last_chunk].next_arena = chunk_id;
        }
        arena->last_chunk = chunk_id;
        arena->chunk_count += 1u;
    }
    chunk = &rt->pool.chunks[chunk_id];
    chunk->cursor = aligned + bytes;
    arena->used_bytes += bytes;
    rt->arena_used_bytes += bytes;
    update_peaks(rt);
    invalid.arena_id = arena_id;
    invalid.chunk_id = chunk_id;
    invalid.generation = arena->generation;
    invalid.size = (uint32_t)bytes;
    invalid.offset = (uint32_t)aligned;
    invalid.valid = 1u;
    return invalid;
}

static void *arena_resolve(Runtime *rt, Ref ref, size_t required_bytes) {
    Arena *arena;
    Chunk *chunk;
    if (ref.valid == 0u || ref.arena_id < 0 || ref.arena_id >= MAX_ARENAS ||
        ref.chunk_id < 0 || ref.chunk_id >= MAX_CHUNKS || required_bytes > ref.size ||
        ref.offset > CHUNK_BYTES || required_bytes > CHUNK_BYTES - ref.offset) {
        return NULL;
    }
    arena = &rt->arenas[ref.arena_id];
    chunk = &rt->pool.chunks[ref.chunk_id];
    if (arena->active == 0u || arena->generation != ref.generation ||
        chunk->base == NULL || chunk->owner_arena != ref.arena_id ||
        ref.offset > chunk->cursor || required_bytes > chunk->cursor - ref.offset) {
        return NULL;
    }
    return chunk->base + ref.offset;
}

static int ref_equal(Ref a, Ref b) {
    return a.valid == b.valid && a.valid != 0u && a.arena_id == b.arena_id &&
           a.chunk_id == b.chunk_id && a.generation == b.generation &&
           a.offset == b.offset && a.size == b.size;
}

static uint32_t count_active_owners(const Runtime *rt);
static uint64_t live_bytes_for_kind(const Runtime *rt, uint32_t kind);
static int has_pending_or_queued_work(const Runtime *rt);

static int pool_drain(Runtime *rt) {
    uint32_t i;
    if (count_active_owners(rt) != 0u || rt->arena_used_bytes != 0u ||
        has_pending_or_queued_work(rt) ||
        live_bytes_for_kind(rt, ARENA_PROVIDER) != 0u ||
        live_bytes_for_kind(rt, ARENA_OUTPUT) != 0u) {
        runtime_fail(rt, "pool drain rejected live arena owners or pending work");
        return 0;
    }
    if (rt->fail != 0) {
        return 0;
    }
    for (i = 0; i < MAX_CHUNKS; ++i) {
        Chunk *chunk = &rt->pool.chunks[i];
        if (chunk->base != NULL) {
            if (chunk->owner_arena != CHUNK_CACHED) {
                runtime_fail(rt, "pool drain found a chunk with a live owner");
                return 0;
            }
            if (!VirtualFree(chunk->base, 0, MEM_RELEASE)) {
                runtime_fail(rt, "VirtualFree failed while draining arena pool");
                return 0;
            }
            chunk->base = NULL;
            chunk->owner_arena = CHUNK_EMPTY;
            chunk->next_free = -1;
            rt->pool.free_calls += 1u;
            rt->pool.reserved_bytes -= CHUNK_BYTES;
        }
    }
    rt->pool.free_head = -1;
    rt->pool.cached_count = 0u;
    update_peaks(rt);
    return rt->fail == 0;
}

static uint32_t count_active_owners(const Runtime *rt) {
    uint32_t i;
    uint32_t count = 0;
    for (i = 0; i < MAX_ARENAS; ++i) {
        count += rt->arenas[i].active != 0u ? 1u : 0u;
    }
    return count;
}

static uint64_t live_bytes_for_kind(const Runtime *rt, uint32_t kind) {
    uint32_t i;
    uint64_t total = 0;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        int32_t arena_id = (int32_t)(i * 5u + kind);
        if (arena_id >= 0 && arena_id < MAX_ARENAS && rt->arenas[arena_id].active != 0u) {
            total += rt->arenas[arena_id].used_bytes;
        }
    }
    return total;
}

static int has_pending_or_queued_work(const Runtime *rt) {
    uint32_t i;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        if (rt->mailboxes[i].pending.active != 0u ||
            rt->mailboxes[i].queue_count != 0u ||
            rt->mailboxes[i].queue_bytes != 0u ||
            rt->mailboxes[i].output_live_bytes != 0u) {
            return 1;
        }
    }
    return 0;
}

static int retire_empty_owners(Runtime *rt) {
    uint32_t i;
    if (has_pending_or_queued_work(rt) ||
        live_bytes_for_kind(rt, ARENA_PROVIDER) != 0u ||
        live_bytes_for_kind(rt, ARENA_OUTPUT) != 0u ||
        rt->arena_used_bytes != 0u) {
        return 0;
    }
    for (i = 0; i < MAX_ARENAS; ++i) {
        if (rt->arenas[i].active != 0u && rt->arenas[i].used_bytes != 0u) {
            return 0;
        }
    }
    for (i = 0; i < MAX_ARENAS; ++i) {
        if (rt->arenas[i].active != 0u) {
            arena_release(rt, (int32_t)i);
        }
    }
    return rt->fail == 0 && count_active_owners(rt) == 0u;
}

static void fill_body(uint8_t *body, uint32_t bytes, uint64_t value) {
    uint32_t i;
    for (i = 0; i < bytes; ++i) {
        body[i] = (uint8_t)((value + (uint64_t)i * 31ULL + (uint64_t)(i >> 3)) & 0xffu);
    }
}

static int check_body(const uint8_t *body, uint32_t bytes, uint64_t value) {
    uint32_t i;
    for (i = 0; i < bytes; ++i) {
        uint8_t expected = (uint8_t)((value + (uint64_t)i * 31ULL + (uint64_t)(i >> 3)) & 0xffu);
        if (body[i] != expected) {
            return 0;
        }
    }
    return 1;
}

static int build_disposable_scratch(Runtime *rt, int32_t arena_id, uint64_t input,
                                    uint32_t bytes, uint64_t *out_checksum) {
    uint64_t hash = 1469598103934665603ULL ^ input;
    uint32_t offset = 0u;
    if (out_checksum == NULL) {
        return 0;
    }
    if (bytes == 0u) {
        *out_checksum = 0u;
        return 1;
    }
    while (offset < bytes) {
        uint32_t amount = bytes - offset;
        Ref ref;
        uint8_t *memory;
        volatile uint8_t *scratch_bytes;
        uint32_t i;
        uint64_t block_value = input + offset;
        if (amount > CHUNK_BYTES) {
            amount = CHUNK_BYTES;
        }
        ref = arena_allocate(rt, arena_id, amount, 8u);
        memory = (uint8_t *)arena_resolve(rt, ref, amount);
        if (memory == NULL) {
            return 0;
        }
        scratch_bytes = (volatile uint8_t *)memory;
        for (i = 0; i < amount; ++i) {
            uint8_t value = (uint8_t)((block_value + (uint64_t)i * 31ULL +
                                       (uint64_t)(i >> 3)) & 0xffu);
            scratch_bytes[i] = value;
            hash ^= scratch_bytes[i];
            hash *= 1099511628211ULL;
        }
        offset += amount;
    }
    *out_checksum = hash ^ bytes;
    return 1;
}

static int create_graph(Runtime *rt, int32_t arena_id, uint32_t request_id,
                        uint64_t value, uint32_t body_bytes, uint64_t work_checksum,
                        Ref *out_root) {
    Ref body_ref;
    Ref payload_ref;
    Ref root_ref;
    uint8_t *body;
    PayloadValue *payload;
    RootValue *root;
    if (out_root == NULL || body_bytes == 0u || body_bytes > MAX_GRAPH_BODY_BYTES) {
        return 0;
    }
    body_ref = arena_allocate(rt, arena_id, body_bytes, 8u);
    body = (uint8_t *)arena_resolve(rt, body_ref, body_bytes);
    if (body == NULL) {
        return 0;
    }
    fill_body(body, body_bytes, value);
    payload_ref = arena_allocate(rt, arena_id, sizeof(PayloadValue), 8u);
    payload = (PayloadValue *)arena_resolve(rt, payload_ref, sizeof(PayloadValue));
    if (payload == NULL) {
        return 0;
    }
    payload->nominal_type = TYPE_PAYLOAD;
    payload->body_bytes = body_bytes;
    payload->value = value;
    payload->work_checksum = work_checksum;
    payload->body = body_ref;

    root_ref = arena_allocate(rt, arena_id, sizeof(RootValue), 8u);
    root = (RootValue *)arena_resolve(rt, root_ref, sizeof(RootValue));
    if (root == NULL) {
        return 0;
    }
    root->nominal_type = TYPE_ROOT;
    root->resume_pc = 7u;
    root->left = payload_ref;
    root->right = payload_ref;
    root->input_id = request_id;
    *out_root = root_ref;
    return 1;
}

static int validate_graph(Runtime *rt, Ref root_ref, uint64_t expected_value,
                         uint32_t expected_body_bytes, uint64_t expected_work,
                         uint64_t *out_value) {
    RootValue *root = (RootValue *)arena_resolve(rt, root_ref, sizeof(RootValue));
    PayloadValue *payload;
    uint8_t *body;
    if (root == NULL || root->nominal_type != TYPE_ROOT || root->resume_pc != 7u ||
        !ref_equal(root->left, root->right)) {
        return 0;
    }
    payload = (PayloadValue *)arena_resolve(rt, root->left, sizeof(PayloadValue));
    if (payload == NULL || payload->nominal_type != TYPE_PAYLOAD ||
        payload->body_bytes != expected_body_bytes || payload->value != expected_value ||
        payload->work_checksum != expected_work) {
        return 0;
    }
    body = (uint8_t *)arena_resolve(rt, payload->body, payload->body_bytes);
    if (body == NULL || payload->body.size != payload->body_bytes ||
        !check_body(body, payload->body_bytes, payload->value)) {
        return 0;
    }
    if (out_value != NULL) {
        *out_value = payload->value;
    }
    return 1;
}

static int promote_graph(Runtime *rt, Ref source_root, int32_t destination_arena,
                         uint32_t body_bytes, uint64_t expected_value,
                         uint64_t expected_work, uint64_t copy_limit,
                         Ref *out_root, uint64_t *out_copied) {
    RootValue *src_root;
    PayloadValue *src_payload;
    uint8_t *src_body;
    Ref body_ref;
    Ref payload_ref;
    Ref root_ref;
    uint8_t *dst_body;
    PayloadValue *dst_payload;
    RootValue *dst_root;
    uint64_t copied = 0;
    if (out_root == NULL || out_copied == NULL) {
        return 0;
    }
    *out_root = (Ref){ -1, -1, 0, 0, 0, 0 };
    *out_copied = 0;
    src_root = (RootValue *)arena_resolve(rt, source_root, sizeof(RootValue));
    if (src_root == NULL || src_root->nominal_type != TYPE_ROOT ||
        !ref_equal(src_root->left, src_root->right)) {
        return 0;
    }
    src_payload = (PayloadValue *)arena_resolve(rt, src_root->left, sizeof(PayloadValue));
    if (src_payload == NULL || src_payload->nominal_type != TYPE_PAYLOAD ||
        src_payload->value != expected_value || src_payload->body_bytes != body_bytes ||
        src_payload->work_checksum != expected_work) {
        return 0;
    }
    src_body = (uint8_t *)arena_resolve(rt, src_payload->body, body_bytes);
    if (src_body == NULL || !check_body(src_body, body_bytes, expected_value)) {
        return 0;
    }

    if ((uint64_t)body_bytes > copy_limit - copied) {
        goto rollback;
    }
    body_ref = arena_allocate(rt, destination_arena, body_bytes, 8u);
    dst_body = (uint8_t *)arena_resolve(rt, body_ref, body_bytes);
    if (dst_body == NULL) {
        goto rollback;
    }
    memcpy(dst_body, src_body, body_bytes);
    copied += body_bytes;

    if (sizeof(PayloadValue) > copy_limit - copied) {
        goto rollback;
    }
    payload_ref = arena_allocate(rt, destination_arena, sizeof(PayloadValue), 8u);
    dst_payload = (PayloadValue *)arena_resolve(rt, payload_ref, sizeof(PayloadValue));
    if (dst_payload == NULL) {
        goto rollback;
    }
    memcpy(dst_payload, src_payload, sizeof(PayloadValue));
    dst_payload->body = body_ref;
    copied += sizeof(PayloadValue);

    if (sizeof(RootValue) > copy_limit - copied) {
        goto rollback;
    }
    root_ref = arena_allocate(rt, destination_arena, sizeof(RootValue), 8u);
    dst_root = (RootValue *)arena_resolve(rt, root_ref, sizeof(RootValue));
    if (dst_root == NULL) {
        goto rollback;
    }
    memcpy(dst_root, src_root, sizeof(RootValue));
    dst_root->left = payload_ref;
    dst_root->right = payload_ref;
    copied += sizeof(RootValue);

    *out_root = root_ref;
    *out_copied = copied;
    return 1;

rollback:
    (void)arena_reset(rt, destination_arena);
    return 0;
}

static int output_write(Runtime *rt, int32_t arena_id, uint32_t bytes,
                        uint32_t request_id, uint64_t value, Ref *out_ref) {
    Ref ref;
    OutputBuffer *buffer;
    if (bytes < sizeof(OutputBuffer) || out_ref == NULL) {
        return 0;
    }
    ref = arena_allocate(rt, arena_id, bytes, 8u);
    buffer = (OutputBuffer *)arena_resolve(rt, ref, sizeof(OutputBuffer));
    if (buffer == NULL) {
        return 0;
    }
    buffer->value = value;
    buffer->request_id = request_id;
    buffer->payload_bytes = bytes - (uint32_t)offsetof(OutputBuffer, payload);
    buffer->reserved = 0u;
    memset(buffer->payload, (int)(value & 0xffu), buffer->payload_bytes);
    ref.size = bytes;
    *out_ref = ref;
    return 1;
}

static int output_read(Runtime *rt, Ref ref, uint32_t request_id, uint64_t expected_value,
                       uint64_t *out_value) {
    OutputBuffer *buffer;
    uint32_t i;
    if (ref.size < sizeof(OutputBuffer) || ref.size < offsetof(OutputBuffer, payload)) {
        return 0;
    }
    buffer = (OutputBuffer *)arena_resolve(rt, ref, ref.size);
    if (buffer == NULL || buffer->request_id != request_id || buffer->value != expected_value ||
        buffer->payload_bytes != ref.size - (uint32_t)offsetof(OutputBuffer, payload)) {
        return 0;
    }
    for (i = 0; i < buffer->payload_bytes; ++i) {
        if (buffer->payload[i] != (uint8_t)(expected_value & 0xffu)) {
            return 0;
        }
    }
    if (out_value != NULL) {
        *out_value = buffer->value;
    }
    return 1;
}

static const char *status_name(ResultStatus status) {
    switch (status) {
        case STATUS_COMPLETED: return "completed";
        case STATUS_CANCELLED: return "cancelled";
        case STATUS_REJECTED_CAPACITY: return "rejected_capacity";
        case STATUS_REJECTED_QUEUE: return "rejected_queue";
        case STATUS_REJECTED_OUTPUT_CAPACITY: return "rejected_output_capacity";
        case STATUS_REJECTED_PENDING: return "rejected_pending";
        case STATUS_FAILED: return "failed";
        case STATUS_QUEUED: return "queued";
        case STATUS_RUNNING: return "running";
        default: return "unprocessed";
    }
}

static int is_rejected(ResultStatus status) {
    return status == STATUS_REJECTED_CAPACITY || status == STATUS_REJECTED_QUEUE ||
           status == STATUS_REJECTED_OUTPUT_CAPACITY || status == STATUS_REJECTED_PENDING;
}

static uint64_t provider_live_bytes(const Runtime *rt) {
    return live_bytes_for_kind(rt, ARENA_PROVIDER);
}

static uint64_t output_live_bytes(const Runtime *rt) {
    return live_bytes_for_kind(rt, ARENA_OUTPUT);
}

static int pending_begin(Runtime *rt, uint32_t mailbox_id, uint32_t request_id,
                         int32_t root_arena_id, Ref root, uint64_t expected_value,
                         uint64_t expected_work, uint64_t due_tick, uint64_t token) {
    Mailbox *mailbox;
    if (mailbox_id >= MAX_MAILBOXES) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->pending.active != 0u) {
        rt->results[request_id].status = STATUS_REJECTED_PENDING;
        return 0;
    }
    memset(&mailbox->pending, 0, sizeof(mailbox->pending));
    mailbox->pending.active = 1u;
    mailbox->pending.request_id = request_id;
    mailbox->pending.root_arena_id = (uint32_t)root_arena_id;
    mailbox->pending.root = root;
    mailbox->pending.expected_value = expected_value;
    mailbox->pending.expected_work = expected_work;
    mailbox->pending.due_tick = due_tick;
    mailbox->pending.token = token;
    return 1;
}

static int pending_attach_provider(Runtime *rt, uint32_t mailbox_id, uint64_t token,
                                   Ref provider_ref, uint32_t provider_bytes, uint64_t tag) {
    Mailbox *mailbox;
    if (mailbox_id >= MAX_MAILBOXES) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->pending.active == 0u || mailbox->pending.token != token ||
        provider_ref.valid == 0u || provider_bytes == 0u || provider_ref.size != provider_bytes) {
        return 0;
    }
    mailbox->pending.provider_ref = provider_ref;
    mailbox->pending.provider_bytes = provider_bytes;
    mailbox->pending.provider_tag = tag;
    return 1;
}

static int request_cancel(Runtime *rt, uint32_t mailbox_id, uint64_t token) {
    Mailbox *mailbox;
    uint64_t provider_before;
    uint64_t output_before;
    if (mailbox_id >= MAX_MAILBOXES) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->pending.active == 0u || mailbox->pending.token != token ||
        mailbox->pending.cancel_requested != 0u) {
        return 0;
    }
    provider_before = provider_live_bytes(rt);
    output_before = output_live_bytes(rt);
    mailbox->pending.cancel_requested = 1u;
    if (rt->has_cancel_snapshot == 0u) {
        rt->provider_live_before_cancel = provider_before;
        rt->output_live_before_cancel = output_before;
        rt->provider_live_after_cancel = provider_live_bytes(rt);
        rt->output_live_after_cancel = output_live_bytes(rt);
        rt->has_cancel_snapshot = 1u;
    }
    return 1;
}

static int provider_acknowledge(Runtime *rt, uint32_t mailbox_id, uint64_t token) {
    Mailbox *mailbox;
    int32_t provider_arena;
    uint8_t *provider_bytes;
    if (mailbox_id >= MAX_MAILBOXES) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->pending.active == 0u || mailbox->pending.token != token ||
        mailbox->pending.ready != 0u || mailbox->pending.provider_released != 0u) {
        return 0;
    }
    provider_bytes = (uint8_t *)arena_resolve(rt, mailbox->pending.provider_ref,
                                              mailbox->pending.provider_bytes);
    if (provider_bytes == NULL || !check_body(provider_bytes, mailbox->pending.provider_bytes,
                                               mailbox->pending.provider_tag)) {
        runtime_fail(rt, "provider completion did not validate its owned buffer");
        return 0;
    }
    mailbox->pending.ready = 1u;
    provider_arena = (int32_t)(mailbox_id * 5u + ARENA_PROVIDER);
    arena_release(rt, provider_arena);
    mailbox->pending.provider_released = 1u;
    return rt->fail == 0;
}

static int message_push(Runtime *rt, uint32_t mailbox_id, const Message *message) {
    Mailbox *mailbox;
    uint32_t tail;
    if (mailbox_id >= MAX_MAILBOXES || message == NULL) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->queue_count >= MAX_QUEUE_DEPTH ||
        message->payload_bytes > MAX_QUEUE_BYTES - mailbox->queue_bytes) {
        return 0;
    }
    tail = (mailbox->queue_head + mailbox->queue_count) % MAX_QUEUE_DEPTH;
    mailbox->queue[tail] = *message;
    mailbox->queue_count += 1u;
    mailbox->queue_bytes += message->payload_bytes;
    return 1;
}

static int message_pop(Runtime *rt, uint32_t mailbox_id, Message *out_message) {
    Mailbox *mailbox;
    if (mailbox_id >= MAX_MAILBOXES || out_message == NULL) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (mailbox->queue_count == 0u) {
        return 0;
    }
    *out_message = mailbox->queue[mailbox->queue_head];
    mailbox->queue_head = (mailbox->queue_head + 1u) % MAX_QUEUE_DEPTH;
    mailbox->queue_count -= 1u;
    if (mailbox->queue_bytes < out_message->payload_bytes) {
        runtime_fail(rt, "mailbox queue-byte underflow");
        return 0;
    }
    mailbox->queue_bytes -= out_message->payload_bytes;
    return 1;
}

static int publish_output(Runtime *rt, uint32_t mailbox_id, uint32_t request_id,
                          uint64_t value) {
    int32_t output_arena = (int32_t)(mailbox_id * 5u + ARENA_OUTPUT);
    Mailbox *mailbox = &rt->mailboxes[mailbox_id];
    Ref output_ref;
    uint64_t decoded = 0;
    uint32_t bytes = rt->output_bytes;
    if (mailbox->output_live_bytes > rt->output_limit_bytes ||
        bytes > rt->output_limit_bytes - mailbox->output_live_bytes) {
        rt->results[request_id].status = STATUS_REJECTED_OUTPUT_CAPACITY;
        return 0;
    }
    if (rt->arenas[output_arena].active == 0u && !arena_acquire(rt, output_arena)) {
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    if (!output_write(rt, output_arena, bytes, request_id, value, &output_ref)) {
        rt->results[request_id].status = STATUS_REJECTED_OUTPUT_CAPACITY;
        return 0;
    }
    mailbox->output_live_bytes += bytes;
    rt->results[request_id].output_ref = output_ref;
    rt->results[request_id].output_acknowledged = 0u;
    if (!output_read(rt, output_ref, request_id, value, &decoded) || decoded != value) {
        rt->results[request_id].status = STATUS_FAILED;
        return 0;
    }
    return 1;
}

static int consumer_ack_output(Runtime *rt, uint32_t request_id) {
    Result *result;
    Mailbox *mailbox;
    uint32_t mailbox_id;
    uint32_t i;
    uint64_t decoded = 0;
    int32_t output_arena;
    if (request_id >= rt->request_count) {
        return 0;
    }
    result = &rt->results[request_id];
    if (result->status != STATUS_COMPLETED || result->output_acknowledged != 0u ||
        result->output_ref.valid == 0u) {
        return 0;
    }
    mailbox_id = result->mailbox_id;
    if (mailbox_id >= MAX_MAILBOXES) {
        return 0;
    }
    mailbox = &rt->mailboxes[mailbox_id];
    if (!output_read(rt, result->output_ref, request_id, result->value, &decoded) ||
        decoded != output_value_oracle(rt->seed, request_id)) {
        runtime_fail(rt, "consumer acknowledgment could not validate retained output");
        return 0;
    }
    if (mailbox->output_live_bytes < result->output_ref.size) {
        runtime_fail(rt, "output live-byte underflow during consumer acknowledgment");
        return 0;
    }
    mailbox->output_live_bytes -= result->output_ref.size;
    result->output_acknowledged = 1u;
    for (i = 0; i < rt->request_count; ++i) {
        Result *other = &rt->results[i];
        if (other->mailbox_id == mailbox_id && other->status == STATUS_COMPLETED &&
            other->output_acknowledged == 0u) {
            return 1;
        }
    }
    if (mailbox->output_live_bytes != 0u) {
        runtime_fail(rt, "output owner bytes remained after the last consumer acknowledgment");
        return 0;
    }
    output_arena = (int32_t)(mailbox_id * 5u + ARENA_OUTPUT);
    arena_release(rt, output_arena);
    return rt->fail == 0;
}

static void release_request_arenas(Runtime *rt, uint32_t mailbox_id, int release_continuation) {
    int32_t scratch = (int32_t)(mailbox_id * 5u + ARENA_SCRATCH);
    int32_t continuation = (int32_t)(mailbox_id * 5u + ARENA_CONTINUATION);
    arena_release(rt, scratch);
    if (release_continuation) {
        arena_release(rt, continuation);
    }
}

static int start_request(Runtime *rt, uint32_t mailbox_id, const InputPayload *input) {
    Mailbox *mailbox = &rt->mailboxes[mailbox_id];
    uint32_t request_id = input->id;
    int32_t scratch = (int32_t)(mailbox_id * 5u + ARENA_SCRATCH);
    int32_t continuation = (int32_t)(mailbox_id * 5u + ARENA_CONTINUATION);
    int32_t provider = (int32_t)(mailbox_id * 5u + ARENA_PROVIDER);
    Ref root;
    Ref retained_root;
    Ref provider_ref;
    uint8_t *provider_bytes;
    uint64_t work_checksum = 0;
    uint64_t copy_bytes = 0;
    uint64_t token;
    int delayed = rt->async_mode != 0u;

    rt->results[request_id].status = STATUS_RUNNING;
    mailbox->static_processed += 1u;
    mailbox->static_checksum = static_state_checksum(mailbox->static_marker, mailbox->static_processed);
    if (!arena_acquire(rt, scratch)) {
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    if (rt->scenario_name[0] == '\0') {
        runtime_fail(rt, "internal scenario name missing");
        return 0;
    }
    if (!build_disposable_scratch(rt, scratch, input->value, rt->working_bytes, &work_checksum)) {
        arena_release(rt, scratch);
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    if (strcmp(rt->scenario_name, "cpu") == 0) {
        work_checksum ^= cpu_control_work(input->value);
    }
    if (!create_graph(rt, scratch, request_id, input->value, rt->body_bytes, work_checksum, &root)) {
        arena_release(rt, scratch);
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    if (!delayed) {
        uint64_t actual = 0;
        Ref newest;
        uint64_t decoded = 0;
        if (!validate_graph(rt, root, input->value, rt->body_bytes, work_checksum, &actual) ||
            actual != output_value_oracle(rt->seed, request_id)) {
            arena_release(rt, scratch);
            rt->results[request_id].status = STATUS_FAILED;
            return 0;
        }
        if (!publish_output(rt, mailbox_id, request_id, actual)) {
            arena_release(rt, scratch);
            return 0;
        }
        arena_release(rt, scratch);
        {
            int32_t output_arena = (int32_t)(mailbox_id * 5u + ARENA_OUTPUT);
            Arena *a = &rt->arenas[output_arena];
            Chunk *c;
            if (a->last_chunk < 0) {
                rt->results[request_id].status = STATUS_FAILED;
                return 0;
            }
            c = &rt->pool.chunks[a->last_chunk];
            newest.arena_id = output_arena;
            newest.chunk_id = a->last_chunk;
            newest.generation = a->generation;
            newest.size = rt->output_bytes;
            newest.offset = (uint32_t)(c->cursor - rt->output_bytes);
            newest.valid = 1u;
            if (!output_read(rt, newest, request_id, actual, &decoded) || decoded != actual) {
                rt->results[request_id].status = STATUS_FAILED;
                return 0;
            }
        }
        rt->results[request_id].status = STATUS_COMPLETED;
        rt->results[request_id].value = actual;
        if (!rt->hold_output && !consumer_ack_output(rt, request_id)) {
            rt->results[request_id].status = STATUS_FAILED;
            return 0;
        }
        return 1;
    }

    if (rt->async_mode == 1u) {
        if (!arena_acquire(rt, continuation) ||
            !promote_graph(rt, root, continuation, rt->body_bytes, input->value,
                           work_checksum, (uint64_t)CHUNK_BYTES, &retained_root, &copy_bytes)) {
            arena_release(rt, continuation);
            arena_release(rt, scratch);
            rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
            return 0;
        }
        rt->continuation_copy_bytes += copy_bytes;
    } else {
        retained_root = root;
    }

    if (!arena_acquire(rt, provider)) {
        release_request_arenas(rt, mailbox_id, rt->async_mode == 1u);
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    provider_ref = arena_allocate(rt, provider, rt->provider_bytes, 8u);
    provider_bytes = (uint8_t *)arena_resolve(rt, provider_ref, rt->provider_bytes);
    if (provider_bytes == NULL) {
        release_request_arenas(rt, mailbox_id, rt->async_mode == 1u);
        arena_release(rt, provider);
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return 0;
    }
    fill_body(provider_bytes, rt->provider_bytes, input->value ^ 0xa55aa55aULL);

    token = ++rt->next_token;
    if (token == 0u) {
        token = ++rt->next_token;
    }
    if (!pending_begin(rt, mailbox_id, request_id,
                       rt->async_mode == 1u ? continuation : scratch,
                       retained_root, input->value, work_checksum,
                       rt->tick + rt->delay_ticks, token)) {
        release_request_arenas(rt, mailbox_id, rt->async_mode == 1u);
        arena_release(rt, provider);
        return 0;
    }
    if (!pending_attach_provider(rt, mailbox_id, token, provider_ref, rt->provider_bytes,
                                 input->value ^ 0xa55aa55aULL)) {
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        release_request_arenas(rt, mailbox_id, rt->async_mode == 1u);
        arena_release(rt, provider);
        rt->results[request_id].status = STATUS_FAILED;
        return 0;
    }
    mailbox->pending.root_arena_id = (uint32_t)(rt->async_mode == 1u ? continuation : scratch);
    if (rt->cancellation_scenario != 0u && request_id % 3u == 0u) {
        (void)request_cancel(rt, mailbox_id, token);
    }
    if (rt->async_mode == 1u) {
        /* Provider and continuation owners are established before scratch is returned. */
        arena_release(rt, scratch);
    }
    return 1;
}

static void dispatch_mailbox(Runtime *rt, uint32_t mailbox_id);

static int resume_pending(Runtime *rt, uint32_t mailbox_id, uint64_t token) {
    Mailbox *mailbox = &rt->mailboxes[mailbox_id];
    PendingOperation pending;
    int32_t scratch = (int32_t)(mailbox_id * 5u + ARENA_SCRATCH);
    uint32_t request_id;
    uint64_t actual = 0;
    uint64_t expected_work = 0;
    int release_continuation;
    Ref frame_ref;
    ResumeFrame *frame;

    if (mailbox->pending.active == 0u || mailbox->pending.token != token ||
        mailbox->pending.ready == 0u || mailbox->pending.provider_released == 0u) {
        return 0;
    }
    pending = mailbox->pending;
    request_id = pending.request_id;
    release_continuation = rt->async_mode == 1u;
    mailbox->resume_count += 1u;
    if (pending.cancel_requested != 0u) {
        rt->results[request_id].status = STATUS_CANCELLED;
        release_request_arenas(rt, mailbox_id, release_continuation);
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 1;
    }
    if (rt->async_mode == 1u) {
        if (!arena_acquire(rt, scratch)) {
            rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
            release_request_arenas(rt, mailbox_id, 1);
            memset(&mailbox->pending, 0, sizeof(mailbox->pending));
            return 0;
        }
    } else if (rt->arenas[scratch].active == 0u) {
        runtime_fail(rt, "whole-request scratch owner ended before resume");
        return 0;
    }
    frame_ref = arena_allocate(rt, scratch, sizeof(ResumeFrame), 8u);
    frame = (ResumeFrame *)arena_resolve(rt, frame_ref, sizeof(ResumeFrame));
    if (frame == NULL) {
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        release_request_arenas(rt, mailbox_id, release_continuation);
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 0;
    }
    frame->token = token;
    frame->resume_pc = 7u;
    frame->mailbox_id = mailbox_id;
    if (frame->token != token || frame->resume_pc != 7u || frame->mailbox_id != mailbox_id) {
        rt->results[request_id].status = STATUS_FAILED;
        release_request_arenas(rt, mailbox_id, release_continuation);
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 0;
    }

    if (rt->scenario_name[0] == '\0') {
        runtime_fail(rt, "internal scenario name missing during resume");
        return 0;
    }
    expected_work = pending.expected_work;
    if (!validate_graph(rt, pending.root, pending.expected_value, rt->body_bytes,
                        expected_work, &actual) ||
        actual != output_value_oracle(rt->seed, request_id)) {
        rt->results[request_id].status = STATUS_FAILED;
        release_request_arenas(rt, mailbox_id, release_continuation);
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 0;
    }
    if (!publish_output(rt, mailbox_id, request_id, actual)) {
        release_request_arenas(rt, mailbox_id, release_continuation);
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 0;
    }
    release_request_arenas(rt, mailbox_id, release_continuation);
    {
        int32_t output_arena = (int32_t)(mailbox_id * 5u + ARENA_OUTPUT);
        Arena *a = &rt->arenas[output_arena];
        if (a->last_chunk < 0) {
            rt->results[request_id].status = STATUS_FAILED;
            memset(&mailbox->pending, 0, sizeof(mailbox->pending));
            return 0;
        }
        {
            Chunk *c = &rt->pool.chunks[a->last_chunk];
            Ref newest;
            uint64_t decoded = 0;
            newest.arena_id = output_arena;
            newest.chunk_id = a->last_chunk;
            newest.generation = a->generation;
            newest.size = rt->output_bytes;
            newest.offset = (uint32_t)(c->cursor - rt->output_bytes);
            newest.valid = 1u;
            if (!output_read(rt, newest, request_id, actual, &decoded) || decoded != actual) {
                rt->results[request_id].status = STATUS_FAILED;
                memset(&mailbox->pending, 0, sizeof(mailbox->pending));
                return 0;
            }
        }
    }
    rt->results[request_id].status = STATUS_COMPLETED;
    rt->results[request_id].value = actual;
    if (!rt->hold_output && !consumer_ack_output(rt, request_id)) {
        rt->results[request_id].status = STATUS_FAILED;
        memset(&mailbox->pending, 0, sizeof(mailbox->pending));
        return 0;
    }
    memset(&mailbox->pending, 0, sizeof(mailbox->pending));
    return 1;
}

static int service_due(Runtime *rt) {
    uint32_t i;
    int progressed = 0;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        Mailbox *mailbox = &rt->mailboxes[i];
        if (mailbox->pending.active != 0u && mailbox->pending.due_tick <= rt->tick) {
            uint64_t token = mailbox->pending.token;
            if (provider_acknowledge(rt, i, token)) {
                (void)resume_pending(rt, i, token);
                dispatch_mailbox(rt, i);
                progressed = 1;
            }
        }
    }
    return progressed;
}

static void dispatch_mailbox(Runtime *rt, uint32_t mailbox_id) {
    Mailbox *mailbox = &rt->mailboxes[mailbox_id];
    while (rt->fail == 0 && mailbox->pending.active == 0u && mailbox->queue_count != 0u) {
        Message message;
        InputPayload *payload;
        InputPayload input;
        int32_t inbox = (int32_t)(mailbox_id * 5u + ARENA_INBOX);
        if (!message_pop(rt, mailbox_id, &message)) {
            runtime_fail(rt, "mailbox queue dequeue failed");
            return;
        }
        payload = (InputPayload *)arena_resolve(rt, message.payload, sizeof(InputPayload));
        if (payload == NULL || payload->id != message.request_id) {
            rt->results[message.request_id].status = STATUS_FAILED;
            runtime_fail(rt, "queued payload handle expired before dequeue");
            return;
        }
        input = *payload;
        (void)start_request(rt, mailbox_id, &input);
        if (mailbox->queue_count == 0u) {
            (void)arena_reset(rt, inbox);
        }
    }
}

static void offer_request(Runtime *rt, uint32_t request_id) {
    uint32_t mailbox_id = request_id % MAX_MAILBOXES;
    Mailbox *mailbox = &rt->mailboxes[mailbox_id];
    int32_t inbox = (int32_t)(mailbox_id * 5u + ARENA_INBOX);
    InputPayload *payload;
    Ref payload_ref;
    Message message;
    uint64_t value = output_value_oracle(rt->seed, request_id);
    if (rt->results[request_id].status != STATUS_NONE) {
        runtime_fail(rt, "request offered more than once");
        return;
    }
    rt->results[request_id].mailbox_id = mailbox_id;
    if (mailbox->queue_count >= MAX_QUEUE_DEPTH || mailbox->queue_bytes > MAX_QUEUE_BYTES - sizeof(InputPayload)) {
        rt->results[request_id].status = STATUS_REJECTED_QUEUE;
        return;
    }
    if (rt->arenas[inbox].active == 0u && !arena_acquire(rt, inbox)) {
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return;
    }
    payload_ref = arena_allocate(rt, inbox, sizeof(InputPayload), 8u);
    payload = (InputPayload *)arena_resolve(rt, payload_ref, sizeof(InputPayload));
    if (payload == NULL) {
        rt->results[request_id].status = STATUS_REJECTED_CAPACITY;
        return;
    }
    payload->id = request_id;
    payload->seed = rt->seed;
    payload->value = value;
    message.request_id = request_id;
    message.payload_bytes = sizeof(InputPayload);
    message.payload = payload_ref;
    if (!message_push(rt, mailbox_id, &message)) {
        rt->results[request_id].status = STATUS_REJECTED_QUEUE;
        return;
    }
    rt->results[request_id].status = STATUS_QUEUED;
    dispatch_mailbox(rt, mailbox_id);
}

static int workload_is_idle(const Runtime *rt) {
    uint32_t i;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        if (rt->mailboxes[i].pending.active != 0u || rt->mailboxes[i].queue_count != 0u) {
            return 0;
        }
    }
    return 1;
}

static uint64_t next_due_tick(const Runtime *rt) {
    uint32_t i;
    uint64_t next = UINT64_MAX;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        const Mailbox *mailbox = &rt->mailboxes[i];
        if (mailbox->pending.active != 0u && mailbox->pending.due_tick < next) {
            next = mailbox->pending.due_tick;
        }
    }
    return next;
}

static int run_workload(Runtime *rt) {
    uint32_t i;
    for (i = 0; i < rt->request_count && rt->fail == 0; ++i) {
        (void)service_due(rt);
        offer_request(rt, i);
        rt->tick += 1u;
    }
    while (rt->fail == 0 && !workload_is_idle(rt)) {
        uint64_t next = next_due_tick(rt);
        if (next == UINT64_MAX) {
            runtime_fail(rt, "scheduler stalled with queued messages and no pending event");
            break;
        }
        if (next > rt->tick) {
            rt->tick = next;
        }
        if (!service_due(rt)) {
            runtime_fail(rt, "scheduler made no progress at a due tick");
        }
    }
    if (rt->hold_output != 0u) {
        uint32_t i;
        for (i = 0; i < rt->request_count; ++i) {
            if (rt->results[i].status == STATUS_COMPLETED &&
                !consumer_ack_output(rt, i)) {
                runtime_fail(rt, "slow output consumer acknowledgment failed");
                break;
            }
        }
        for (i = 0; i < MAX_MAILBOXES && rt->fail == 0; ++i) {
            int32_t output_arena = (int32_t)(i * 5u + ARENA_OUTPUT);
            if (rt->mailboxes[i].output_live_bytes != 0u) {
                runtime_fail(rt, "slow output owner remained live after consumer drain");
                break;
            }
            arena_release(rt, output_arena);
        }
    }
    return rt->fail == 0;
}

static void initialize_runtime(Runtime *rt, uint64_t budget_bytes, size_t page_bytes) {
    uint32_t i;
    memset(rt, 0, sizeof(*rt));
    rt->budget_bytes = budget_bytes;
    rt->fixed_metadata_bytes = sizeof(*rt);
    rt->pool.free_head = -1;
    rt->pool.page_bytes = page_bytes;
    for (i = 0; i < MAX_CHUNKS; ++i) {
        rt->pool.chunks[i].owner_arena = CHUNK_EMPTY;
        rt->pool.chunks[i].next_arena = -1;
        rt->pool.chunks[i].next_free = -1;
    }
    for (i = 0; i < MAX_ARENAS; ++i) {
        rt->arenas[i].first_chunk = -1;
        rt->arenas[i].last_chunk = -1;
    }
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        rt->mailboxes[i].static_marker = 0x414c4d4200000000ULL | i;
        rt->mailboxes[i].static_checksum = static_state_checksum(rt->mailboxes[i].static_marker, 0u);
    }
}

static void set_workload_shape(Runtime *rt, const char *mode, const char *scenario,
                               uint32_t seed, uint32_t request_count) {
    rt->seed = seed;
    rt->request_count = request_count;
    rt->scenario_name = scenario;
    rt->mode_name = mode;
    rt->async_mode = strcmp(scenario, "cpu") == 0 ? 0u :
        (strcmp(mode, "turn") == 0 ? 1u : 2u);
    rt->hold_output = strcmp(scenario, "slow-output") == 0 ? 1u : 0u;
    rt->cancellation_scenario = strcmp(scenario, "cancellation") == 0 ? 1u : 0u;
    rt->output_limit_bytes = MAX_OUTPUT_BYTES_PER_MAILBOX;
    rt->body_bytes = 128u;
    rt->provider_bytes = 1024u;
    rt->output_bytes = 64u;
    rt->delay_ticks = 24u;
    rt->working_bytes = WORKING_SCRATCH_BYTES;
    if (strcmp(scenario, "cpu") == 0) {
        rt->body_bytes = 1024u;
        rt->provider_bytes = 0u;
        rt->output_bytes = 64u;
        rt->delay_ticks = 0u;
    } else if (strcmp(scenario, "delayed-small") == 0) {
        rt->body_bytes = 128u;
        rt->provider_bytes = 1024u;
        rt->output_bytes = 64u;
        rt->delay_ticks = 24u;
    } else if (strcmp(scenario, "delayed-large") == 0) {
        rt->body_bytes = 8192u;
        rt->provider_bytes = 4096u;
        rt->output_bytes = 128u;
        rt->delay_ticks = 40u;
    } else if (strcmp(scenario, "slow-output") == 0) {
        rt->body_bytes = 128u;
        rt->provider_bytes = 512u;
        rt->output_bytes = 4096u;
        rt->delay_ticks = 24u;
    } else if (strcmp(scenario, "cancellation") == 0) {
        rt->body_bytes = 512u;
        rt->provider_bytes = 4096u;
        rt->output_bytes = 64u;
        rt->delay_ticks = 24u;
    } else if (strcmp(scenario, "retained-only") == 0) {
        rt->body_bytes = 8192u;
        rt->provider_bytes = 4096u;
        rt->output_bytes = 128u;
        rt->delay_ticks = 40u;
        rt->working_bytes = 0u;
    }
}

static int verify_static_state(Runtime *rt) {
    uint32_t i;
    for (i = 0; i < MAX_MAILBOXES; ++i) {
        Mailbox *mailbox = &rt->mailboxes[i];
        if (mailbox->static_marker != (0x414c4d4200000000ULL | i) ||
            mailbox->static_checksum != static_state_checksum(mailbox->static_marker,
                                                               mailbox->static_processed)) {
            return 0;
        }
    }
    return 1;
}

static int parse_u64(const char *text, uint64_t *value) {
    char *end = NULL;
    unsigned long long parsed;
    if (text == NULL || text[0] == '\0' || text[0] == '-') {
        return 0;
    }
    parsed = strtoull(text, &end, 10);
    if (end == text || end == NULL || *end != '\0') {
        return 0;
    }
    *value = (uint64_t)parsed;
    return 1;
}

static int parse_options(int argc, char **argv, CliOptions *options) {
    int i;
    memset(options, 0, sizeof(*options));
    options->process_limit_bytes = 64u * 1024u * 1024u;
    for (i = 1; i < argc; ++i) {
        uint64_t value;
        if (strcmp(argv[i], "--self-test") == 0) {
            options->self_test = 1u;
        } else if (strcmp(argv[i], "--mode") == 0 && i + 1 < argc) {
            options->mode = argv[++i];
            options->has_mode = 1u;
        } else if (strcmp(argv[i], "--scenario") == 0 && i + 1 < argc) {
            options->scenario = argv[++i];
            options->has_scenario = 1u;
        } else if (strcmp(argv[i], "--seed") == 0 && i + 1 < argc &&
                   parse_u64(argv[++i], &value) && value <= UINT32_MAX) {
            options->seed = (uint32_t)value;
            options->has_seed = 1u;
        } else if (strcmp(argv[i], "--requests") == 0 && i + 1 < argc &&
                   parse_u64(argv[++i], &value) && value <= MAX_REQUESTS) {
            options->requests = (uint32_t)value;
            options->has_requests = 1u;
        } else if (strcmp(argv[i], "--budget-bytes") == 0 && i + 1 < argc &&
                   parse_u64(argv[++i], &value)) {
            options->budget_bytes = value;
            options->has_budget = 1u;
        } else if (strcmp(argv[i], "--process-limit-bytes") == 0 && i + 1 < argc &&
                   parse_u64(argv[++i], &value)) {
            options->process_limit_bytes = value;
            options->has_process_limit = 1u;
        } else {
            return 0;
        }
    }
    if (options->self_test != 0u) {
        return options->has_mode == 0u && options->has_scenario == 0u &&
               options->has_seed == 0u && options->has_requests == 0u &&
               options->has_budget == 0u;
    }
    return options->has_mode != 0u && options->has_scenario != 0u &&
           options->has_seed != 0u && options->has_requests != 0u &&
           options->has_budget != 0u && options->has_process_limit != 0u &&
           options->requests > 0u &&
           (strcmp(options->mode, "turn") == 0 || strcmp(options->mode, "request") == 0) &&
           (strcmp(options->scenario, "cpu") == 0 ||
            strcmp(options->scenario, "delayed-small") == 0 ||
            strcmp(options->scenario, "delayed-large") == 0 ||
            strcmp(options->scenario, "slow-output") == 0 ||
            strcmp(options->scenario, "cancellation") == 0 ||
            strcmp(options->scenario, "retained-only") == 0);
}

static int enforce_process_limit(uint64_t limit_bytes, JobLimit *job) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION configured;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION queried;
    SYSTEM_INFO system_info;
    SIZE_T probe_bytes;
    void *probe;
    if (job == NULL || limit_bytes == 0u || limit_bytes > (uint64_t)SIZE_MAX) {
        return 0;
    }
    memset(job, 0, sizeof(*job));
    job->handle = CreateJobObjectW(NULL, NULL);
    if (job->handle == NULL) {
        return 0;
    }
    memset(&configured, 0, sizeof(configured));
    configured.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    configured.ProcessMemoryLimit = (SIZE_T)limit_bytes;
    if (!SetInformationJobObject(job->handle, JobObjectExtendedLimitInformation,
                                 &configured, sizeof(configured))) {
        CloseHandle(job->handle);
        job->handle = NULL;
        return 0;
    }
    if (!AssignProcessToJobObject(job->handle, GetCurrentProcess())) {
        CloseHandle(job->handle);
        job->handle = NULL;
        return 0;
    }
    memset(&queried, 0, sizeof(queried));
    if (!QueryInformationJobObject(job->handle, JobObjectExtendedLimitInformation,
                                   &queried, sizeof(queried), NULL) ||
        (queried.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_PROCESS_MEMORY) == 0u ||
        queried.ProcessMemoryLimit != (SIZE_T)limit_bytes) {
        CloseHandle(job->handle);
        job->handle = NULL;
        return 0;
    }
    GetSystemInfo(&system_info);
    if (limit_bytes > (uint64_t)SIZE_MAX - system_info.dwPageSize) {
        CloseHandle(job->handle);
        job->handle = NULL;
        return 0;
    }
    probe_bytes = (SIZE_T)limit_bytes + system_info.dwPageSize;
    probe = VirtualAlloc(NULL, probe_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (probe != NULL) {
        (void)VirtualFree(probe, 0, MEM_RELEASE);
        CloseHandle(job->handle);
        job->handle = NULL;
        return 0;
    }
    job->limit_bytes = limit_bytes;
    job->enforced = 1u;
    return 1;
}

static uint64_t query_process_peak_commit(JobLimit *job) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
    if (job == NULL || job->handle == NULL ||
        !QueryInformationJobObject(job->handle, JobObjectExtendedLimitInformation,
                                   &info, sizeof(info), NULL)) {
        return 0u;
    }
    return (uint64_t)info.PeakProcessMemoryUsed;
}

static int query_process_memory(uint64_t *private_bytes, uint64_t *working_set_bytes) {
    PROCESS_MEMORY_COUNTERS_EX counters;
    memset(&counters, 0, sizeof(counters));
    if (!GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&counters,
                              sizeof(counters))) {
        return 0;
    }
    *private_bytes = (uint64_t)counters.PrivateUsage;
    *working_set_bytes = (uint64_t)counters.WorkingSetSize;
    return 1;
}

static void print_workload_json(Runtime *rt, const CliOptions *options, JobLimit *job,
                                uint64_t elapsed_ticks, uint64_t qpc_frequency,
                                uint64_t private_bytes, uint64_t working_set_bytes,
                                uint32_t drained, uint32_t active_owners, uint32_t static_preserved) {
    uint32_t i;
    uint32_t completed = 0;
    uint32_t rejected = 0;
    uint32_t cancelled = 0;
    uint32_t failed = 0;
    double elapsed_ms = qpc_frequency == 0u ? 0.0 :
        ((double)elapsed_ticks * 1000.0) / (double)qpc_frequency;
    for (i = 0; i < options->requests; ++i) {
        ResultStatus status = rt->results[i].status;
        completed += status == STATUS_COMPLETED ? 1u : 0u;
        rejected += is_rejected(status) ? 1u : 0u;
        cancelled += status == STATUS_CANCELLED ? 1u : 0u;
        failed += status == STATUS_FAILED ? 1u : 0u;
    }
    printf("{\"schemaVersion\":1,\"kind\":\"workload\",\"mode\":\"%s\",\"scenario\":\"%s\","
           "\"seed\":%u,\"requestsOffered\":%u,\"mailboxCount\":%u,"
           "\"workingScratchBytes\":%u,\"cacheCapBytes\":%u,"
           "\"budgetBytes\":%" PRIu64 ",\"processLimitBytes\":%" PRIu64 ","
           "\"processLimitEnforced\":%s,\"results\":[",
           options->mode, options->scenario, options->seed, options->requests, MAX_MAILBOXES,
           rt->working_bytes, MAX_CACHED_CHUNKS * CHUNK_BYTES,
           options->budget_bytes, options->process_limit_bytes,
           job->enforced != 0u ? "true" : "false");
    for (i = 0; i < options->requests; ++i) {
        Result *result = &rt->results[i];
        if (i != 0u) {
            putchar(',');
        }
        printf("{\"id\":%u,\"mailboxId\":%u,\"status\":\"%s\",\"value\":",
               i, result->mailbox_id, status_name(result->status));
        if (result->status == STATUS_COMPLETED) {
            printf("%" PRIu64, result->value);
        } else {
            printf("null");
        }
        putchar('}');
    }
    printf("],\"completed\":%u,\"rejected\":%u,\"cancelled\":%u,\"failed\":%u,"
           "\"continuationCopyBytes\":%" PRIu64 ",\"fixedMetadataBytes\":%" PRIu64 ","
           "\"arenaUsedBytes\":%" PRIu64 ",\"arenaReservedBytes\":%" PRIu64 ","
           "\"arenaPeakUsedBytes\":%" PRIu64 ",\"arenaPeakReservedBytes\":%" PRIu64 ","
           "\"arenaCachedBytes\":%" PRIu64 ",\"arenaPeakCachedBytes\":%" PRIu64 ","
           "\"allocatorCalls\":%" PRIu64 ",\"freeCalls\":%" PRIu64 ","
           "\"providerLiveBytesBeforeCancel\":%" PRIu64 ","
           "\"providerLiveBytesAfterCancel\":%" PRIu64 ","
           "\"providerLiveBytesAtDrain\":%" PRIu64 ","
           "\"outputLiveBytesBeforeCancel\":%" PRIu64 ","
           "\"outputLiveBytesAfterCancel\":%" PRIu64 ","
           "\"outputLiveBytesAtDrain\":%" PRIu64 ","
           "\"privateBytes\":%" PRIu64 ",\"workingSetBytes\":%" PRIu64 ","
           "\"processPeakCommittedBytes\":%" PRIu64 ","
           "\"elapsedQpcTicks\":%" PRIu64 ",\"qpcFrequency\":%" PRIu64 ","
           "\"elapsedMilliseconds\":%.6f,\"drained\":%s,"
           "\"activeOwnerCountAtDrain\":%u,\"staticStatePreserved\":%s}\n",
           completed, rejected, cancelled, failed, rt->continuation_copy_bytes,
           rt->fixed_metadata_bytes, rt->arena_used_bytes, rt->pool.reserved_bytes,
           rt->arena_peak_used_bytes, rt->pool.peak_reserved_bytes,
           current_cached_bytes(rt), rt->pool.peak_cached_bytes,
           rt->pool.allocator_calls, rt->pool.free_calls,
           rt->provider_live_before_cancel, rt->provider_live_after_cancel,
           provider_live_bytes(rt), rt->output_live_before_cancel,
           rt->output_live_after_cancel, output_live_bytes(rt),
           private_bytes, working_set_bytes, query_process_peak_commit(job),
           elapsed_ticks, qpc_frequency, elapsed_ms,
           drained != 0u ? "true" : "false", active_owners,
           static_preserved != 0u ? "true" : "false");
}

static uint32_t g_assertions;
static char g_test_failure[192];

static int check_assert(int condition, const char *description) {
    g_assertions += 1u;
    if (!condition && g_test_failure[0] == '\0') {
        copy_bounded_string(g_test_failure, sizeof(g_test_failure), description);
    }
    return condition;
}

static void selftest_init(Runtime *rt, uint64_t extra_budget) {
    SYSTEM_INFO system_info;
    GetSystemInfo(&system_info);
    initialize_runtime(rt, sizeof(*rt) + extra_budget, system_info.dwPageSize);
}

static int selftest_drain(Runtime *rt) {
    int retired = retire_empty_owners(rt);
    check_assert(retired != 0, "self-test reached drain with live work or arena owners");
    check_assert(count_active_owners(rt) == 0u,
                 "self-test owner retirement was not complete before pool drain");
    int ok = pool_drain(rt);
    check_assert(ok != 0, "pool drain failed");
    check_assert(count_active_owners(rt) == 0u, "dynamic arena owner survived drain");
    check_assert(rt->pool.reserved_bytes == 0u && current_cached_bytes(rt) == 0u,
                 "arena backing survived drain");
    check_assert(rt->arena_used_bytes == 0u, "arena live bytes survived drain");
    check_assert(rt->pool.allocator_calls == rt->pool.free_calls,
                 "VirtualAlloc and VirtualFree call counts differ after drain");
    return ok;
}

static void selftest_strict_drain_rejects_live_owner(Runtime *rt) {
    Ref live_ref;
    uint64_t reserved_before;
    selftest_init(rt, 2u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "strict-drain owner acquire failed");
    live_ref = arena_allocate(rt, 0, 64u, 8u);
    check_assert(live_ref.valid != 0u, "strict-drain live allocation failed");
    reserved_before = rt->pool.reserved_bytes;
    check_assert(!pool_drain(rt), "pool drain accepted a live arena owner");
    check_assert(rt->arenas[0].active != 0u && arena_resolve(rt, live_ref, 64u) != NULL &&
                 rt->pool.reserved_bytes == reserved_before && rt->pool.free_calls == 0u,
                 "rejected pool drain changed a live owner or its backing allocation");
    rt->fail = 0;
    arena_release(rt, 0);
    (void)selftest_drain(rt);
}

static void selftest_normal_completion(Runtime *rt) {
    Ref root;
    uint64_t actual = 0;
    uint64_t expected = output_value_oracle(17u, 3u);
    uint64_t work = 0;
    selftest_init(rt, 4u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "normal test scratch acquire failed");
    check_assert(create_graph(rt, 0, 3u, expected, 128u, work, &root), "normal test graph creation failed");
    check_assert(validate_graph(rt, root, expected, 128u, work, &actual), "normal graph validation failed");
    check_assert(actual == expected, "normal output value mismatch");
    arena_release(rt, 0);
    (void)selftest_drain(rt);
}

static void selftest_nested_promotion(Runtime *rt) {
    Ref source;
    Ref promoted;
    uint64_t copied = 0;
    uint64_t actual = 0;
    uint64_t value = output_value_oracle(19u, 8u);
    selftest_init(rt, 4u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "promotion source acquire failed");
    check_assert(arena_acquire(rt, 1), "promotion destination acquire failed");
    check_assert(create_graph(rt, 0, 8u, value, 8192u, 55u, &source), "promotion source graph creation failed");
    check_assert(promote_graph(rt, source, 1, 8192u, value, 55u, CHUNK_BYTES,
                               &promoted, &copied), "nested promotion failed");
    check_assert(copied == 8192u + sizeof(PayloadValue) + sizeof(RootValue),
                 "nested promotion byte count mismatch");
    check_assert(validate_graph(rt, promoted, value, 8192u, 55u, &actual),
                 "promoted graph type, alias, or body validation failed");
    check_assert(actual == value, "promoted graph value mismatch");
    arena_release(rt, 0);
    check_assert(validate_graph(rt, promoted, value, 8192u, 55u, &actual),
                 "promoted graph depended on released scratch");
    arena_release(rt, 1);
    (void)selftest_drain(rt);
}

static void selftest_output_survives_reset(Runtime *rt) {
    Ref output;
    uint64_t decoded = 0;
    uint64_t value = 0x1234abcdu;
    selftest_init(rt, 4u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "output test scratch acquire failed");
    check_assert(arena_acquire(rt, 1), "output test output acquire failed");
    check_assert(arena_allocate(rt, 0, 64u, 8u).valid != 0u, "output test scratch allocation failed");
    check_assert(output_write(rt, 1, 128u, 4u, value, &output), "output buffer allocation failed");
    arena_release(rt, 0);
    check_assert(output_read(rt, output, 4u, value, &decoded) && decoded == value,
                 "output buffer expired with scratch reset");
    arena_release(rt, 1);
    (void)selftest_drain(rt);
}

static void selftest_stale_handle_reuse(Runtime *rt) {
    Ref old_ref;
    Ref new_ref;
    Ref wrong_owner_ref;
    Ref out_of_bounds_ref;
    void *old_address;
    void *new_address;
    selftest_init(rt, 2u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "stale test first acquire failed");
    old_ref = arena_allocate(rt, 0, 32u, 8u);
    old_address = arena_resolve(rt, old_ref, 32u);
    check_assert(old_address != NULL, "stale test first allocation failed");
    arena_release(rt, 0);
    check_assert(arena_acquire(rt, 0), "stale test second acquire failed");
    check_assert(arena_acquire(rt, 1), "stale test second owner acquire failed");
    new_ref = arena_allocate(rt, 0, 32u, 8u);
    new_address = arena_resolve(rt, new_ref, 32u);
    check_assert(new_address == old_address, "arena pool did not reuse the physical chunk");
    check_assert(arena_resolve(rt, old_ref, 32u) == NULL, "stale reference survived generation change");
    wrong_owner_ref = new_ref;
    wrong_owner_ref.arena_id = 1;
    check_assert(arena_resolve(rt, wrong_owner_ref, 32u) == NULL,
                 "reference resolved through the wrong arena owner");
    out_of_bounds_ref = new_ref;
    out_of_bounds_ref.offset = CHUNK_BYTES - 8u;
    check_assert(arena_resolve(rt, out_of_bounds_ref, 32u) == NULL,
                 "reference crossing the chunk boundary was accepted");
    check_assert(new_address != NULL, "new generation reference failed");
    arena_release(rt, 1);
    arena_release(rt, 0);
    (void)selftest_drain(rt);
}

static void selftest_partial_promotion_rollback(Runtime *rt) {
    Ref source;
    Ref promoted = { -1, -1, 0, 0, 0, 0 };
    uint64_t copied = 0;
    uint64_t value = 0x98765432u;
    selftest_init(rt, 3u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "partial promotion source acquire failed");
    check_assert(arena_acquire(rt, 1), "partial promotion destination acquire failed");
    check_assert(create_graph(rt, 0, 2u, value, 512u, 9u, &source), "partial promotion source graph failed");
    check_assert(!promote_graph(rt, source, 1, 512u, value, 9u,
                               512u + sizeof(PayloadValue), &promoted, &copied),
                 "bounded partial promotion unexpectedly succeeded");
    check_assert(promoted.valid == 0u, "partial promotion published a root");
    check_assert(rt->arenas[1].used_bytes == 0u, "partial promotion rollback retained bytes");
    check_assert(validate_graph(rt, source, value, 512u, 9u, NULL),
                 "partial promotion rollback damaged source");
    arena_release(rt, 0);
    arena_release(rt, 1);
    (void)selftest_drain(rt);
}

static void selftest_capacity_exhaustion(Runtime *rt) {
    Ref first;
    Ref second;
    selftest_init(rt, CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "capacity first owner acquire failed");
    check_assert(arena_acquire(rt, 1), "capacity second owner acquire failed");
    first = arena_allocate(rt, 0, 8u, 8u);
    second = arena_allocate(rt, 1, 8u, 8u);
    check_assert(first.valid != 0u, "capacity first allocation failed");
    check_assert(second.valid == 0u, "capacity exceeded charged backing limit");
    check_assert(rt->fixed_metadata_bytes + rt->pool.reserved_bytes <= rt->budget_bytes,
                 "capacity accounting exceeded budget");
    arena_release(rt, 0);
    arena_release(rt, 1);
    (void)selftest_drain(rt);
}

static void selftest_queue_fifo_and_backpressure(Runtime *rt) {
    Message message;
    Message popped;
    uint32_t i;
    selftest_init(rt, 2u * CHUNK_BYTES);
    memset(&message, 0, sizeof(message));
    for (i = 0; i < MAX_QUEUE_DEPTH; ++i) {
        message.request_id = 40u + i;
        message.payload_bytes = sizeof(InputPayload);
        check_assert(message_push(rt, 0, &message), "bounded FIFO queue rejected within capacity");
    }
    message.request_id = 99u;
    check_assert(!message_push(rt, 0, &message), "bounded FIFO queue accepted beyond capacity");
    for (i = 0; i < MAX_QUEUE_DEPTH; ++i) {
        check_assert(message_pop(rt, 0, &popped), "FIFO dequeue failed");
        check_assert(popped.request_id == 40u + i, "mailbox did not preserve FIFO order");
    }
    check_assert(rt->mailboxes[0].queue_count == 0u && rt->mailboxes[0].queue_bytes == 0u,
                 "mailbox queue accounting did not drain");
    message.request_id = 100u;
    message.payload_bytes = MAX_QUEUE_BYTES;
    check_assert(message_push(rt, 0, &message), "mailbox byte-capacity boundary was rejected");
    message.request_id = 101u;
    message.payload_bytes = 1u;
    check_assert(!message_push(rt, 0, &message), "mailbox queue exceeded its byte limit");
    check_assert(rt->mailboxes[0].queue_count == 1u &&
                 rt->mailboxes[0].queue_bytes == MAX_QUEUE_BYTES,
                 "mailbox byte-limit rejection changed queue accounting");
    check_assert(message_pop(rt, 0, &popped) && popped.request_id == 100u,
                 "mailbox byte-limited message could not be dequeued");
    check_assert(rt->mailboxes[0].queue_count == 0u && rt->mailboxes[0].queue_bytes == 0u,
                 "mailbox byte-limited queue did not drain");
    (void)selftest_drain(rt);
}

static void selftest_pending_exhaustion(Runtime *rt) {
    Ref root = { -1, -1, 0, 0, 0, 0 };
    selftest_init(rt, 2u * CHUNK_BYTES);
    check_assert(pending_begin(rt, 0, 0u, 0, root, 5u, 0u, 4u, 11u),
                 "first pending operation was not admitted");
    check_assert(!pending_begin(rt, 0, 1u, 0, root, 6u, 0u, 5u, 12u),
                 "mailbox accepted a second pending operation");
    check_assert(rt->results[1].status == STATUS_REJECTED_PENDING,
                 "pending capacity rejection was not recorded");
    memset(&rt->mailboxes[0].pending, 0, sizeof(rt->mailboxes[0].pending));
    (void)selftest_drain(rt);
}

static void selftest_output_exhaustion(Runtime *rt) {
    uint32_t i;
    selftest_init(rt, 2u * CHUNK_BYTES);
    rt->request_count = 3u;
    rt->seed = 17u;
    rt->output_limit_bytes = 2u * (uint32_t)sizeof(OutputBuffer);
    rt->output_bytes = (uint32_t)sizeof(OutputBuffer);
    for (i = 0; i < 2u; ++i) {
        rt->results[i].mailbox_id = 0u;
        rt->results[i].value = output_value_oracle(rt->seed, i);
        rt->results[i].status = STATUS_COMPLETED;
        check_assert(publish_output(rt, 0u, i, rt->results[i].value),
                     "bounded output allocation failed");
    }
    rt->results[2].mailbox_id = 0u;
    check_assert(!publish_output(rt, 0u, 2u, output_value_oracle(rt->seed, 2u)),
                 "output byte limit accepted a third buffer");
    check_assert(rt->mailboxes[0].output_live_bytes == rt->output_limit_bytes,
                 "output exhaustion changed live-byte accounting");
    check_assert(rt->results[2].status == STATUS_REJECTED_OUTPUT_CAPACITY,
                 "output exhaustion was not reported as a structured rejection");
    check_assert(consumer_ack_output(rt, 0u), "first bounded output acknowledgment failed");
    check_assert(consumer_ack_output(rt, 1u), "last bounded output acknowledgment failed");
    check_assert(rt->mailboxes[0].output_live_bytes == 0u,
                 "bounded output acknowledgments did not release all live bytes");
    (void)selftest_drain(rt);
}

static void selftest_cancellation_ownership(Runtime *rt) {
    Ref provider;
    uint8_t *provider_bytes;
    uint64_t before_provider;
    uint64_t before_output;
    selftest_init(rt, 4u * CHUNK_BYTES);
    rt->request_count = 3u;
    rt->seed = 17u;
    rt->hold_output = 1u;
    rt->output_bytes = 64u;
    rt->output_limit_bytes = MAX_OUTPUT_BYTES_PER_MAILBOX;
    rt->results[0].mailbox_id = 0u;
    rt->results[0].status = STATUS_RUNNING;
    rt->results[1].mailbox_id = 0u;
    rt->results[1].status = STATUS_COMPLETED;
    rt->results[1].value = output_value_oracle(rt->seed, 1u);
    check_assert(arena_acquire(rt, 2), "cancel provider owner acquire failed");
    provider = arena_allocate(rt, 2, 128u, 8u);
    provider_bytes = (uint8_t *)arena_resolve(rt, provider, 128u);
    check_assert(provider_bytes != NULL, "cancel provider allocation failed");
    fill_body(provider_bytes, 128u, 91u);
    check_assert(publish_output(rt, 0u, 1u, rt->results[1].value),
                 "cancel slow output allocation failed");
    check_assert(pending_begin(rt, 0, 0u, 0, (Ref){ -1, -1, 0, 0, 0, 0 },
                               92u, 0u, 5u, 77u), "cancel pending operation setup failed");
    check_assert(pending_attach_provider(rt, 0, 77u, provider, 128u, 91u),
                 "cancel provider attachment failed");
    before_provider = provider_live_bytes(rt);
    before_output = output_live_bytes(rt);
    check_assert(before_provider > 0u && before_output > 0u,
                 "cancellation test requires both provider and output owners live");
    check_assert(request_cancel(rt, 0, 77u), "cancellation request was rejected");
    check_assert(provider_live_bytes(rt) == before_provider && output_live_bytes(rt) == before_output,
                 "cancel request released a provider or output owner early");
    check_assert(!request_cancel(rt, 0, 77u), "duplicate cancellation request was accepted");
    check_assert(provider_acknowledge(rt, 0, 77u), "provider acknowledgment failed");
    check_assert(provider_live_bytes(rt) == 0u && output_live_bytes(rt) == before_output,
                 "provider acknowledgment released the wrong owners");
    check_assert(!provider_acknowledge(rt, 0, 77u), "duplicate provider completion was accepted");
    check_assert(resume_pending(rt, 0, 77u), "cancelled operation did not reach a terminal state");
    check_assert(rt->results[0].status == STATUS_CANCELLED,
                 "cancelled operation was not reported as cancelled");
    check_assert(output_live_bytes(rt) == before_output, "slow output was freed before consumer acknowledgment");
    check_assert(consumer_ack_output(rt, 1u), "slow output consumer acknowledgment failed");
    check_assert(output_live_bytes(rt) == 0u, "output acknowledgment did not release output owner");
    check_assert(!consumer_ack_output(rt, 1u), "duplicate slow output acknowledgment was accepted");
    check_assert(!provider_acknowledge(rt, 0, 77u), "late provider completion was accepted");

    rt->results[2].mailbox_id = 0u;
    rt->results[2].status = STATUS_RUNNING;
    check_assert(arena_acquire(rt, 2), "new operation provider reacquire failed");
    provider = arena_allocate(rt, 2, 64u, 8u);
    provider_bytes = (uint8_t *)arena_resolve(rt, provider, 64u);
    check_assert(provider_bytes != NULL, "new operation provider allocation failed");
    fill_body(provider_bytes, 64u, 93u);
    check_assert(pending_begin(rt, 0, 2u, 0, (Ref){ -1, -1, 0, 0, 0, 0 },
                               93u, 0u, 6u, 78u), "newer pending operation setup failed");
    check_assert(pending_attach_provider(rt, 0, 78u, provider, 64u, 93u),
                 "newer provider attachment failed");
    check_assert(!provider_acknowledge(rt, 0, 77u),
                 "late token from an earlier operation acknowledged newer provider data");
    check_assert(provider_live_bytes(rt) == 64u,
                 "late token released the newer provider buffer");
    check_assert(request_cancel(rt, 0, 78u), "newer operation cancellation failed");
    check_assert(provider_acknowledge(rt, 0, 78u), "newer provider acknowledgment failed");
    check_assert(resume_pending(rt, 0, 78u), "newer cancelled operation did not drain");
    (void)selftest_drain(rt);
}

static void selftest_cancel_after_ready(Runtime *rt) {
    Ref provider;
    uint8_t *bytes;
    selftest_init(rt, 2u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 2), "ready-cancel provider acquire failed");
    provider = arena_allocate(rt, 2, 64u, 8u);
    bytes = (uint8_t *)arena_resolve(rt, provider, 64u);
    check_assert(bytes != NULL, "ready-cancel provider allocation failed");
    fill_body(bytes, 64u, 7u);
    check_assert(pending_begin(rt, 0, 0u, 0, (Ref){ -1, -1, 0, 0, 0, 0 },
                               7u, 0u, 1u, 88u), "ready-cancel pending setup failed");
    check_assert(pending_attach_provider(rt, 0, 88u, provider, 64u, 7u),
                 "ready-cancel provider attachment failed");
    check_assert(provider_acknowledge(rt, 0, 88u), "ready-cancel provider acknowledgment failed");
    check_assert(provider_live_bytes(rt) == 0u, "ready provider buffer remained live");
    check_assert(request_cancel(rt, 0, 88u), "cancel after readiness was rejected");
    check_assert(resume_pending(rt, 0, 88u), "cancel after readiness did not reach a terminal state");
    check_assert(rt->results[0].status == STATUS_CANCELLED,
                 "cancel after readiness was not reported as cancelled");
    check_assert(!provider_acknowledge(rt, 0, 88u), "late completion after ready cancellation was accepted");
    check_assert(provider_live_bytes(rt) == 0u, "ready-cancel leaked provider storage");
    (void)selftest_drain(rt);
}

static void selftest_start_request_failure_cleanup(Runtime *rt) {
    InputPayload input;
    selftest_init(rt, CHUNK_BYTES);
    set_workload_shape(rt, "request", "cpu", 17u, 1u);
    input.id = 0u;
    input.seed = 17u;
    input.value = output_value_oracle(input.seed, input.id);
    rt->results[0].mailbox_id = 0u;
    check_assert(!start_request(rt, 0u, &input),
                 "low-budget start_request unexpectedly succeeded");
    check_assert(rt->results[0].status == STATUS_REJECTED_CAPACITY,
                 "low-budget start_request did not report capacity rejection");
    check_assert(count_active_owners(rt) == 0u && rt->arena_used_bytes == 0u &&
                 rt->mailboxes[0].pending.active == 0u &&
                 provider_live_bytes(rt) == 0u && output_live_bytes(rt) == 0u,
                 "failed start_request left pending state or dynamic owners live");
    check_assert(verify_static_state(rt),
                 "failed start_request damaged persistent mailbox state");
    (void)selftest_drain(rt);
}

static void selftest_resume_failure_cleanup(Runtime *rt) {
    InputPayload input;
    RootValue *root;
    uint64_t token = 0u;
    selftest_init(rt, WORKING_SCRATCH_BYTES + 8u * CHUNK_BYTES);
    set_workload_shape(rt, "turn", "delayed-small", 17u, 1u);
    input.id = 0u;
    input.seed = 17u;
    input.value = output_value_oracle(input.seed, input.id);
    rt->results[0].mailbox_id = 0u;
    check_assert(start_request(rt, 0u, &input),
                 "resume-failure request setup did not reach its pending state");
    check_assert(rt->mailboxes[0].pending.active != 0u,
                 "resume-failure setup did not create pending work");
    if (rt->mailboxes[0].pending.active != 0u) {
        token = rt->mailboxes[0].pending.token;
        root = (RootValue *)arena_resolve(rt, rt->mailboxes[0].pending.root,
                                          sizeof(RootValue));
        check_assert(root != NULL, "resume-failure retained root could not be resolved");
        if (root != NULL) {
            root->nominal_type ^= 0xffffffffu;
        }
        check_assert(provider_acknowledge(rt, 0u, token),
                     "resume-failure provider acknowledgment failed");
        check_assert(provider_live_bytes(rt) == 0u,
                     "resume-failure retained provider bytes after acknowledgment");
        check_assert(!resume_pending(rt, 0u, token),
                     "corrupt retained graph unexpectedly completed");
    }
    check_assert(rt->results[0].status == STATUS_FAILED,
                 "resume failure did not produce a terminal failed result");
    check_assert(rt->mailboxes[0].pending.active == 0u && count_active_owners(rt) == 0u &&
                 rt->arena_used_bytes == 0u && provider_live_bytes(rt) == 0u &&
                 output_live_bytes(rt) == 0u,
                 "failed resume left pending state or dynamic owners live");
    check_assert(verify_static_state(rt),
                 "failed resume damaged persistent mailbox static state");
    (void)selftest_drain(rt);
}

static void selftest_pool_reuse(Runtime *rt) {
    uint64_t allocations;
    Ref ref;
    selftest_init(rt, 2u * CHUNK_BYTES);
    check_assert(arena_acquire(rt, 0), "reuse first acquire failed");
    ref = arena_allocate(rt, 0, 32u, 8u);
    check_assert(ref.valid != 0u, "reuse first allocation failed");
    allocations = rt->pool.allocator_calls;
    arena_release(rt, 0);
    check_assert(current_cached_bytes(rt) == CHUNK_BYTES, "released chunk was not cached");
    check_assert(arena_acquire(rt, 1), "reuse second acquire failed");
    ref = arena_allocate(rt, 1, 32u, 8u);
    check_assert(ref.valid != 0u, "reuse second allocation failed");
    check_assert(rt->pool.allocator_calls == allocations,
                 "pool reuse made an unnecessary VirtualAlloc call");
    arena_release(rt, 1);
    check_assert(verify_static_state(rt), "pool reuse changed static mailbox state");
    (void)selftest_drain(rt);
}

static void selftest_run(Runtime *rt, JobLimit *job) {
    uint64_t private_bytes = 0;
    uint64_t working_set = 0;
    SYSTEM_INFO system_info;
    GetSystemInfo(&system_info);
    g_assertions = 0u;
    g_test_failure[0] = '\0';
    selftest_normal_completion(rt);
    selftest_strict_drain_rejects_live_owner(rt);
    selftest_nested_promotion(rt);
    selftest_output_survives_reset(rt);
    selftest_stale_handle_reuse(rt);
    selftest_partial_promotion_rollback(rt);
    selftest_capacity_exhaustion(rt);
    selftest_queue_fifo_and_backpressure(rt);
    selftest_pending_exhaustion(rt);
    selftest_output_exhaustion(rt);
    selftest_cancellation_ownership(rt);
    selftest_cancel_after_ready(rt);
    selftest_start_request_failure_cleanup(rt);
    selftest_resume_failure_cleanup(rt);
    selftest_pool_reuse(rt);
    if (!query_process_memory(&private_bytes, &working_set)) {
        (void)private_bytes;
        (void)working_set;
        copy_bounded_string(g_test_failure, sizeof(g_test_failure),
                            "GetProcessMemoryInfo failed in self-test");
    }
    if (g_test_failure[0] != '\0') {
        fprintf(stderr, "self-test failed: %s\n", g_test_failure);
        return;
    }
    printf("{\"schemaVersion\":1,\"kind\":\"self-test\",\"passed\":true,"
           "\"assertions\":%u,\"processLimitEnforced\":%s}\n",
           g_assertions, job->enforced != 0u ? "true" : "false");
}

static void print_usage(void) {
    fprintf(stderr,
            "usage: probe.exe --self-test [--process-limit-bytes N]\n"
            "   or: probe.exe --mode turn|request --scenario cpu|delayed-small|delayed-large|slow-output|cancellation|retained-only\n"
            "       --seed N --requests N --budget-bytes N --process-limit-bytes N\n");
}

int main(int argc, char **argv) {
    CliOptions options;
    JobLimit job;
    SYSTEM_INFO system_info;
    LARGE_INTEGER qpc_start;
    LARGE_INTEGER qpc_end;
    LARGE_INTEGER qpc_frequency;
    uint64_t private_bytes = 0;
    uint64_t working_set_bytes = 0;
    uint64_t elapsed_ticks = 0;
    uint32_t drained;
    uint32_t owners_at_drain;
    uint32_t static_preserved;
    int workload_ok;
    if (!parse_options(argc, argv, &options)) {
        print_usage();
        return 2;
    }
    if (!enforce_process_limit(options.process_limit_bytes, &job)) {
        fprintf(stderr, "failed to establish and verify the Windows Job Object process-memory cap\n");
        return 3;
    }
    GetSystemInfo(&system_info);
    if (system_info.dwPageSize == 0u || CHUNK_BYTES % system_info.dwPageSize != 0u) {
        fprintf(stderr, "arena chunk size is incompatible with the Windows page size\n");
        CloseHandle(job.handle);
        return 3;
    }
    if (options.self_test != 0u) {
        selftest_run(&g_runtime, &job);
        CloseHandle(job.handle);
        return g_test_failure[0] == '\0' ? 0 : 4;
    }
    initialize_runtime(&g_runtime, options.budget_bytes, system_info.dwPageSize);
    set_workload_shape(&g_runtime, options.mode, options.scenario, options.seed, options.requests);
    if (g_runtime.fixed_metadata_bytes > g_runtime.budget_bytes ||
        options.budget_bytes > g_runtime.fixed_metadata_bytes + (uint64_t)MAX_CHUNKS * CHUNK_BYTES) {
        fprintf(stderr, "arena budget must cover fixed metadata and fit the bounded chunk table\n");
        CloseHandle(job.handle);
        return 2;
    }
    if (options.process_limit_bytes < g_runtime.fixed_metadata_bytes + CHUNK_BYTES * 2u) {
        fprintf(stderr, "process-memory limit is too small for the native process and one arena turn\n");
        CloseHandle(job.handle);
        return 2;
    }
    QueryPerformanceFrequency(&qpc_frequency);
    QueryPerformanceCounter(&qpc_start);
    workload_ok = run_workload(&g_runtime);
    if (!workload_ok) {
        fprintf(stderr, "workload failed: %s\n", g_runtime.failure);
        CloseHandle(job.handle);
        return 4;
    }
    if (!verify_static_state(&g_runtime)) {
        fprintf(stderr, "mailbox static state did not survive workload turns\n");
        CloseHandle(job.handle);
        return 4;
    }
    if (!retire_empty_owners(&g_runtime)) {
        fprintf(stderr, "workload reached drain with live work, output, or arena bytes\n");
        CloseHandle(job.handle);
        return 4;
    }
    owners_at_drain = count_active_owners(&g_runtime);
    if (owners_at_drain != 0u) {
        fprintf(stderr, "workload reached pool drain with active arena owners\n");
        CloseHandle(job.handle);
        return 4;
    }
    if (!pool_drain(&g_runtime)) {
        fprintf(stderr, "workload cleanup failed: %s\n", g_runtime.failure);
        CloseHandle(job.handle);
        return 4;
    }
    drained = owners_at_drain == 0u && g_runtime.pool.reserved_bytes == 0u &&
              current_cached_bytes(&g_runtime) == 0u && g_runtime.arena_used_bytes == 0u;
    static_preserved = verify_static_state(&g_runtime) != 0;
    QueryPerformanceCounter(&qpc_end);
    if (qpc_end.QuadPart >= qpc_start.QuadPart) {
        elapsed_ticks = (uint64_t)(qpc_end.QuadPart - qpc_start.QuadPart);
    }
    if (!query_process_memory(&private_bytes, &working_set_bytes)) {
        fprintf(stderr, "GetProcessMemoryInfo failed after workload\n");
        CloseHandle(job.handle);
        return 4;
    }
    print_workload_json(&g_runtime, &options, &job, elapsed_ticks,
                        (uint64_t)qpc_frequency.QuadPart, private_bytes,
                        working_set_bytes, drained, owners_at_drain, static_preserved);
    CloseHandle(job.handle);
    return 0;
}

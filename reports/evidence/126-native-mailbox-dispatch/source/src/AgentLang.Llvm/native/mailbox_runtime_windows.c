#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <limits.h>
#include <stdint.h>

static volatile LONG64 al_mailbox_instance_counter = 0;

uint32_t al_mailbox_platform_current_thread_id(void) {
  return (uint32_t)GetCurrentThreadId();
}

int32_t al_mailbox_platform_next_instance_id(uint64_t *out_instance_id) {
  LONG64 observed;
  LONG64 desired;
  LONG64 prior;

  if (out_instance_id == NULL ||
      (((uintptr_t)out_instance_id & (uintptr_t)7u) != 0u)) {
    return 0;
  }
  for (;;) {
    observed = InterlockedCompareExchange64(&al_mailbox_instance_counter, 0, 0);
    if (observed == INT64_MAX) {
      return 0;
    }
    desired = observed + 1;
    prior = InterlockedCompareExchange64(&al_mailbox_instance_counter, desired,
                                         observed);
    if (prior == observed) {
      *out_instance_id = (uint64_t)desired;
      return 1;
    }
  }
}

#ifdef AL_MAILBOX_RUNTIME_TESTING
int32_t al_mailbox_platform_test_exhaust_instance_counter(void) {
  (void)InterlockedExchange64(&al_mailbox_instance_counter, INT64_MAX);
  return 1;
}
#endif

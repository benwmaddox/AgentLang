#!/bin/bash
# Builds and runs the fragment-cache stress test (src/AgentLang.Llvm/native/
# http_host/flow_http_cache_test.c) under ThreadSanitizer, AddressSanitizer and
# -O2 with clang in WSL/Linux.
#   test-flow-http-cache-linux.sh <http_host dir (Linux path)> <out dir>
set -euo pipefail
SRC="$1"; OUT="$2"
mkdir -p "$OUT"
for mode in tsan asan o2; do
  case $mode in
    tsan) FLAGS="-O1 -g -fsanitize=thread" ;;
    asan) FLAGS="-O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer" ;;
    o2) FLAGS="-O2" ;;
  esac
  clang -std=gnu11 -Wall -Wextra $FLAGS "$SRC/flow_http_cache.c" "$SRC/flow_http_cache_test.c" -lpthread -o "$OUT/cache_test_$mode"
  # Sanitizer reports abort the run (nonzero exit) instead of scrolling past.
  rc=0
  TSAN_OPTIONS=halt_on_error=1 ASAN_OPTIONS=halt_on_error=1 UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1 \
    "$OUT/cache_test_$mode" > "$OUT/cache_test_$mode.log" 2>&1 || rc=$?
  reports=$(grep -cE 'WARNING: ThreadSanitizer|ERROR: AddressSanitizer|runtime error:' "$OUT/cache_test_$mode.log" || true)
  echo "[$mode] exit=$rc sanitizer-reports=$reports $(tail -n 1 "$OUT/cache_test_$mode.log")"
  if [ "$rc" -ne 0 ] || [ "$reports" -ne 0 ]; then tail -n 40 "$OUT/cache_test_$mode.log"; exit 1; fi
done
echo "CACHE STRESS: all modes clean"

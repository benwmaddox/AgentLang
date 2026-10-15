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
  echo "[$mode] $("$OUT/cache_test_$mode" 2>&1 | tail -n 3)"
done

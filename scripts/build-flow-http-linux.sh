#!/bin/bash
# Linux half of Build-FlowHttpServer.ps1: compiles a staged Flow HTTP server
# (module.ll emitted on Windows for linux-x64, the owning runtime and the
# portable host C) with clang/lld and links the system libsqlite3.so.0.
#   build-flow-http-linux.sh <staged dir (WSL path)> <output dir> <trusted:0|1> <O0|O2> [profile:0|1]
# profile=1 keeps frame pointers everywhere (generated words included) and adds
# debug info to the C, for `perf record -g`; symbols.tsv maps agentlang_word_NNNN.
set -euo pipefail
STAGE="$1"; OUT="$2"; TRUSTED="${3:-1}"; OPT="-${4:-O2}"; PROF="${5:-0}"
mkdir -p "$OUT"
rm -rf "$OUT/src"; cp -r "$STAGE/src" "$OUT/src"; cp "$STAGE/module.ll" "$OUT/module.ll"
[ -f "$STAGE/symbols.tsv" ] && cp "$STAGE/symbols.tsv" "$OUT/symbols.tsv"
cd "$OUT"
if [ "$PROF" = 1 ]; then
  OPT="$OPT -g -fno-omit-frame-pointer -mno-omit-leaf-frame-pointer"
  sed -i -E 's/^(define .*\)) \{$/\1 #900 {/' module.ll
  printf '\nattributes #900 = { "frame-pointer"="all" }\n' >> module.ll
fi
DEFS="-DAL_OWNING_TRUSTED_GENERATED=$TRUSTED"
clang --target=x86_64-pc-linux-gnu -Wno-override-module $OPT -c -x ir module.ll -o module.o
clang --target=x86_64-pc-linux-gnu $OPT -std=c11 -ffreestanding -fno-builtin -Wall -Wextra $DEFS -Isrc -c src/owning_stack_runtime.c -o runtime.o
clang --target=x86_64-pc-linux-gnu $OPT -std=gnu11 -Wall -Wextra -Wno-unused-parameter $DEFS -Isrc -c src/flow_http_db.c -o db.o
clang --target=x86_64-pc-linux-gnu $OPT -std=gnu11 -Wall -Wextra -Wno-unused-parameter $DEFS -Isrc -c src/flow_http_cache.c -o cache.o
clang --target=x86_64-pc-linux-gnu $OPT -std=gnu11 -Wall -Wextra -Wno-unused-parameter $DEFS -Isrc -c src/flow_http_host.c -o host.o
# The schema the app's SQL was checked against (emit --schema); the host refuses another.
clang --target=x86_64-pc-linux-gnu $OPT -std=c11 -c src/flow_app_schema.c -o schema.o
SQLITE=$(ls /usr/lib/x86_64-linux-gnu/libsqlite3.so.0 2>/dev/null || ls /lib/x86_64-linux-gnu/libsqlite3.so.0)
# The OS crypto table resolves libcrypto symbols weakly: keep the library even when
# --as-needed would drop it (Ubuntu default), or every crypto primitive reports failure.
clang -fuse-ld=lld $OPT host.o db.o cache.o schema.o module.o runtime.o "$SQLITE" -lpthread -Wl,--no-as-needed -lcrypto -Wl,--as-needed -o flow_http_server
echo "built $OUT/flow_http_server"

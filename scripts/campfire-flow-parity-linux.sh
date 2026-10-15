#!/bin/bash
# Linux half of Test-CampfireFlowServerParity.ps1 -LinuxServer: runs the Campfire
# Flow server in WSL on 127.0.0.1:<port> against a fresh ext4 copy of the snapshot
# database and records the parity cases (status, headers, body) with curl, then
# a 2 s harness loadgen --validate run. Secrets stay in files (mode 600) and are
# never printed.
#   campfire-flow-parity-linux.sh <server> <cases.tsv> <out dir> <snapshot db> <reference.env> <cookie file> <loadgen> <contract> <port> <run name> [cache mb]
set -euo pipefail
SERVER=$1; CASES=$2; OUT=$3; SNAPSHOT=$4; REFENV=$5; COOKIEFILE=$6; LOADGEN=$7; CONTRACT=$8; PORT=$9; RUN=${10}; STORE_MB=${11:-32}
PRIV=$HOME/linuxbench/private/cfs-parity-$RUN
test ! -e "$PRIV" || { echo "run exists"; exit 2; }
mkdir -p -m 700 "$PRIV"; mkdir -p "$OUT"
cp "$SNAPSHOT" "$PRIV/production.sqlite3"
umask 077
{ printf 'secret_key_base='; grep '^SECRET_KEY_BASE=' "$REFENV" | head -1 | cut -d= -f2- | tr -d '\r\n'; printf '\nauthority=127.0.0.1:%s\n' "$PORT"; } > "$PRIV/config.txt"
COOKIE=$(tr -d '\r\n' < "$COOKIEFILE")
TOKEN_ONLY=$(printf '%s' "$COOKIE" | tr ';' '\n' | sed 's/^ *//' | grep '^session_token=' || true)
BASE=http://127.0.0.1:$PORT
"$SERVER" --port "$PORT" --db "$PRIV/production.sqlite3" --config "$PRIV/config.txt" --workers 4 --allow-control --cache-mb 0 --store-mb "$STORE_MB" > "$OUT/server.log" 2> "$OUT/server.stderr.log" &
PID=$!
for i in $(seq 1 300); do grep -q '^READY ' "$OUT/server.log" 2>/dev/null && break; sleep 0.1; done
grep -q '^READY ' "$OUT/server.log" || { echo "server not READY"; kill $PID; exit 3; }
while IFS=$'\t' read -r name method path mode agent; do
  [ -z "$name" ] && continue
  args=(-s -S -D "$OUT/$name.headers.txt" -o "$OUT/$name.body.html" -A "$agent")
  if [ "$method" = HEAD ]; then args+=(-I); else args+=(-X "$method"); fi
  [ "$method" = POST ] && args+=(-d '')
  case "$mode" in
    True) args+=(-H "Cookie: $COOKIE");;
    last) args+=(-H "Cookie: $COOKIE; last_room=${path##*/}");;
    token) args+=(-H "Cookie: $TOKEN_ONLY");;
    cross) args+=(-H "Cookie: $COOKIE" -H 'Sec-Fetch-Site: cross-site' -H 'Origin: https://evil.example');;
  esac
  curl "${args[@]}" "$BASE$path" > /dev/null
done < "$CASES"
ROOMPATH=$(awk -F'\t' '$1=="room-steady"{print $3}' "$CASES")
"$LOADGEN" fetch --base "$BASE" --cookie "$COOKIE" --path "$ROOMPATH" --out "$OUT/loadgen-body.html" > /dev/null
"$LOADGEN" http --base "$BASE" --cookie "$COOKIE" --path "$ROOMPATH" --validate "$CONTRACT" --conc 4 --duration 2 --gzip 0 > "$OUT/loadgen.json"
curl -s -X POST "$BASE/__flow_http/shutdown" > /dev/null || true
wait $PID || true
unset COOKIE TOKEN_ONLY
rm -f "$PRIV/config.txt"
echo "linux run done"

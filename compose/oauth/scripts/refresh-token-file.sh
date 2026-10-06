#!/usr/bin/env bash
# Keeps tokens/sink.token fresh (writes a new token every INTERVAL seconds, default 60), as an external
# token agent would. Writes to a temp file and renames, so Replicator never reads a half-written file.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p tokens
interval="${1:-60}"
while true; do
  token="$(./scripts/get-token.sh)"
  printf '%s' "$token" > tokens/sink.token.tmp && mv tokens/sink.token.tmp tokens/sink.token
  echo "$(date '+%H:%M:%S') wrote tokens/sink.token"
  sleep "$interval"
done

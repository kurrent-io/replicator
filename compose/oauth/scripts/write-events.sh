#!/usr/bin/env bash
# Appends events to a stream over HTTP. Usage: write-events.sh <basic|oauth|oauth2> <stream> <count> [delay-seconds]
set -euo pipefail
cd "$(dirname "$0")/.."
node="$1"; stream="$2"; count="$3"; delay="${4:-0}"
case "$node" in
  basic)  port=2113; auth=(-u admin:changeit) ;;
  oauth)  port=2114; auth=(-H "Authorization: Bearer $(./scripts/get-token.sh)") ;;
  oauth2) port=2115; auth=(-H "Authorization: Bearer $(./scripts/get-token.sh)") ;;
  *) echo "unknown node $node"; exit 1 ;;
esac
for i in $(seq 1 "$count"); do
  id="$(uuidgen | tr 'A-Z' 'a-z')"
  curl -fsS --cacert certs/ca/ca.crt "${auth[@]}" \
    -H "Content-Type: application/vnd.eventstore.events+json" \
    -d "[{\"eventId\":\"$id\",\"eventType\":\"TestEvent\",\"data\":{\"n\":$i}}]" \
    "https://localhost:$port/streams/$stream" >/dev/null
  # Re-fetch the token periodically for long runs (tokens live 5 minutes)
  if [ "$node" != basic ] && [ $((i % 100)) -eq 0 ]; then auth=(-H "Authorization: Bearer $(./scripts/get-token.sh)"); fi
  [ "$delay" != 0 ] && sleep "$delay"
done
echo "wrote $count events to $stream on $node"

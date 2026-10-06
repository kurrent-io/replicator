#!/usr/bin/env bash
# Prints the last event number of a stream (or "none"). Usage: stream-head.sh <basic|oauth|oauth2> <stream>
set -euo pipefail
cd "$(dirname "$0")/.."
node="$1"; stream="$2"
case "$node" in
  basic)  port=2113; auth=(-u admin:changeit) ;;
  oauth)  port=2114; auth=(-H "Authorization: Bearer $(./scripts/get-token.sh)") ;;
  oauth2) port=2115; auth=(-H "Authorization: Bearer $(./scripts/get-token.sh)") ;;
esac
code=$(curl -sS -o /tmp/stream-head.$$ -w '%{http_code}' --cacert certs/ca/ca.crt "${auth[@]}" \
  -H "Accept: application/vnd.eventstore.atom+json" "https://localhost:$port/streams/$stream/head/backward/1?embed=body")
if [ "$code" = 404 ]; then echo none; else jq -r '.entries[0].eventNumber // .entries[0].positionEventNumber' /tmp/stream-head.$$; fi
rm -f /tmp/stream-head.$$

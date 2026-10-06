#!/usr/bin/env bash
# Prints an access token for the "replicator" client (client credentials, role $admins).
set -euo pipefail
cd "$(dirname "$0")/.."
curl -fsS http://localhost:5080/connect/token \
  -d grant_type=client_credentials -d client_id=replicator \
  --data-urlencode "client_secret=$(cat secrets/replicator-client-secret)" -d scope=streams \
  | jq -r .access_token

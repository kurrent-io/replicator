#!/usr/bin/env bash
# Generates a test CA and one node certificate shared by all KurrentDB nodes (SANs cover every hostname used).
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -f certs/node/node.crt ]; then echo "certs/ already exists; delete it to regenerate"; exit 0; fi
mkdir -p certs
docker run --rm -v "$PWD/certs:/certs" alpine:3 sh -euc '
  apk add -q --no-progress openssl
  cd /certs && mkdir -p ca node
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=Replicator OAuth E2E CA" -keyout ca/ca.key -out ca/ca.crt 2>/dev/null
  openssl req -newkey rsa:2048 -nodes -subj "/CN=kurrentdb-node" -keyout node/node.key -out node/node.csr 2>/dev/null
  printf "subjectAltName=DNS:localhost,DNS:kurrentdb-node,DNS:kurrentdb-basic,DNS:kurrentdb-oauth,DNS:kurrentdb-oauth2,IP:127.0.0.1\nextendedKeyUsage=serverAuth,clientAuth\nkeyUsage=digitalSignature,keyEncipherment\n" > node/ext.cnf
  openssl x509 -req -in node/node.csr -CA ca/ca.crt -CAkey ca/ca.key -CAcreateserial -days 365 -extfile node/ext.cnf -out node/node.crt 2>/dev/null
  rm node/node.csr node/ext.cnf
  chmod 644 ca/ca.crt node/node.crt node/node.key
'
echo "certs written to $(pwd)/certs"

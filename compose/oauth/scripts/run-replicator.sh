#!/usr/bin/env bash
# Builds Replicator from this branch (Release; Debug runs yarn) and runs it with a scenario's config.
# Usage: run-replicator.sh <scenario-folder-name>   e.g. 1-basic-to-oauth
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$here/../.." && pwd)"
scenario="$here/scenarios/$1"
[ -d "$scenario" ] || { echo "no scenario $1"; exit 1; }
dotnet build "$repo/src/replicator" -c Release -p:UseAppHost=false -v q -nologo
cd "$scenario"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5005}"
export DOTNET_ROLL_FORWARD=Major
exec dotnet "$repo/src/replicator/bin/Release/net9.0/replicator.dll"

#!/bin/sh
# Runs the load test from scratch and prints the report. Every setting has a default; override any of them:
#
#   samples/LoadTest/run.sh
#   WORKERS=8 OWNERS=2000 samples/LoadTest/run.sh
#
# Needs Docker with Compose v2. Takes a few minutes; the stack is removed at the end.
set -e
cd "$(dirname "$0")"
export WORKERS="${WORKERS:-4}"

docker compose --profile report down -v --remove-orphans >/dev/null 2>&1 || true
docker compose build --quiet
docker compose up -d --wait postgres
docker compose up -d api
docker compose up -d --scale worker="$WORKERS" worker
status=0
docker compose --profile report run --rm report || status=$?
docker compose --profile report down -v --remove-orphans >/dev/null 2>&1
exit $status

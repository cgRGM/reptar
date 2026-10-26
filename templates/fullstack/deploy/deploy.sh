#!/usr/bin/env bash
# One-command VPS deployment for a scaffolded Reptar app.
# Run from <project>/deploy on any Docker-enabled host with the framework
# clone present (build context is the repo root).
set -euo pipefail

echo "=================================================="
echo " Reptar VPS Production Deployment (Native AOT)"
echo "=================================================="

command -v docker >/dev/null 2>&1 || { echo >&2 "Error: docker is required."; exit 1; }
docker compose version >/dev/null 2>&1 || { echo >&2 "Error: docker compose is required."; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> Building the Native AOT container..."
docker compose build reptar

echo "==> Starting database and application services..."
docker compose up -d

echo "==> Waiting for Reptar to pass health check..."
ATTEMPTS=0
MAX_ATTEMPTS=30
HEALTH_URL="http://127.0.0.1:8787/health"

until curl -s -f "$HEALTH_URL" >/dev/null 2>&1 || [ $ATTEMPTS -ge $MAX_ATTEMPTS ]; do
  sleep 1
  ATTEMPTS=$((ATTEMPTS + 1))
done

if [ $ATTEMPTS -ge $MAX_ATTEMPTS ]; then
  echo "Error: health check failed after ${MAX_ATTEMPTS}s. Check: docker compose logs reptar"
  exit 1
fi

echo "==> Deployed. Health check green:"
curl -s "$HEALTH_URL" && echo
echo "==> Your app is live on port 8787."

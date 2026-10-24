#!/usr/bin/env bash
set -euo pipefail

echo "=================================================="
echo " Reptar VPS Production Deployment (Native AOT)"
echo "=================================================="

# Check requirements
command -v docker >/dev/null 2>&1 || { echo >&2 "Error: docker is required."; exit 1; }
docker compose version >/dev/null 2>&1 || { echo >&2 "Error: docker compose is required."; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> Building Native AOT Linux container..."
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

if [ $ATTEMPTS -ge $MAX_ATTEMPTS ]; do
  echo >&2 "Error: Reptar failed to start within 30 seconds."
  docker compose logs reptar
  exit 1
fi

echo "==> Reptar Native AOT is live!"
curl -s "$HEALTH_URL"
echo ""
echo "Deployment successful: http://localhost:8787"

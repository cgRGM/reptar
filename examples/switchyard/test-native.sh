#!/usr/bin/env bash
# Published-artifact gate. All HTTP tests target a disposable native container.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
image="${SWITCHYARD_TEST_IMAGE:-reptar-switchyard:verify}"
name="switchyard-verify-$$"
data="$(mktemp -d)"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$data"
}
trap cleanup EXIT

cd "$root"
docker build -f deploy/Dockerfile --build-arg APP_DIR=examples/switchyard -t "$image" .
docker run --rm --user "$(id -u):$(id -g)" \
  -v "$data:/data" -e SWITCHYARD_DB=/data/probe.db "$image" --self-test

docker run -d --name "$name" --user "$(id -u):$(id -g)" \
  -p 127.0.0.1::8795 -e PORT=8795 -e SWITCHYARD_INSECURE_LOCAL=1 \
  -e SWITCHYARD_DB=/data/app.db -v "$data:/data" "$image" >/dev/null
binding="$(docker port "$name" 8795/tcp)"
export SWITCHYARD_URL="http://127.0.0.1:${binding##*:}"
ready=0
for attempt in {1..40}; do
  if curl --fail --silent "$SWITCHYARD_URL/health" >/dev/null; then
    ready=1
    break
  fi
  sleep 0.25
done
if [ "$ready" != 1 ]; then
  docker logs "$name" >&2
  exit 1
fi
cd examples/switchyard
bun install --frozen-lockfile --ignore-scripts
bun run check-types
bun test acceptance.test.ts

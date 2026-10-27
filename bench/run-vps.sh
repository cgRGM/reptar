#!/usr/bin/env bash
# Run the benchmark suite on the VPS.
#
# Assumes:
#   - bench/build.sh has been run (binaries in bench/out)
#   - ~/bin/oha exists (or OHA_BIN points at it)
#   - the DB has been seeded (this script seeds it)
#
# The server is pinned to CPU 0 and the load generator to CPU 1. On the
# Lightsail box those are SMT siblings, so this is a *relative* comparison,
# not an absolute capacity measurement.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OHA="${OHA_BIN:-$HOME/bin/oha}"
DURATION="${DURATION:-10}"
WARMUP="${WARMUP:-3}"
REPS="${REPS:-3}"
CONNECTIONS="${CONNECTIONS:-100}"
IMPLS="${IMPLS:-reptar minimal-api hono-jit hono-compiled}"
ONLY="${ONLY:-}"
STAMP="${STAMP:-$(date -u +%Y%m%dT%H%M%SZ)}"
RESULTS="$ROOT/bench/results/vps-$STAMP"

if [[ ! -x "$OHA" ]]; then
  echo "oha not found at $OHA (set OHA_BIN)" >&2
  exit 1
fi

mkdir -p "$RESULTS"

# Shared VPSes may host unrelated apps or other agents' experiments. Never
# stop containers or kill processes implicitly. Operators can explicitly opt
# into quiescing named containers; restore only ones that were running.
containers_to_restart=()
restart_containers() {
  if [[ -n "${SERVER_PID:-}" ]]; then
    kill "$SERVER_PID" >/dev/null 2>&1 || true
    wait "$SERVER_PID" >/dev/null 2>&1 || true
  fi
  if [[ ${#containers_to_restart[@]} -gt 0 ]]; then
    docker start "${containers_to_restart[@]}" >/dev/null 2>&1 || true
  fi
}
trap restart_containers EXIT
for container in ${BENCH_STOP_CONTAINERS:-}; do
  if [[ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null)" == "true" ]]; then
    containers_to_restart+=("$container")
    docker stop "$container" >/dev/null
  fi
done

echo "==> Seeding the shared database"
bun "$ROOT/bench/shared/seed.ts" "$ROOT/bench/out/bench.db" >/dev/null

cat > "$RESULTS/manifest.json" <<EOF
{
  "stamp": "$STAMP",
  "hostname": "$(hostname)",
  "kernel": "$(uname -srm)",
  "cpu": "$(lscpu | grep -E 'Model name|^CPU\(s\)|Thread|Core|Socket' | tr '\n' '|')",
  "memory": "$(free -h | head -2 | tail -1)",
  "bun": "$(bun --version)",
  "dotnet": "$(docker run --rm mcr.microsoft.com/dotnet/sdk:10.0 dotnet --version 2>/dev/null | tail -1)",
  "oha": "$($OHA --version)",
  "gitCommit": "${BENCH_SOURCE_REVISION:-$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)}",
  "builderImageId": "$(docker image inspect --format '{{.Id}}' "${BUILDER_IMAGE:-reptar-bench-builder}" 2>/dev/null || echo unknown)",
  "binarySizes": {
    "reptar": $(stat -c%s "$ROOT/bench/out/reptar/reptar-bench" 2>/dev/null || echo 0),
    "minimalApi": $(stat -c%s "$ROOT/bench/out/minimal-api/minimal-api-bench" 2>/dev/null || echo 0),
    "hono": $(stat -c%s "$ROOT/bench/out/hono" 2>/dev/null || echo 0)
  },
  "config": {
    "connections": $CONNECTIONS,
    "duration": $DURATION,
    "warmup": $WARMUP,
    "reps": $REPS,
    "only": "$ONLY"
  }
}
EOF

start_server() {
  local label="$1"
  local port="$2"
  local db="$3"
  shift 3

  if curl -s --max-time 1 "http://127.0.0.1:$port/__bench/plaintext" >/dev/null 2>&1; then
    echo "Port $port is occupied; refusing to kill another process." >&2
    return 1
  fi
  rm -f "$db" "$db-wal" "$db-shm"
  cp "$ROOT/bench/out/bench.db" "$db"

  SERVER_START_NS=$(date +%s%N)
  pushd "$ROOT" >/dev/null
  taskset -c 0 env \
    BENCH_DB="$db" \
    PORT="$port" \
    HOST=127.0.0.1 \
    Logging__LogLevel__Default=Warning \
    ASPNETCORE_ENVIRONMENT=Production \
    "$@" >"$RESULTS/$label.server.log" 2>&1 &
  SERVER_PID=$!
  popd >/dev/null
}

wait_ready() {
  local port="$1"
  for _ in $(seq 1 200); do
    if curl -sf "http://127.0.0.1:$port/__bench/plaintext" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.1
  done
  return 1
}

run_impl() {
  local label="$1"
  local port="$2"
  shift 2

  echo
  echo "================================================================"
  echo "  $label (port $port)"
  echo "================================================================"

  start_server "$label" "$port" "$ROOT/bench/out/$label.db" "$@"

  if ! wait_ready "$port"; then
    echo "Server $label did not become ready" >&2
    tail -50 "$RESULTS/$label.server.log" >&2 || true
    kill "$SERVER_PID" >/dev/null 2>&1 || true
    return 1
  fi

  SERVER_READY_NS=$(date +%s%N)
  STARTUP_MS=$(( (SERVER_READY_NS - SERVER_START_NS) / 1000000 ))
  printf '{"label":"%s","startupMs":%s}\n' "$label" "$STARTUP_MS" > "$RESULTS/$label.startup.json"

  sleep 1

  local only_args=()
  if [[ -n "$ONLY" ]]; then
    only_args=(--only "$ONLY")
  fi

  taskset -c 1 bun "$ROOT/bench/load/run.ts" \
    --base-url "http://127.0.0.1:$port" \
    --label "$label" \
    --pid "$SERVER_PID" \
    --connections "$CONNECTIONS" \
    --duration "$DURATION" \
    --warmup "$WARMUP" \
    --reps "$REPS" \
    --oha "$OHA" \
    "${only_args[@]}" \
    --out "$RESULTS" \
    2>&1 | tee "$RESULTS/$label.run.log"

  if [[ "${MIXED:-0}" == "1" ]]; then
    taskset -c 1 bun "$ROOT/bench/load/mixed.ts" \
      --base-url "http://127.0.0.1:$port" \
      --label "$label" \
      --duration "${MIXED_DURATION:-15}" \
      --warmup "$WARMUP" \
      --concurrency "$CONNECTIONS" \
      --out "$RESULTS" \
      2>&1 | tee "$RESULTS/$label.mixed.log"
  fi

  kill "$SERVER_PID" >/dev/null 2>&1 || true
  wait "$SERVER_PID" >/dev/null 2>&1 || true
  SERVER_PID=""
  sleep 1
}

for impl in $IMPLS; do
  case "$impl" in
    reptar)
      run_impl "reptar" 9001 "$ROOT/bench/out/reptar/reptar-bench"
      ;;
    minimal-api)
      run_impl "minimal-api" 9002 "$ROOT/bench/out/minimal-api/minimal-api-bench"
      ;;
    hono-jit)
      run_impl "hono-jit" 9003 bun run "$ROOT/bench/hono/src/index.ts"
      ;;
    hono-compiled)
      run_impl "hono-compiled" 9004 "$ROOT/bench/out/hono"
      ;;
    *)
      echo "Unknown impl: $impl" >&2
      exit 1
      ;;
  esac
done

echo
echo "==> Results: $RESULTS"
ls -lh "$RESULTS"

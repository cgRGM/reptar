#!/usr/bin/env bash
# Build the three benchmark implementations on Linux.
# Run this on the VPS (or any Linux host with Docker + Bun).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/bench/out"
BUILDER_IMAGE="${BUILDER_IMAGE:-reptar-bench-builder}"

mkdir -p "$OUT"

echo "==> Building Native AOT builder image"
docker build -t "$BUILDER_IMAGE" -f "$ROOT/bench/builder.Dockerfile" "$ROOT"

echo "==> Publishing Reptar (Native AOT)"
docker run --rm \
  -v "$ROOT:/src" \
  -w /src/bench/reptar \
  "$BUILDER_IMAGE" \
  dotnet publish -c Release -r linux-x64 --self-contained \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:ReptarSqliteOnly=true \
    -o /src/bench/out/reptar

echo "==> Publishing .NET Minimal API (Native AOT)"
docker run --rm \
  -v "$ROOT:/src" \
  -w /src/bench/minimal-api \
  "$BUILDER_IMAGE" \
  dotnet publish -c Release -r linux-x64 --self-contained \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o /src/bench/out/minimal-api

echo "==> Installing Hono dependencies"
cd "$ROOT/bench/hono"
bun install --frozen-lockfile

echo "==> Compiling Hono standalone bundle"
bun build src/index.ts --compile --outfile "$OUT/hono"

echo "==> Sizes"
ls -lh "$OUT/reptar/reptar-bench" "$OUT/minimal-api/minimal-api-bench" "$OUT/hono" 2>/dev/null || true

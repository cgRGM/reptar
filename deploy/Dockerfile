# Multi-stage Dockerfile for Reptar Native AOT on Linux
# Produces a minimal distroless container with the runtime-deps base image.

ARG APP_DIR=examples/blog

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_DIR
WORKDIR /src

# Install native AOT compilation dependencies (clang, zlib)
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Copy framework source and target application, preserving the repo-relative
# paths used by file-based app #:project directives.
COPY framework/ /src/framework/
COPY ${APP_DIR}/ /src/${APP_DIR}/

WORKDIR /src/${APP_DIR}

# Publish Native AOT self-contained binary for Linux x64
RUN dotnet publish app.cs \
    -r linux-x64 \
    -c Release \
    --self-contained \
    -p:PublishAot=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o /app/publish && \
    (mv /app/publish/app /app/publish/reptar || true) && \
    rm -f /app/publish/*.dbg

# Runtime stage: Ubuntu 24.04 Chiseled distroless (matches SDK glibc, minimal attack surface, no shell, no package manager)
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled
WORKDIR /app

# Copy the standalone compiled binary and all native dependencies (such as libe_sqlite3.so)
COPY --chown=1654:1654 --from=build /app/publish/ /app/

# Default environment
ENV PORT=8787
EXPOSE 8787

USER 1654:1654
ENTRYPOINT ["/app/reptar"]

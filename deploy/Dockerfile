# Multi-stage Dockerfile for Reptar Native AOT on Linux
# Produces an ultra-lightweight distroless container (< 25MB) booting in < 5ms.

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
    -o /app/publish

# Runtime stage: Google Distroless static Debian 12 (minimal attack surface, no shell, no package manager)
FROM gcr.io/distroless/cc-debian12:nonroot
WORKDIR /app

# Copy the standalone compiled binary
COPY --from=build /app/publish/app /app/reptar

# Default environment
ENV PORT=8787
EXPOSE 8787

USER nonroot:nonroot
ENTRYPOINT ["/app/reptar"]

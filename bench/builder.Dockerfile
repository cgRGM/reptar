FROM mcr.microsoft.com/dotnet/sdk:10.0

# Native AOT needs clang and zlib. Install them once so repeated benchmark
# builds don't pay the apt-get cost every time.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

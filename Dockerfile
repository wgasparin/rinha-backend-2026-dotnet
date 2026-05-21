# syntax=docker/dockerfile:1.7

# ────────────────────────────────────────────────────────────────────
# Stage 1: pre-process the 3M-vector dataset into references_v1.bin
#
# Runs on the build host (no CPU/RAM limit). The output is baked into
# the final image as a read-only layer; both API replicas share the
# same physical pages via the kernel page cache at runtime, so we pay
# ~45 MB once instead of per-replica.
# ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS loader

WORKDIR /build

# Restore as a cacheable layer (invalidated only when loader.csproj changes)
COPY loader/loader.csproj ./loader/
RUN dotnet restore ./loader/loader.csproj

# Sources + the gzipped dataset shipped with the rinha repo
COPY loader/ ./loader/
COPY resources/references.json.gz ./dataset/

# Produces /out/references_v1.bin in the RVF2 (IVF) format:
#   header + K=1024 centroides + bucket offsets + sorted vectors + labels
RUN dotnet run -c Release --no-restore --project ./loader/loader.csproj -- \
        --input  ./dataset/references.json.gz \
        --output /out/references_v1.bin

# ────────────────────────────────────────────────────────────────────
# Stage 2: publish the API as a Native AOT single-file binary
#
# Native AOT needs clang + zlib at build time; the resulting binary
# is statically linked except for libc/libstdc++ which come from the
# runtime base image.
# ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY src/src.csproj ./src/
RUN dotnet restore ./src/src.csproj -r linux-x64

COPY src/ ./src/
RUN dotnet publish ./src/src.csproj \
        -c Release \
        -r linux-x64 \
        --no-restore \
        -o /app

# ────────────────────────────────────────────────────────────────────
# Stage 3: distroless runtime
#
# Chiseled = Ubuntu 24.04 (noble) minus everything that isn't libc/libstdc++/
# libssl/zlib. No shell, no package manager, no /tmp writeable by default,
# runs as non-root UID 1654 ($APP_UID). Base layer ~30 MB.
# Only this stage is subject to the 1 CPU / 350 MB envelope at runtime.
# ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime

ARG APP_UID=1654

WORKDIR /app

# AOT executable (~10 MB stripped). 755 perms preserved from publish stage,
# readable+executable by the non-root user via "others" bits.
COPY --chown=$APP_UID:$APP_UID --from=publish /app/api               /app/api

# Pre-processed IVF index (~45 MB), read-only layer shared between replicas.
COPY --chown=$APP_UID:$APP_UID --from=loader  /out/references_v1.bin /data/references_v1.bin

ENV VECTORS_PATH=/data/references_v1.bin \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1 \
    DOTNET_GCConserveMemory=9

EXPOSE 8080

ENTRYPOINT ["/app/api"]

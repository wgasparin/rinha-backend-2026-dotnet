# syntax=docker/dockerfile:1.7
#
# Standalone image for running the loader independently (useful for local
# iteration). The production path is to invoke this project as a build stage
# in the root Dockerfile and COPY --from=loader the resulting .bin into the
# API image. See build-time-preprocessing.md for that pattern.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY loader/loader.csproj ./loader/
RUN dotnet restore ./loader/loader.csproj

COPY loader/ ./loader/
RUN dotnet publish ./loader/loader.csproj \
        -c Release \
        --no-restore \
        -o /publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /publish ./

ENV REFERENCES_PATH=/data/references.json.gz \
    OUTPUT_PATH=/data/references_v1.bin

ENTRYPOINT ["dotnet", "loader.dll"]

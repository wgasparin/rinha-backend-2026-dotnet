# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

This repo is a submission for **Rinha de Backend 2026** (theme: fraud detection via vector search). The design is fully specified in `HLD.md` and `FDD.md` at the repo root, but **the fraud detection feature is not yet implemented**. `src/` currently contains the default `dotnet new webapiaot` scaffold with sample `/todos` endpoints; this scaffolding will be replaced by the `POST /fraud-score` and `GET /ready` endpoints described in the design docs.

When asked to implement features, treat `HLD.md` and `FDD.md` as authoritative. The challenge brief itself lives in `docs-rinha/rinha-de-backend-2026/docs/br/` (`README.md`, `API.md`, `ARQUITETURA.md`, `REGRAS_DE_DETECCAO.md`, `DATASET.md`, `AVALIACAO.md`).

## Hard constraints (do not violate)

These come from the challenge rules and the architectural decisions in `HLD.md`. They drive most code choices.

- **1 CPU and 350 MB RAM total** across all services in `docker-compose.yml`. Planned split: Qdrant ~200 MB, two API instances ~50–60 MB each, HAProxy ~20 MB.
- **p99 ≤ 1 ms** for `POST /fraud-score`. This dominates every implementation decision.
- **Native AOT is mandatory** (`<PublishAot>true</PublishAot>` is set). Any new code or dependency must be AOT-compatible — no reflection-based serialization, no runtime code generation, no incompatible packages. New JSON types must be registered in `AppJsonSerializerContext` (source generator) in `src/Program.cs`.
- **No observability in runtime** (ADR-007 in HLD): no logging in the hot path, no metrics, no OpenTelemetry. Only fatal startup errors may write to stderr.
- **No application cache** in the hot path (ADR-008). Qdrant is the low-latency layer.
- **No retries, no circuit breaker, no fallback inference** in the request path. On Qdrant failure return `503`; never synthesize a decision.
- Topology must be **HAProxy + ≥2 API instances + Qdrant**, all on Docker `bridge` network, `linux-amd64`. The LB cannot inspect payloads.
- Listen port is **9999** (HAProxy public; APIs internal).

## Architecture (target, per HLD/FDD)

Request path: `Avaliador → HAProxy (round-robin) → API .NET Minimal API (AOT) → Qdrant (gRPC kNN k=5) → API → response`.

Bootstrap path (one-time): a Loader reads `references.json.gz` (3M labeled vectors), normalizes each into 14 float32 dims using `normalization.json` + `mcc_risk.json`, ingests in batch into Qdrant collection `references_v1`, and persists a snapshot to a Docker volume. `GET /ready` returns 2xx only after this completes.

Code is organized as **Vertical Slices** (no Hexagonal/Clean multi-project split). Each feature gets its own folder under `src/` containing its endpoint, handler, contracts, and feature-specific helpers (e.g. `FraudScore/` for the `POST /fraud-score` slice).

Decision math for `POST /fraud-score`: `fraud_score = (count of "fraud" labels among the 5 nearest neighbors) / 5`; `approved = fraud_score < 0.6`. Response schema is fixed: `{ "approved": bool, "fraud_score": float }`. `fraud_score` is always a multiple of 0.2 in normal operation.

Internal communication is **gRPC** (binary, lower overhead than REST). The Qdrant client must be verified for AOT compatibility before adoption — fallback is generating stubs from the Qdrant `.proto` with `Grpc.Tools` configured for AOT, or a minimal hand-rolled HTTP/2 client.

## Hot-path coding rules

- Use `System.Text.Json` source generation only (already wired via `AppJsonSerializerContext`). Never use reflection-based `JsonSerializer.Serialize<T>(obj)` overloads on user types.
- Reuse buffers via `ArrayPool<float>` for the 14-dim vector. Avoid LINQ, boxing, and `JsonElement` dynamic access.
- Prefer `record struct` for ephemeral DTOs to avoid heap allocation.
- gRPC channel/client to Qdrant must be a long-lived singleton (HTTP/2 multiplex).
- Do not add logging, metrics, or tracing inside request handlers.

## Common commands

All commands assume PowerShell on Windows; the working directory below is the repo root.

```powershell
# Restore + build (debug, JIT — fast iteration)
dotnet build src/src.csproj

# Run locally (development profile, listens on http://localhost:5121)
dotnet run --project src/src.csproj

# Publish Native AOT (this is what the docker image must use)
dotnet publish src/src.csproj -c Release -r linux-x64

# Run a single ad-hoc HTTP request from src/src.http (use the IDE's HTTP client,
# or curl: e.g. curl http://localhost:5121/todos/)
```

There are **no tests yet** in the repo; when adding them, prefer `xunit` and add a separate test project (e.g. `tests/`) — do not add test packages to `src/src.csproj`, since they likely break AOT publish.

There is **no `docker-compose.yml` yet**; it will need to be authored as part of bringing up the full stack. The `submission` branch (per challenge rules) is what gets evaluated, and it must contain the compose file at the repo root.

## Verification before submission

The challenge runner (`docs-rinha/rinha-de-backend-2026/run.sh`) is the authoritative local benchmark. Validate:

- `dotnet publish ... -c Release` completes with **zero AOT/trim warnings**.
- `docker stats` shows API instance memory ≤ 60 MB under load.
- p99 measured by `run.sh` ≤ 1 ms.
- 5+ sample payloads from the challenge produce the same `fraud_score` as a manual reference computation using the formulas in `REGRAS_DE_DETECCAO.md`.
- Stopping the Qdrant container makes the API return `503`, never a synthetic `200`.

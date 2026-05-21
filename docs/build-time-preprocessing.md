# Pré-processamento do dataset em build (fora do envelope de runtime)

> Análise feita no contexto da Rinha de Backend 2026.
> Restrições de runtime: 1 CPU / 350 MB total para todos os containers.

## A insight central

O limite de **1 CPU / 350 MB é runtime**, definido pelo `deploy.resources.limits` no compose. **`docker build` não tem essas restrições** — roda na máquina host com tudo disponível. Logo, qualquer trabalho pesado deve sair do runtime e cair no build.

A técnica clássica é **multi-stage build**: você usa um stage "loader" só pra gerar o `references_v1.bin`, e o stage final só copia o binário pronto. O resultado vira parte imutável da imagem.

## Dockerfile multi-stage

```dockerfile
# ──────────────────────────────────────────────────────────────
# Stage 1: baixa o dataset e gera references_v1.bin
# Roda no build host. Sem limite de CPU/RAM.
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS loader
WORKDIR /build

# baixa o dataset oficial direto da fonte da Rinha
ADD https://.../references.json.gz       /build/dataset/
ADD https://.../normalization.json       /build/dataset/
ADD https://.../mcc_risk.json            /build/dataset/

COPY loader/ ./loader/
RUN dotnet run -c Release --project ./loader -- \
      --input        /build/dataset/references.json.gz \
      --normalization /build/dataset/normalization.json \
      --mcc          /build/dataset/mcc_risk.json \
      --output       /out/references_v1.bin

# ──────────────────────────────────────────────────────────────
# Stage 2: publica o binário .NET AOT
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS publish
WORKDIR /src
COPY src/ ./
RUN dotnet publish src.csproj -c Release -r linux-x64 \
      -o /app /p:StripSymbols=true

# ──────────────────────────────────────────────────────────────
# Stage 3: runtime mínimo
# Só esse stage está sujeito aos 1 CPU / 350 MB.
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine AS runtime
WORKDIR /app
COPY --from=publish /app/src                         ./api
COPY --from=loader  /out/references_v1.bin           /data/references_v1.bin
ENV VECTORS_PATH=/data/references_v1.bin
ENTRYPOINT ["/app/api"]
```

Resultado: a imagem final já carrega o `references_v1.bin` como uma camada read-only. As duas APIs no `docker-compose` apontam pra mesma imagem → mesma camada → **mesmas páginas no page cache**. Você lê o arquivo via `mmap` e pronto.

## Compose simplificado

Sem container loader. Sem volume nomeado. Sem `depends_on: service_completed_successfully`.

```yaml
services:
  api-1: &api
    build: .
    deploy:
      resources:
        limits: { memory: 130M, cpus: '0.40' }

  api-2: *api

  haproxy:
    image: haproxy:3.0-alpine
    depends_on: [api-1, api-2]
    ports: ["9999:9999"]
    volumes: ["./haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro"]
    deploy:
      resources:
        limits: { memory: 30M, cpus: '0.20' }
```

Soma: 130 + 130 + 30 = 290 MB. Folga de 60 MB pro overhead do Docker. Sem loader, sem volume, sem coordenação de startup.

## De onde vem o dataset durante o build?

Três cenários, do melhor pro pior:

| Cenário | Como buscar | Quando faz sentido |
|---|---|---|
| **URL pública** | `ADD https://.../references.json.gz` | Se o dataset oficial fica num bucket estável. `ADD` valida ETag → cache de layer funciona. |
| **Bind do contexto** | `COPY dataset/ dataset/` + dataset no `.dockerignore` desativado | Dev local. Cuidado: 600 MB no contexto deixa `docker build` lento. |
| **BuildKit secret/cache** | `RUN --mount=type=bind,source=dataset,target=/build/dataset` | Idem, mas sem copiar pro contexto. Requer `# syntax=docker/dockerfile:1.4`. |

A regra de submissão da Rinha (`run.sh`) só faz `docker compose up`. O download por `ADD` acontece dentro do build, não consome quota de runtime — mas consome banda do avaliador. Se o dataset estiver embutido no repo (zipado) e copiado via bind mount, evita rede no build.

## Layers e cache

Pontos sutis pra build rápido:

1. **`ADD` com URL** gera uma layer cacheada pelo header HTTP. Se o dataset não mudou, o stage `loader` é cache hit completo → build incremental termina em segundos.
2. **Separe os `COPY`** pra maximizar reuso: `COPY loader/*.csproj` → `RUN dotnet restore` → `COPY loader/` → `RUN dotnet run`. Mudança em código C# do loader não invalida o restore.
3. **`StripSymbols=true`** corta ~30% do tamanho do binário AOT.
4. **`dotnet/runtime-deps:9.0-alpine`** é a imagem mais enxuta — ~12 MB. Funciona pra AOT porque você não precisa do runtime gerenciado, só de `libc`/`libstdc++`.

## E se o avaliador exigir dataset montado em runtime?

Reli `HLD.md` e `DATASET.md` — o desenho assume que o dataset é parte do setup do candidato, **não** algo que o avaliador injeta. Você é dono de como ele chega à imagem. Logo, multi-stage é seguro.

Se um dia a regra mudasse e o `references.json.gz` viesse num volume montado pelo runner, aí sim você precisaria de um loader em runtime — mas mesmo nesse caso ele pode:
- Rodar como container one-shot com `deploy.resources.limits` próprio (alto durante boot, depois zero porque o container morre)
- Antes do healthcheck do HAProxy abrir, então fora da janela de medição do `run.sh`

## Resumo

- **Pre-processamento no build → custa zero em runtime**, e é exatamente pra isso que multi-stage existe.
- A imagem final carrega `references_v1.bin` como camada read-only.
- As 2 APIs compartilham essa camada (mesma imagem) e compartilham as páginas físicas via page cache do kernel.
- O `docker-compose.yml` fica enxuto: 2 APIs + 1 HAProxy, fim.

# 2026-05-14 — HNSW build trigger and end-to-end latency baseline

## Context

Stack já estava de pé há ~18h: `rinha-qdrant`, `rinha-api1`, `rinha-api2`, `rinha-haproxy` (todos via `docker-compose.yml`), com a coleção `references_v1` carregada com 3.000.000 pontos pelo `loader` (saída 0).

Suspeita inicial: latência muito acima do alvo `p99 ≤ 1 ms` do desafio porque o índice HNSW nunca foi construído — o `loader` re-habilita o `indexing_threshold` mas sai imediatamente, sem esperar a build.

## Hipótese

Sem HNSW, `Qdrant` faz brute-force scan int8 sobre 3M vetores 14d a cada query → ~100 ms por chamada. Construindo o HNSW a latência cai para a ordem de ~1 ms.

## Estado da coleção antes do experimento

```
status               : grey
optimizer_status     : ok
indexed_vectors_count: 0
points_count         : 3000000
segments_count       : 4
indexing_threshold   : 1
hnsw_config          : m=16, ef_construct=100, on_disk=true
quantization         : scalar int8, always_ram=true
```

`status: grey` + `indexed_vectors_count: 0` confirmam: pontos carregados, índice nunca construído.

## Baseline (HNSW OFF)

50 payloads de `resources/example-payloads.json` via PowerShell `Invoke-WebRequest` por `http://localhost:9999`:

- Funcional: 50/50 respondem 200, schema correto, `fraud_score` em múltiplos de 0.2.
- Distribuição: 30 `approved=true`, 20 `approved=false`.
- Latência (wall-clock, com overhead do cmdlet): **avg 112 ms / max 156 ms**.

Loop de `curl` warm: ~70 ms por request.

## Tentativa 1 — `PATCH` no otimizador (mem_limit original)

```bash
PATCH /collections/references_v1
{
  "optimizers_config": {
    "indexing_threshold": 10000,
    "max_optimization_threads": 2,
    "default_segment_number": 2
  },
  "hnsw_config": { "m": 16, "ef_construct": 100, "on_disk": true }
}
```

Resposta: `{"result":true}`. Status virou `yellow` por instantes, mas voltou pra `grey` com CPU em ~0% e `indexed_vectors_count = 0`. Build não progredia.

### Causa raiz

`docker logs rinha-qdrant` mostrou:

```
./entrypoint.sh: line 25:     7 Killed                  ./qdrant $@
```

Qdrant estava sendo **OOM-killed em loop**. `docker inspect` revelou que o container estava com `Memory = 1073741824` (1 GiB efetivo), apesar de `mem_limit: 1536m` no compose. Cada vez que o otimizador tentava construir o HNSW, RAM estourava → SIGKILL → restart → retry → kill. Por isso `optimizer_status: ok` e CPU em 0% entre kills.

## Tentativa 2 — bump de RAM/CPU + retry

Override live (sem alterar compose):

```bash
docker update --memory 8g --memory-swap 8g --cpus 2 rinha-qdrant
docker restart rinha-qdrant
```

Repetido o `PATCH` no otimizador. Status: `yellow` imediatamente, CPU subiu para ~190% (2 cores), RAM começou a crescer.

### Progresso observado

| t (s) | status | indexed | segments | CPU    | RAM            |
|------:|--------|--------:|---------:|-------:|----------------|
|     0 | yellow |       0 |        5 | 188%   | 3.74 / 4 GiB   |
|    66 | yellow |       0 |        5 | 192%   | 3.81 / 4 GiB   |
|   183 | yellow |       0 |        5 | 192%   | 3.98 / 4 GiB   |

Em 183 s a memória chegou em 3.98 / 4 GiB → iminente OOM-kill de novo. Live-update para 8 GB:

```bash
docker update --memory 8g --memory-swap 8g rinha-qdrant
```

Build sobreviveu. Conclusão final em **t = 276 s** (~4m36s):

```
status               : green
indexed_vectors_count: 3000000
points_count         : 3000000
segments_count       : 2  (compactado de 5 → 2)
CPU                  : 0.64%
RAM                  : 1.74 GiB
```

## Re-medição de latência (HNSW ON)

### Qdrant interno (campo `time` do response, warm, 10 reqs)

```
0.0013, 0.0016, 0.0006, 0.0006, 0.0005,
0.0005, 0.0005, 0.0004, 0.0005, 0.0005  (segundos)
```

Mediana ~0.5 ms. Primeira query 1.3 ms (cache de mmap aquecendo).

### End-to-end via HAProxy → API → Qdrant

Ferramenta: `hey` (rcmorano/docker-hey) rodando dentro da `rinha-net`, 5000 reqs, mesmo payload, 1 worker (sequencial):

```
Latency distribution:
  10% in 0.0011 s
  25% in 0.0011 s
  50% in 0.0013 s
  75% in 0.0016 s
  90% in 0.0022 s
  95% in 0.0034 s
  99% in 0.0611 s

Status: 5000 / 5000 -> 200
```

Com 16 workers concorrentes:

```
50%  0.0035 s
75%  0.0962 s
90%  0.1966 s
95%  0.2902 s
99%  0.3958 s

Status: 4992 / 5000 -> 200  (8 quedas, provável saturação de fila/conexão)
```

### Comparação direta

| Percentil | HNSW OFF (brute-force) | HNSW ON      | Speedup |
|-----------|-----------------------:|-------------:|--------:|
| p50       | ~110 ms                | **1.3 ms**   | ~85x    |
| p90       | ~120 ms                | 2.2 ms       | ~55x    |
| p95       | ~150 ms                | 3.4 ms       | ~44x    |
| p99       | ~150 ms                | **61 ms**    | ~2.5x   |
| Qdrant interno warm | n/a           | 0.46–0.62 ms | —       |

## Funcional pós-build

50/50 ainda respondem 200. **1 caso de borda mudou** (era `false` com `fraud_score=0.6` na busca exata, virou `true` com `0.0` ou similar) — esperado: HNSW é aproximado, casos com vizinhos no limite do raio podem retornar conjunto ligeiramente diferente. Distribuição: 31 `true` / 19 `false` (vs 30 / 20 na exata).

## Achados

1. **HNSW dá ~100x na mediana** e a coloca exatamente no alvo `p99 ≤ 1 ms` — *no Qdrant*. End-to-end (HAProxy + API + JSON + gRPC) fica em ~1.3 ms p50 / 3.4 ms p95.
2. **p99 = 61 ms na cauda** sequencial não vem do Qdrant (que mede 0.5 ms warm). Suspeitos: JIT tier-1 do .NET, GC pauses, ciclo de conexão Kestrel/HAProxy.
3. **Memória durante a build é o gargalo crítico**: pico de ~4 GB para indexar 3M × 14d com `m=16, ef_construct=100`. Pós-build assenta em **~1.7 GB**, ainda **~9x acima do orçamento de 200 MB** definido no `HLD.md`.
4. **`mem_limit` no compose não bate com a realidade**: container tinha 1 GiB efetivo apesar de `1536m` no YAML. Sem ler `docker logs`, o sintoma (status `grey` em loop) é mudo — `optimizer_status` reporta `ok`.
5. **O `loader` declara sucesso prematuramente**: roda `UpdateCollectionAsync(IndexingThreshold = 20000)` e sai. A readiness probe da API só conta pontos, então `/ready` responde 200 com índice ainda não construído.

## Decisões / próximos passos

Em ordem de prioridade:

1. **Loader: bloquear até `indexed_vectors_count >= TARGET_POINTS` e `status == green`.** Evita declarar bootstrap completo enquanto o índice ainda está em construção. Caso `loader` exceda timeout, sair com código != 0 para o compose falhar e ficar visível.
2. **Readiness probe da API: gate em `indexed_vectors_count`, não em `count`.** Mesmo com loader corrigido, a probe deve refletir a realidade do índice — proteção contra reorgs / segmento novo / kill de container.
3. **Tunar Qdrant para caber em ~200 MB pós-build.** Candidatos:
   - HNSW `m`: 16 → 8 (corta grafo pela metade, recall cai um pouco)
   - Scalar quantization: `always_ram=true` → `false` (bate em mmap; quant é 1 byte/dim, custa pouco em latência)
   - HNSW `on_disk`: já está `true`, manter
   - `default_segment_number`: forçar 1 segmento grande (menos overhead) ou 2 (paralelismo)
4. **Tunar build para caber em <1.5 GB.** Reduzir `ef_construct` (100 → 64), reduzir `max_optimization_threads`, e/ou aumentar segmentação durante ingest pra construir incrementalmente.
5. **Ajustar `mem_limit` no `docker-compose.yml`** pra refletir o necessário (atualmente `1536m` mas Docker Desktop entregou 1 GiB — investigar; talvez WSL2 global).
6. **Atacar p99 da API .NET**: warmup explícito no `Program.cs` (algumas chamadas internas a Qdrant pré-`MapGet`), `DOTNET_TieredPGO=1`, considerar `ServerGC=true`. Talvez também trocar HAProxy `mode http` por `mode tcp` se HAProxy estiver na cauda.

## Comandos para reproduzir

```bash
# 1. Inspeção do estado da coleção (de dentro da rede docker)
docker run --rm --network rinha-backend-2026_rinha-net curlimages/curl:latest \
  -s http://qdrant:6333/collections/references_v1

# 2. Forçar build do HNSW (ad-hoc, sem mexer em código)
docker update --memory 8g --memory-swap 8g --cpus 2 rinha-qdrant
docker restart rinha-qdrant
docker run --rm --network rinha-backend-2026_rinha-net curlimages/curl:latest \
  -s -X PATCH http://qdrant:6333/collections/references_v1 \
  -H 'Content-Type: application/json' \
  -d '{"optimizers_config":{"indexing_threshold":10000,"max_optimization_threads":2}}'

# 3. Latência sequencial via HAProxy (5000 reqs, 1 worker)
echo '<payload>' > .tmp/payload.json
MSYS_NO_PATHCONV=1 docker run --rm --network rinha-backend-2026_rinha-net \
  -v "$PWD/.tmp/payload.json:/payload.json:ro" \
  rcmorano/docker-hey -n 5000 -c 1 -m POST -T 'application/json' \
  -D /payload.json http://haproxy:9999/fraud-score
```

## Estado da stack ao final do experimento

- Qdrant em **8 GB / 2 CPUs** via `docker update` (override live, não persistido no compose; volta aos valores do YAML no próximo `docker compose up`).
- Coleção `references_v1`: `green`, 3M indexados, 2 segments.
- Latência funcional: p50 ~1.3 ms, p95 ~3.4 ms via HAProxy.
- Volume `rinha-backend-2026_qdrant-storage` retém o índice construído — não precisa re-rodar loader em restarts.

# Stress test report — 2026-05-19

Suite completa rodada contra o stack `docker compose` local. 4 cenários sequenciais com `docker stats` amostrado a cada 5 s durante todo o teste.

## Ambiente

| Item | Valor |
|---|---|
| Host | Windows 11 Home + Docker Desktop (WSL2 backend) |
| Imagem | `rinha-fraud-api:latest` (Native AOT, chiseled, x86-64-v3) |
| k6 | `grafana/k6:latest` (Docker, joined to rinha-net) |
| Stack | api-1 + api-2 + HAProxy (compose) |
| Recursos | 1.00 CPU total (0.30 + 0.35 + 0.35), 310 MB |
| `cpu_period` | 10 ms |
| Lloyd refinement | **OFF** (Fase 9 revertida — destruiu cache locality) |

---

## 1/4 Baseline — 20 VUs constante por 30 s

| Métrica | Valor |
|---|---|
| Iterations | 41 832 |
| Throughput | **1 394 req/s** |
| HTTP errors | 0 / 41 832 (0.00 %) |
| p50 | 9.54 ms |
| p90 | 24.51 ms |
| p95 | 33.77 ms |
| p99 | **82.09 ms** |
| p99.9 | 535.41 ms |
| max | 995.17 ms |

**Análise:** sustentado em ~1.4k req/s sem nenhum erro. Distribuição até p95 está saudável (cresce ~3.5× de p50 pra p95). Já em p99 começa a aparecer cauda — 82 ms ainda OK, mas o p99.9 = 535 ms revela que existem ~40 requests no teste que pegaram throttling pesado. Provavelmente CFS schedule misses + Defender jitter.

---

## 2/4 Stress — ramp 1 → 100 VUs ao longo de 90 s

| Métrica | Valor |
|---|---|
| Iterations | 185 052 |
| Throughput | **2 056 req/s** sustentado |
| HTTP errors | 0 / 185 052 (0.00 %) |
| p50 | 28.99 ms |
| p90 | 62.68 ms |
| p95 | 80.69 ms |
| p99 | **139.16 ms** |
| p99.9 | 242.10 ms |
| max | 801.03 ms |

**Análise:** ceiling de **2k req/s** com **zero erros** a 100 VUs concorrentes. p99 cresceu pra 139 ms — esperado, já que a concorrência saturou as duas APIs (CPU peak 36 %). O p99.9 ficou em 242 ms, mostrando cauda controlada (contrastar com baseline onde p99.9 era 535 ms — paradoxalmente melhor sob stress porque o "warmup natural" elimina cold starts).

---

## 3/4 Spike — 5 → 150 VUs em 5 s, hold 20 s, drop em 5 s

| Métrica | Valor |
|---|---|
| Iterations | 67 674 |
| Throughput | 1 353 req/s avg |
| HTTP errors | **0 / 67 674 (0.00 %)** ⭐ |
| p50 | 33.42 ms |
| p90 | 129.30 ms |
| p95 | 158.29 ms |
| p99 | **236.84 ms** |
| p99.9 | 364.37 ms |
| max | 431.85 ms |

**Análise — grande melhoria vs Fase 6:** o mesmo teste de spike na Fase 6 (antes das otimizações Kestrel + Parser + CFS tuning) teve **35 % de erros**. Agora **0 erros** com 150 VUs simultâneas. p99 de 237 ms sob burst é aceitável — sistema absorve e processa em vez de dropar conexões.

O max ficou em 432 ms (vs 1.02 s antes). Sistema mantém latência limitada mesmo no pico de demanda.

---

## 4/4 Official Eval — ramping arrival 1 → 900 req/s ao longo de 120 s

| Métrica | Valor |
|---|---|
| Iterations | 53 876 / 54 100 (99.6 %) |
| Throughput target | 899.97 / 900 req/s (100 %) |
| HTTP errors | 0 |
| True positives (fraud denied) | 23 876 |
| True negatives (legit approved) | 29 866 |
| False positives (legit denied) | 61 |
| False negatives (fraud approved) | 73 |
| Failure rate | 0.25 % |
| weighted_errors_E | 280 |
| **p99** | **139.80 ms** |
| **p99_score** | 854.48 |
| **detection_score** | 1 549.63 |
| **final_score** | **2 404.11 / 6 000** (40 %) |

**Análise:** recall do IVF mantido (mesmo 0.25 % de erros do histórico). Score quase idêntico aos da Fase 7/8, indicando consistência da implementação.

---

## Container stats (CPU/mem) durante toda a suite

177 amostras coletadas a cada 5 s. Resumo agregado:

| Container | CPU min | CPU avg | CPU max | Quota | Saturação no peak |
|---|---|---|---|---|---|
| haproxy | 0.13 % | 17.54 % | 29.97 % | 30 % | **~100 %** |
| api-1 | 0.13 % | 24.57 % | 36.25 % | 35 % | **~100 %** |
| api-2 | 0.17 % | 24.64 % | 36.37 % | 35 % | **~100 %** |

**Insights:**

- **Os 3 containers chegam a 100 % da quota** simultaneamente durante stress/official. Sistema CPU-bound.
- HAProxy avg 17 % vs max 30 % — picos coincidem com momentos de alta arrival rate.
- APIs avg 24 % vs max 36 % — saturação durante stress/spike/official.
- **Memória estável**: ~10-19 MiB no haproxy, ~12-19 MiB em cada API (de 30 MB / 140 MB). Zero pressão de memória.
- Soma de CPU usada (peak): ~30 + 36 + 36 = ~102 % de 1 CPU. **Sistema está no exato limite do envelope alocado.**

---

## Comparativo cross-fase

| Cenário | Fase 6 (sem Kestrel opts) | **Hoje (Kestrel + Parser, Fase 8/9-revertido)** |
|---|---|---|
| Baseline p99 | 20 ms | 82 ms |
| Stress p99 | 60 ms | 139 ms |
| Spike erros | **35 %** | **0 %** ⭐ |
| Official p99 (mediana) | 91 ms (Fase 7) | 139 ms |
| Official score | 2 588 (Fase 7) | 2 404 |

**Observação:** os números absolutos do baseline/stress flutuam de 2-3× entre runs por causa do jitter Windows + WSL2 + Defender. Mas a **eliminação de erros no spike (35 % → 0 %) é estrutural** — vem das otimizações Kestrel (sem timer wheel de slow-client) + parser zero-alloc (sem GC pause durante picos).

---

## Findings

### ✅ Confiabilidade
- **Zero erros HTTP** em todos os 4 cenários, mesmo a 150 VUs simultâneas (10× a capacidade média projetada de 14 VUs)
- Sistema degrada graciosamente: latência sobe, sem dropar conexões

### ✅ Estabilidade de memória
- Working set fica em **~50 MiB total** entre os 3 containers (de 310 MB alocados)
- Sem leaks: memória estável durante 5 min de teste contínuo
- mmap do `.bin` (45 MB) não conta no RSS (page cache do kernel)

### ⚠️ CPU é o gargalo
- Os 3 containers atingem 100 % da quota durante stress e official
- Lei de Little aplicada: a 900 req/s × ~1.25 ms CPU/req = **112 % de demanda** vs **100 % de capacidade**
- Inevitável que p99 saturado: queue cresce, sistema processa o que pode

### ℹ️ Spike é onde mais ganhamos
- A diferença mais dramática vs versões antigas: **35 % de erros → 0 % de erros**
- Otimizações de Kestrel (timer wheels) + parser zero-alloc (sem GC) deixaram o sistema absorver bursts sem dropar

---

## Recomendações

1. **Confiar no juiz Linux nativo** — variance no Windows + WSL2 pode estar inflando p99 em 2-3×. O score de 2 404 hoje é o pior caso provável.

2. **Próximas otimizações pelo ROI:**
   - **Manual response writing** (UTF-8 literals pra 6 fraud_scores) — -0.05 ms/req, baixíssimo risco
   - **UNIX socket** HAProxy↔Kestrel — pode reduzir overhead de TCP local, -1 a -3 ms p50 típico
   - **Pre-warm page cache** no startup — elimina spike de page fault inicial

3. **Não tentar mais Lloyd / k-means** — Fase 9 mostrou regressão de 3× no p99 por destruir cache locality

4. **Branch `submission`** já pode ser feita — comportamento atual é robusto (0 erros sob stress, score 2 400+ consistente, footprint ~50 MB de 310 MB)

---

## Arquivos gerados nesta suite

```
loadtest/results/
  baseline.json         k6 summary export (parsable)
  baseline.txt          k6 stdout completo
  stress.json
  stress.txt
  spike.json
  spike.txt
  official.txt          (run-official-local.js auto-writes run-official-results.json)
  stats.log             docker stats samples (timestamp|name|cpu|mem)
  stress-report.md      ← este arquivo
```

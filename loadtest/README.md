# Load tests (k6)

Cenários k6 que rodam num container Grafana k6 ligado à mesma rede do
`docker-compose` — bate em `http://haproxy:9999` sem passar pelo stack
de rede do host.

Pré-requisito: `docker compose up -d` rodando antes.

## Scripts

| Script | Perfil | Duração | Pra que serve |
|---|---|---|---|
| `smoke.js` | 1 VU constante | 10 s | Correção / shape da resposta (status, JSON, score múltiplo de 0.2) |
| `baseline.js` | 20 VUs constante | 30 s | p99 em regime estável — medida principal |
| `stress.js` | Ramp 1 → 100 VUs | 90 s | Onde p99 começa a degradar |
| `spike.js` | 5 → 150 → 5 burst | 50 s | HAProxy queueing + recuperação |

## Como rodar

PowerShell, a partir da raiz do repo:

```powershell
$NET = 'rinha-backend-2026_rinha-net'

# Smoke
docker run --rm --network $NET -v "${PWD}:/work:ro" `
    grafana/k6 run -e BASE_URL=http://haproxy:9999 /work/loadtest/smoke.js

# Baseline (medida primária do p99)
docker run --rm --network $NET -v "${PWD}:/work:ro" `
    grafana/k6 run -e BASE_URL=http://haproxy:9999 /work/loadtest/baseline.js

# Stress (ramp até 100 VUs)
docker run --rm --network $NET -v "${PWD}:/work:ro" `
    grafana/k6 run -e BASE_URL=http://haproxy:9999 /work/loadtest/stress.js

# Spike (burst de 150 VUs)
docker run --rm --network $NET -v "${PWD}:/work:ro" `
    grafana/k6 run -e BASE_URL=http://haproxy:9999 /work/loadtest/spike.js
```

## Thresholds

- `http_req_duration` — latência observada pelo k6 (inclui rede + HAProxy + JSON)
- `http_req_failed` — fração de respostas não-2xx
- `checks` — taxa de sucesso das assertions (status 200, JSON válido)

`baseline.js` reprova se p99 > 10 ms ou taxa de erro > 0.1 %.

## Notas

- Todos os scripts pegam um dos 50 payloads reais de
  `resources/example-payloads.json` aleatoriamente
- Mount `${PWD}:/work:ro` dá acesso ao dataset e aos scripts sem duplicar arquivos
- Para output em JSON: adicionar `--out json=results.json` no comando
- O k6 não roda dentro do envelope de 1 CPU / 350 MB — só os 3 containers da app rodam

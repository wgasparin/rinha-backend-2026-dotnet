### HLD: Módulo de Detecção de Fraude por Busca Vetorial (Rinha de Backend 2026)

Versão: 1.0
Data: 2026-05-08
Responsável: gasparin@gmail.com

---

### Objetivo técnico
Implementar um serviço HTTP de detecção de fraude por busca vetorial em alta concorrência e baixa latência (p99 alvo ≤ 1 ms), expondo `POST /fraud-score` e `GET /ready` na porta 9999, com pré-processamento dos 3M vetores de referência em build/startup, dentro do envelope de 1 CPU e 350 MB de RAM compartilhados entre Load Balancer, ≥2 instâncias da API e o Vector DB.

Dependências com outros sistemas
- Avaliador da Rinha (cliente HTTP externo que envia transações para `POST /fraud-score` e checa `GET /ready`).
- Arquivos de referência fornecidos pelo desafio (`references.json.gz`, `mcc_risk.json`, `normalization.json`), consumidos em build/startup.

---

### Arquitetura geral
Topologia composta por HAProxy (load balancer round-robin), 2 instâncias .NET Minimal API compiladas com Native AOT (stateless, shared-nothing) e Qdrant single-node como vector DB, todos orquestrados via `docker-compose` na mesma rede `bridge`. Comunicação externa em REST/HTTP+JSON, comunicação interna API↔Qdrant em gRPC. Pré-computação dos vetores de referência ocorre no startup, com snapshot persistido em volume Docker para reutilização entre execuções.

Ambiente de implantação
- On-premises / local containerizado.
- `docker-compose.yml` com modo de rede `bridge`, imagens públicas `linux-amd64`, sem dependência de cloud, auto-contido.

Tecnologias principais
- .NET Minimal API com Native AOT (camada de aplicação)
- HAProxy (load balancer)
- Qdrant single-node (vector DB)
- gRPC/HTTP-2 (transporte interno API↔Qdrant)
- Docker Compose (orquestração)

Padrões adotados
- REST síncrono entre avaliador externo e LB
- gRPC síncrono entre API e Qdrant
- Stateless services nas instâncias da API
- Shared-nothing entre instâncias
- Pré-computação dos vetores no startup antes de `GET /ready` responder 2xx
- Vertical Slice Architecture na organização do projeto .NET

---

### Componentes e responsabilidades
| Componente | Responsabilidades | Dependências |
| ----------- | ----------------- | ------------ |
| HAProxy (LB) | Recebe HTTP na porta 9999, distribui round-robin entre as 2 APIs, sem inspeção de payload nem lógica de negócio | Instâncias da API |
| API .NET (×2, AOT) | Receber `POST /fraud-score`, parsear payload, normalizar vetor de 14 dimensões, consultar Qdrant via gRPC (k=5), calcular `fraud_score`, responder JSON; expor `GET /ready` | Qdrant, constantes em memória (`normalization.json`, `mcc_risk.json`) |
| Qdrant (single-node) | Armazenar coleção de 3M vetores rotulados (`fraud`/`legit`), atender queries kNN via gRPC | Snapshot em volume Docker |
| Loader de ingestão | Bootstrap único: ler `references.json.gz`, normalizar e ingerir vetores no Qdrant antes do `GET /ready` da API responder 2xx | Qdrant, arquivos de referência |

---

### Fluxo de requisições e de dados
**Fluxo de requisição**
- Avaliador envia `POST /fraud-score` na porta 9999.
- HAProxy seleciona uma das 2 APIs via round-robin (sem inspeção do payload).
- API faz parse mínimo do JSON (System.Text.Json source-generated, AOT-friendly).
- API normaliza o payload em vetor de 14 dimensões usando constantes pré-carregadas.
- API envia query kNN (`limit=5`) ao Qdrant via gRPC.
- API recebe os 5 vizinhos com seus rótulos e calcula `fraud_score = nº de fraudes / 5`.
- API monta resposta `{ "approved": fraud_score < 0.6, "fraud_score": <valor> }` e devolve `200 OK`.
- HAProxy repassa a resposta ao avaliador.
- `GET /ready`: HAProxy → API → responde 2xx apenas após o loader confirmar ingestão completa no Qdrant.

**Fluxo de dados**
- Bootstrap (primeira execução): `references.json.gz` → Loader (descompressão em streaming) → normalização (aplicando `normalization.json` e `mcc_risk.json`) → ingestão em batch via gRPC → Qdrant → snapshot persistido em volume Docker.
- Bootstrap (execuções subsequentes): volume Docker → Qdrant carrega snapshot → coleção pronta sem re-ingestão.
- Constantes (`normalization.json`, `mcc_risk.json`): carregadas em memória nas APIs no startup (read-only, imutáveis).
- Runtime: payload JSON → API (transformação in-memory) → Qdrant (kNN gRPC) → vizinhos rotulados → API (agregação) → resposta JSON.
- Sem persistência de requisições, sem replicação, sem fila/event stream.

---

### Modelo de dados (alto nível)
Entidades principais
- ReferenceVector (vetor de 14 dim float32 + label `fraud`/`legit`, persistido no Qdrant)
- TransactionRequest (payload de entrada, efêmero)
- NormalizedVector (representação intermediária de 14 dim, efêmera in-memory)
- FraudDecision (resposta da API, efêmera)
- NormalizationConstants (conteúdo de `normalization.json` em memória nas APIs)
- MccRiskTable (conteúdo de `mcc_risk.json` em memória nas APIs)

Relações
- TransactionRequest →[normalização com NormalizationConstants + MccRiskTable]→ NormalizedVector
- NormalizedVector →[kNN k=5 sobre ReferenceVector]→ vizinhos
- vizinhos →[agregação de labels]→ FraudDecision

Fonte de verdade
- ReferenceVector: Qdrant (única fonte persistente em runtime; backed por `references.json.gz` em build).
- NormalizationConstants e MccRiskTable: arquivos do desafio carregados em memória nas APIs.
- TransactionRequest e FraudDecision: efêmeros, sem fonte persistente.
- Versionamento de coleção: `references_v1` (imutável por edição do desafio).
- Retenção: snapshot persiste no volume Docker; requisições não persistidas; sem cache aplicativo.

---

### Interfaces públicas
| Nome | Tipo | Protocolo | Exposição | SLAs/Limites |
| ---- | ---- | ---------- | --------- | ------------- |
| POST /fraud-score | API | HTTP/1.1 + JSON (REST) | Externa (porta 9999, via HAProxy) | p99 alvo ≤ 1 ms |
| GET /ready | API | HTTP/1.1 (REST) | Externa (porta 9999, via HAProxy) | Responde 2xx apenas após ingestão Qdrant concluída |
| Qdrant gRPC | API | gRPC/HTTP-2 (Protobuf) | Interna (rede `bridge` do compose) | Latência intra-host sub-ms, uso exclusivo das APIs |
| Qdrant snapshot file | SDK/Storage | Filesystem (volume Docker) | Interna (mount no container Qdrant) | Read-only após bootstrap |

---

### Considerações de escalabilidade e disponibilidade
Abordagem geral
- Escala horizontal fixa em 2 APIs (mínimo do desafio), distribuídas via HAProxy round-robin. Sem autoscaling. Qdrant single-node sem sharding (dataset cabe em memória após pré-processamento).

Técnicas aplicadas
- Distribuição verticalizada de RAM: Qdrant ~200 MB, APIs ~50–60 MB cada (graças ao AOT), HAProxy ~20 MB.
- Snapshot Qdrant em volume Docker (cache de bootstrap).
- Constantes em memória nas APIs (zero I/O em runtime).
- Backpressure natural via limites de conexão do HAProxy e do Kestrel.
- Health check do HAProxy para roteamento apenas para instâncias saudáveis.
- `restart: unless-stopped` em todos os serviços do compose.

Meta de disponibilidade
- 99.9% durante a janela do teste oficial (sem requisito de uptime contínuo).

---

### Segurança
Autenticação
- Nenhuma nos endpoints públicos (escopo do desafio); nenhuma na comunicação API↔Qdrant (rede interna).

Autorização
- Nenhuma (single-purpose, sem multi-tenant, sem RBAC).

Proteção de dados
- HTTP em texto puro entre avaliador e HAProxy; gRPC sem TLS internamente.
- Sem criptografia em repouso específica da aplicação.
- Dataset sintético do desafio, sem PII real.
- Sem retenção de payloads; sem log de payloads.

Gestão de segredos
- Não aplicável (sistema não consome credenciais, chaves ou tokens).

---

### Observabilidade
Logs
- Desabilitados ou no nível mínimo absoluto (apenas falha fatal de startup, se ocorrer). Nenhum log no caminho quente.

Métricas
- Nenhuma instrumentação custom. Sem `/metrics`, sem `System.Diagnostics.Metrics`. Inspeção apenas externa via `docker stats` em desenvolvimento.

Tracing
- Desabilitado. Sem OpenTelemetry, sem spans.

Dashboards e alertas
- Não aplicáveis. A pontuação oficial do desafio (`score_p99`, `score_det`) é a única medida de sucesso, computada externamente pelo avaliador.

Justificativa: o projeto otimiza para latência extrema (p99 ≤ 1 ms) no envelope de 350 MB; instrumentação adiciona CPU, memória e I/O no caminho quente, prejudicando diretamente a pontuação.

---

### Riscos arquiteturais e mitigação
#### R1. Estouro do envelope de 350 MB de RAM
- **Probabilidade:** alta
- **Impacto:** OOM-kill de container; falha do teste; possível taxa de erro >15% fixando score em -3000.
- **Mitigação:**
  - Quantização de vetores no Qdrant (scalar int8) para reduzir footprint.
  - Configurar `mmap_threshold` baixo no Qdrant para offloading parcial em disco.
  - Limites rígidos por serviço no `docker-compose` (Qdrant ~200 MB, APIs ~50 MB cada, HAProxy ~20 MB).
  - Validar consumo real com `docker stats` em carga simulada antes da submissão.
- **Plano de contingência:** reduzir dimensionalidade efetiva via Product Quantization ou diminuir `m` do HNSW.

#### R2. p99 estourar 1 ms (latência alvo)
- **Probabilidade:** alta
- **Impacto:** perda de pontos no `score_p99` (até -3000 se p99 > 2000 ms).
- **Mitigação:**
  - .NET Native AOT eliminando JIT/warmup.
  - gRPC binário entre API e Qdrant.
  - HNSW com `ef_search` baixo para favorecer latência sobre recall.
  - System.Text.Json source-generated; sem alocações no caminho quente.
- **Plano de contingência:** trocar HNSW por busca exata em SIMD se 14 dim + 3M vetores couber em estrutura plana otimizada.

#### R3. Tempo de ingestão excessivo no startup
- **Probabilidade:** média
- **Impacto:** teste pode falhar antes de a API estar pronta.
- **Mitigação:**
  - Snapshot Qdrant persistido em volume Docker (reuso entre runs).
  - Ingestão em batch grande via gRPC com paralelismo controlado.
  - Loader desacoplado das APIs.
- **Plano de contingência:** snapshot embarcado na imagem Docker se o volume não for confiável.

#### R4. Qdrant como Single Point of Failure
- **Probabilidade:** baixa
- **Impacto:** indisponibilidade total das APIs durante incidente.
- **Mitigação:**
  - `restart: unless-stopped` no compose.
  - Health check no HAProxy só responde 2xx em `/ready` se as APIs conseguirem ping no Qdrant.
- **Plano de contingência:** sem replicação (envelope não permite); aceitar downtime curto se ocorrer.

#### R5. Divergência da fórmula de detecção (falsos positivos/negativos)
- **Probabilidade:** média
- **Impacto:** redução do `score_det` (até -3000 se taxa de falhas > 15%).
- **Mitigação:**
  - Implementar normalização exatamente conforme `REGRAS_DE_DETECCAO.md`.
  - Validação cruzada com avaliador local (`run.sh`) antes da submissão.
- **Plano de contingência:** ajustar `ef_search` do HNSW para aumentar recall se houver perda de qualidade.

---

### ADRs e próximos passos
ADRs associados
- ADR-001 — Adoção de .NET Minimal API com Native AOT para a camada de aplicação.
- ADR-002 — Adoção de HAProxy como load balancer em round-robin.
- ADR-003 — Adoção de Qdrant single-node como vector database.
- ADR-004 — Comunicação API↔Qdrant via gRPC.
- ADR-005 — Organização do código em Vertical Slice Architecture.
- ADR-006 — Persistência do snapshot Qdrant em volume Docker.
- ADR-007 — Observabilidade desabilitada em runtime.
- ADR-008 — Sem cache aplicativo no caminho quente.

Decisões pendentes
- Versão exata do .NET (hipótese: .NET 10).
- Versão exata do Qdrant (última estável `linux-amd64` validada).
- Tipo de quantização no Qdrant (scalar int8 vs. binary vs. nenhuma) — definição empírica.
- Parâmetros do índice HNSW (`m`, `ef_construct`, `ef_search`) — tuning empírico.
- Distribuição final de CPU/memória entre HAProxy / 2 APIs / Qdrant no `docker-compose.yml`.
- Snapshot embarcado na imagem vs. volume — escolher após medir tempo de bootstrap real.
- Library cliente gRPC para Qdrant (oficial `Qdrant.Client` vs. proto custom) — verificar compatibilidade AOT.

Próximos passos
- Provar viabilidade do envelope: POC com Qdrant + 3M vetores, medir RAM com e sem quantização.
- Validar AOT: compilar Minimal API com AOT + cliente gRPC; confirmar ausência de warnings de trimming.
- Definir layout do `docker-compose.yml` com limites preliminares de CPU/memória por serviço.
- Implementar Loader de ingestão e medir tempo de carga dos 3M vetores.
- Implementar normalização das 14 dimensões conforme `REGRAS_DE_DETECCAO.md` e validar contra exemplos do desafio.
- Executar `run.sh` localmente para benchmark inicial de p99 e taxa de erro.
- Iterar tuning de HNSW e quantização para encontrar ponto ótimo entre latência e qualidade.
- Estabilizar branch `submission` com `docker-compose.yml` final e abrir issue `rinha/test`.

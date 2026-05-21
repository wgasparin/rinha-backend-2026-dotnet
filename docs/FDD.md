### FDD: POST /fraud-score (Detecção de Fraude por Busca Vetorial)

Versão: 1.0
Data: 2026-05-08
Responsável: gasparin@gmail.com

---

### 1. Contexto e motivação técnica
O sistema precisa expor um endpoint HTTP que receba uma transação de cartão e retorne, com p99 ≤ 1 ms, uma decisão de aprovar/negar baseada em busca vetorial sobre um dataset de 3M vetores rotulados. O endpoint é o único caminho quente do sistema e é diretamente avaliado pelo `score_p99` e `score_det` da Rinha de Backend 2026.

A feature vive na camada API .NET (Minimal API + Native AOT), em uma das duas instâncias balanceadas pelo HAProxy. Consome as constantes `NormalizationConstants` e `MccRiskTable` (carregadas em memória no startup) e consulta o Qdrant via gRPC para kNN (k=5) sobre `ReferenceVector[]`. É implementada como uma vertical slice isolada (`FraudScore/` com endpoint, handler, contratos e normalização juntos).

**Atores**
- Avaliador da Rinha (cliente HTTP externo, único consumidor).
- HAProxy (roteador upstream, round-robin entre 2 instâncias).
- Qdrant (dependência síncrona via gRPC).

**Limites do escopo**
- Apenas o caminho de inferência síncrono.
- Sem treinamento, sem atualização de dataset em runtime, sem persistência de requisições.
- Não inclui o `GET /ready` (feature separada) nem o Loader de ingestão (feature separada).

**Suposições**
- O Qdrant está pronto e populado com a coleção `references_v1` (3M vetores) quando o endpoint começa a receber tráfego (garantido pelo gate do `GET /ready`).
- As constantes `normalization.json` e `mcc_risk.json` foram carregadas em memória no startup (read-only, imutáveis).
- O HAProxy só envia tráfego para instâncias saudáveis (health check ativo).
- Payloads seguem exatamente o contrato de `API.md` do desafio.
- O cliente gRPC do Qdrant é compatível com .NET 10 Native AOT (sem warnings de trimming).

**Restrições**
- p99 alvo ≤ 1 ms para o endpoint inteiro (parsing + normalização + gRPC + agregação + serialização).
- Footprint da instância API ≤ 50–60 MB de RAM (envelope total de 350 MB compartilhado com Qdrant e HAProxy).
- 1 CPU compartilhada entre todos os serviços do compose.
- Sem cache aplicativo, sem observabilidade em runtime, sem TLS.
- Sem alocações desnecessárias no caminho quente.
- Compatível com `linux-amd64`, modo de rede `bridge`.

---

### 2. Objetivos técnicos
- Atender `POST /fraud-score` com **p99 ≤ 1 ms** sob carga do avaliador oficial. Medida: `score_p99` da Rinha; meta direta no resultado do `run.sh`.
- Garantir **decisão determinística**: para o mesmo payload e o mesmo estado da coleção, a resposta é idêntica. Invariante: `approved == (fraud_score < 0.6)`.
- Implementar a **normalização das 14 dimensões** exatamente conforme `REGRAS_DE_DETECCAO.md`. Invariante: byte-identidade com vetores de referência gerados pelas mesmas fórmulas.
- Executar **kNN com k=5** sobre a coleção `references_v1` para todo request. Invariante: o `fraud_score` retornado é múltiplo de 0.2 no intervalo `[0.0, 1.0]`.
- Manter **zero alocações evitáveis** no caminho quente. Medida: build sem warnings de trimming/AOT, uso de `System.Text.Json` source-generated, reuso de buffers via `ArrayPool` quando aplicável.
- Garantir **schema de resposta fixo**: 100% das respostas 200 OK contêm exatamente `{ "approved": bool, "fraud_score": float }`.

---

### 3. Escopo e exclusões

**Incluído**
- Endpoint `POST /fraud-score` registrado via Minimal API.
- Deserialização do payload com `System.Text.Json` source-generated.
- Validação mínima de presença dos campos obrigatórios.
- Normalização in-memory do payload em vetor de 14 dimensões `float32`.
- Chamada gRPC `Search` ao Qdrant com k=5 sobre `references_v1`.
- Agregação dos rótulos retornados e cálculo de `fraud_score`.
- Serialização da resposta.
- Tratamento de erros mapeado em status 400/503.

**Excluído**
- Endpoint `GET /ready` (feature separada).
- Loader de ingestão dos 3M vetores no Qdrant (feature separada).
- Treinamento ou atualização do dataset em runtime.
- Cache aplicativo de respostas ou de resultados de kNN.
- Autenticação, autorização, TLS.
- Logs, métricas, tracing em runtime (desabilitados por ADR-007).
- Retries, circuit breaker, fallback alternativo.
- Rate limiting explícito.

---

### 4. Fluxos detalhados e diagramas

**Fluxo principal**
1. HAProxy recebe `POST /fraud-score` na porta 9999 e roteia round-robin para uma das 2 APIs.
2. Kestrel da API .NET recebe a requisição.
3. Endpoint Minimal API deserializa o JSON via context source-generated.
4. Validação mínima: presença dos campos obrigatórios `id`, `transaction`, `customer`, `merchant`, `terminal`, `last_transaction`.
5. Normalização in-memory: aplica fórmulas de `REGRAS_DE_DETECCAO.md` usando `NormalizationConstants` e `MccRiskTable` para produzir vetor de 14 dimensões `float32` em buffer reutilizável.
6. Chamada gRPC `Search` ao Qdrant com `collection_name=references_v1`, `vector=<14 dim>`, `limit=5`, parâmetros HNSW configurados (ex.: `ef=baixo` para favorecer latência).
7. Qdrant retorna até 5 vizinhos com payload mínimo `{ label }`.
8. Agregação: `fraud_count = nº de vizinhos com label == "fraud"`.
9. Cálculo: `fraud_score = fraud_count / 5.0`.
10. Decisão: `approved = fraud_score < 0.6`.
11. Serialização e resposta `200 OK` com `{ "approved": <bool>, "fraud_score": <float> }`.

**Fluxos alternativos e exceções**
- Payload malformado (JSON inválido) → resposta `400 Bad Request` com corpo vazio ou mensagem mínima.
- Campo obrigatório ausente → resposta `400 Bad Request`.
- Falha na chamada gRPC ao Qdrant (conexão, indisponibilidade) → resposta `503 Service Unavailable`.
- Timeout da chamada gRPC ao Qdrant (> 50 ms hipótese) → resposta `503 Service Unavailable`.
- Qdrant retorna menos de 5 vizinhos (esperado: nunca, dataset com 3M) → calcula `fraud_score` sobre o que retornou (defensivo, não recupera) e responde normalmente.

**Diagramas**
- Sequência: `Avaliador → HAProxy → API → Qdrant (gRPC Search) → API → HAProxy → Avaliador`.
- Estados internos: `Recebido → Validado → Normalizado → kNN → Agregado → Respondido`.

---

### 5. Contratos públicos (assinaturas, endpoints, headers, exemplos)

**`POST /fraud-score`**
- Tipo: `http_endpoint`
- Rota: `POST /fraud-score`
- Método: `POST`
- Content-Type esperado: `application/json`
- Exposição: externa (porta 9999, via HAProxy)
- Versionamento: não versionado em URL (contrato fixo pela edição do desafio).

**Semântica de status**
- `200 OK` — decisão calculada com sucesso.
- `400 Bad Request` — payload malformado ou campo obrigatório ausente.
- `503 Service Unavailable` — falha ou timeout na dependência Qdrant.

**Semântica de headers**
- `Content-Type: application/json` — obrigatório no request e response.
- Nenhum header customizado.

**Limites**
- Rate: sem limite explícito (controlado pelo avaliador).
- Payload size: ~1–2 KB típico, hipótese de teto em 4 KB.
- Timeout interno (API → Qdrant): hipótese 50 ms.

**Exemplo de requisição**
```json
{
  "id": "tx-123",
  "transaction": { "amount": 384.88, "installments": 3, "requested_at": "2026-05-08T12:00:00Z" },
  "customer": { "avg_amount": 769.76, "tx_count_24h": 3, "known_merchants": ["MERC-001"] },
  "merchant": { "id": "MERC-001", "mcc": "5912", "avg_amount": 298.95 },
  "terminal": { "is_online": false, "card_present": true, "km_from_home": 13.7 },
  "last_transaction": { "timestamp": "2026-05-08T11:50:00Z", "km_from_current": 18.8 }
}
```

**Exemplo de resposta**
```json
{ "approved": false, "fraud_score": 0.8 }
```

---

### 6. Erros, exceções e fallback

**Matriz de erros previstos**

| Condição | Tratamento | Observações |
| --- | --- | --- |
| JSON malformado | `400 Bad Request` | Corpo mínimo ou vazio; sem log |
| Campo obrigatório ausente | `400 Bad Request` | Validação curta-circuita antes de normalizar |
| MCC desconhecido | Usa fallback default da `MccRiskTable` | Define-se valor neutro durante carga |
| Vetor fora de domínio numérico (NaN/Inf) | `400 Bad Request` | Defensivo; não deveria ocorrer com payload válido |
| Falha de conexão com Qdrant | `503 Service Unavailable` | Sem retry no caminho quente |
| Timeout gRPC > 50 ms (hipótese) | `503 Service Unavailable` | Sem retry; aceitar perda em vez de duplicar custo |
| Qdrant retorna menos de 5 vizinhos | Calcula score sobre o que retornou | Caso defensivo; não esperado |

**Estratégias de resiliência**
- Timeouts: timeout gRPC curto (~50 ms hipótese), abortando a requisição cedo.
- Retries: **não há retries** no caminho quente (custo de latência inaceitável; falsos positivos no `score_det` pesam menos que estouro de p99).
- Backoff: não aplicável.
- Circuit breaker: não aplicável (Qdrant é dependência única e local; falha sustentada já é capturada pelo health check do HAProxy via `GET /ready`).

**Política de fallback**
- Nenhum fallback de inferência (não inverter score, não chutar valor padrão). Em caso de falha de dependência, retornar `503` é preferível a responder com decisão sintética que penalizaria o `score_det`.

**Invariantes**
- `fraud_score ∈ {0.0, 0.2, 0.4, 0.6, 0.8, 1.0}` em condições normais (k=5, dataset completo).
- `approved == (fraud_score < 0.6)` sempre.
- Resposta `200 OK` sempre conforma o schema `{ "approved": bool, "fraud_score": float }`.
- Endpoint é stateless: nenhuma instância persiste estado entre requisições.
- Normalização é determinística e idempotente sobre o mesmo payload.

---

### 7. Observabilidade

**Métricas**
- Nenhuma instrumentação custom em runtime (decisão arquitetural ADR-007).

**Logs**
- Formato: nenhum log no caminho quente.
- Campos: não aplicável.
- Apenas falha fatal de startup pode emitir uma linha em `stderr`.

**Tracing**
- Nenhum span. OpenTelemetry desabilitado.

**Dashboards e alertas**
- Não aplicáveis. A pontuação oficial do desafio (`score_p99`, `score_det`) é a única medida de sucesso, computada externamente pelo avaliador da Rinha.

**Justificativa**
- O projeto otimiza para latência extrema (p99 ≤ 1 ms) no envelope de 350 MB. Instrumentação adiciona CPU, memória e I/O no caminho quente, prejudicando diretamente a pontuação.

---

### 8. Dependências e compatibilidade

| Componente | Versão mínima | Observações |
| --- | --- | --- |
| .NET | 10.0 | Native AOT habilitado; Minimal API |
| Qdrant | última estável `linux-amd64` validada | Modo single-node; coleção `references_v1` populada |
| HAProxy | última estável | Round-robin, health check em `GET /ready` |
| Cliente gRPC Qdrant | versão compatível com .NET 10 AOT | `Qdrant.Client` oficial ou proto custom gerado; verificar ausência de warnings de trimming |
| `System.Text.Json` | nativo do .NET 10 | Source generation obrigatória (sem reflexão) |
| Docker / Compose | qualquer versão suportando `linux-amd64` e modo `bridge` | Conforme regras do desafio |

**Garantias de compatibilidade**
- Contrato JSON do `POST /fraud-score` segue exatamente `API.md` da edição 2026 do desafio (sem versionamento em URL).
- Coleção do Qdrant nomeada `references_v1` permite re-ingestão futura sem quebrar o endpoint.
- Build AOT garante que mudanças de pacote dependente sejam detectadas em tempo de compilação (sem fallback dinâmico).
- Sem versionamento semântico externo (artefato é um docker-compose imutável submetido ao desafio).

---

### 9. Critérios de aceite técnicos
- p99 do `POST /fraud-score` ≤ 1 ms validado pelo `run.sh` local antes da submissão.
- 100% das respostas `200 OK` contêm exatamente as chaves `approved` (bool) e `fraud_score` (float).
- `approved` é deterministicamente derivado de `fraud_score < 0.6`.
- `fraud_score` retornado é múltiplo de 0.2 em condições normais.
- Taxa de erro HTTP < 1% durante a execução do teste oficial.
- Build de release com AOT publica sem warnings de trimming nem AOT.
- Footprint da instância API observado via `docker stats` ≤ 60 MB sob carga.
- Validação cruzada: ao menos 5 payloads de exemplo do desafio produzem o mesmo `fraud_score` que a referência manual calculada com as fórmulas de `REGRAS_DE_DETECCAO.md`.
- Em falha simulada do Qdrant (container parado), endpoint responde `503` e nunca `200` com decisão sintética.

---

### 10. Riscos e mitigação

#### R1. Latência da chamada gRPC ao Qdrant excede budget de p99
- **Probabilidade:** alta
- **Impacto:** estoura p99 ≤ 1 ms, perdendo pontos no `score_p99`.
- **Mitigação:**
  - Conexão gRPC persistente (HTTP/2 multiplex) reusada entre requisições.
  - HNSW configurado com `ef_search` baixo para minimizar trabalho do Qdrant.
  - Quantização escalar int8 da coleção para reduzir cache miss.
  - Co-localização API + Qdrant na mesma rede `bridge` do compose.
- **Plano de contingência:** trocar HNSW por busca exata SIMD em estrutura plana custom dentro do próprio processo da API se Qdrant não atingir budget.

#### R2. Alocações no caminho quente fragmentando o GC e elevando p99
- **Probabilidade:** alta
- **Impacto:** p99 instável, gerando outliers que dominam a pontuação.
- **Mitigação:**
  - System.Text.Json source-generated, sem reflexão.
  - Reuso de buffers via `ArrayPool<float>` para o vetor de 14 dim.
  - Evitar LINQ no caminho quente.
  - Estruturas `record struct` para DTOs intermediários, evitando alocações em heap.
- **Plano de contingência:** utilitário `dotnet-trace`/`dotnet-counters` em build de dev para identificar pontos de alocação e refatorar.

#### R3. Custo de deserialização JSON consumindo orçamento de latência
- **Probabilidade:** média
- **Impacto:** parte significativa dos ~1 ms gasta antes mesmo da inferência.
- **Mitigação:**
  - `JsonSerializerContext` com source generation.
  - Tipos POCO concretos (sem polimorfismo, sem `JsonElement` dinâmico).
  - Considerar `Utf8JsonReader` direto se necessário em otimização final.
- **Plano de contingência:** parser custom mínimo focado apenas nos campos relevantes para a normalização.

#### R4. Cliente gRPC Qdrant incompatível com Native AOT
- **Probabilidade:** média
- **Impacto:** impossibilita compilação AOT, forçando JIT e degradando startup/footprint.
- **Mitigação:**
  - Validar `Qdrant.Client` oficial em build AOT no início do desenvolvimento.
  - Alternativa: gerar stubs gRPC a partir do `.proto` do Qdrant com `Grpc.Tools` configurado para AOT.
- **Plano de contingência:** fallback para chamadas REST ao Qdrant (perdendo binário) ou cliente HTTP/2 minimalista próprio.

#### R5. Divergência da normalização vs. dataset de referência
- **Probabilidade:** média
- **Impacto:** vizinhos retornados não correspondem semanticamente ao payload, prejudicando `score_det`.
- **Mitigação:**
  - Implementar fórmulas exatamente conforme `REGRAS_DE_DETECCAO.md`.
  - Testes unitários comparando vetores gerados com vetores esperados a partir de exemplos do desafio.
  - Validação local com `run.sh` antes de cada submissão.
- **Plano de contingência:** ajuste fino de `ef_search` no Qdrant para aumentar recall, compensando ruído de normalização.

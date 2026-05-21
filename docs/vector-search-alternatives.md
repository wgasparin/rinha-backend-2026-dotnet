# Alternativas de busca vetorial para 14 dimensões

> Análise feita no contexto da Rinha de Backend 2026 (fraud detection).
> Restrições: 3M vetores × 14 dims, 1 vCPU, 350 MB RAM total, p99 ≤ 1 ms, AOT obrigatório.

Com 14 dimensões o jogo muda bastante. A maioria dos vector DBs (Qdrant, Milvus, Weaviate) é projetada pra 100–1000+ dims, onde HNSW/IVF compensam o overhead. Em 14 dims, abordagens "burras" ficam competitivas e cabem em poucas dezenas de MB.

## Tamanho bruto dos dados (3M × 14)

| Representação | Tamanho |
|---|---|
| float32 | ~168 MB |
| float16 / bfloat16 | ~84 MB |
| int8 quantizado | ~42 MB |
| PQ 7 bytes/vec | ~21 MB |

Ou seja, os dados *brutos* já cabem confortavelmente em RAM. O problema do Qdrant não é o índice — é o processo dele (Rust + tokio + gRPC + WAL + segments).

## Famílias de algoritmos pra 14 dims

### 1. Flat (brute force) com SIMD — *o candidato mais forte aqui*

- Varre os 3M vetores calculando distância. Com AVX2 (8 float32/ciclo) ou AVX-512 (16), são ~42M ops = sub-milissegundo num único core moderno.
- Em int8 + dot product com `Vector256<sbyte>`/`Vector512<sbyte>`, fica ainda mais rápido (4× throughput).
- Zero overhead de estrutura. 100% AOT-friendly em C# puro (`System.Numerics.Tensors`, `Vector<T>`, intrínsecos `Avx2`).
- Pra k=5 dá pra usar um heap pequeno fixo ou só uma busca linear pelo top-5.

### 2. KD-Tree / VP-Tree / Ball-Tree — funciona bem até ~20 dims

- Em 14 dims ainda é viável (acima de ~25 vira praticamente brute force).
- `O(log n)` médio, mas com 3M nodes você paga muitos cache misses — na prática SIMD flat costuma ganhar em dims baixas.
- Implementação simples e AOT-safe.

### 3. HNSW / NSG / DiskANN — graph-based

- Mata seu orçamento de RAM: grafo com M=16 vizinhos × 4 bytes × 3M = ~200 MB *só de arestas*. Não cabe.
- `usearch` (lib C, tem binding .NET) com mmap permite jogar grafo+vetores em disco; OS page cache cuida do hot set.

### 4. IVF (Inverted File) — clustering + busca dentro de bucket

- Treina ~1k centroides, busca top-N buckets e faz brute force neles.
- Trade-off recall × velocidade. Pra 14 dims, ganho marginal sobre flat-SIMD.

### 5. Product Quantization (PQ) / Scalar Quantization

- Reduz 168 MB → 20–40 MB.
- Pra 14 dims é exagero — flat int8 já cabe.

### 6. LSH — hash-based

- Memória barata, mas recall ruim em dims baixas com distribuição não-aleatória. Provavelmente não atinge a precisão exigida pelo desafio.

### 7. Disco / mmap puro

- mmap de um arquivo flat de 168 MB. Linux serve via page cache. Footprint do processo "oficial" fica em poucos MB; o resto é cache do kernel que não conta no `cgroup memory.usage` do container do mesmo jeito que heap.
- Combinado com SIMD você tem brute force "disk-backed" mas quente.

## Alternativas a Qdrant se quiser manter serviço externo

- **`usearch`** (Unum) — single header C++, mmap nativo, ~10 MB RSS. Tem [USearch.NET](https://github.com/unum-cloud/usearch).
- **`hnswlib`** — biblioteca pura, sem servidor, embute no processo.
- **SQLite + extensão `sqlite-vec`** — vetor em disco, brute force em SQL. Footprint absurdamente baixo.
- **Vald / Marqo / Chroma** — todos pesados demais pro budget.

## Recomendação

Dado p99 ≤ 1 ms, 1 vCPU compartilhado, AOT obrigatório e o HLD já assumindo Qdrant fora do processo:

**Embutir um flat index SIMD no próprio processo .NET AOT**, com vetores em int8 (escala fixa nas 14 dims já normalizadas), 42 MB residentes. Sem gRPC, sem network hop — economiza os ~200 MB do Qdrant *e* ganha latência (sem serializar/desserializar request). O HLD vira:

```
Avaliador → HAProxy → API (índice in-process) → resposta
```

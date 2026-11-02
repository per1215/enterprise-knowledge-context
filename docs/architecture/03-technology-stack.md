# 3. Technology stack

Versions are the latest stable releases checked on **2026-11-01**. They are pinned when each phase is implemented and re-verified at that time.

## 3.1 Platform components

| Concern | Choice | Version | Licence | Why | Rejected alternatives |
|---|---|---|---|---|---|
| Backend | .NET / ASP.NET Core | 10.0 LTS | MIT | team skill set, performance, LTS to Nov 2028 | — |
| LLM abstraction (in-process) | Microsoft.Extensions.AI (`IChatClient`, `IEmbeddingGenerator`) | 10.11.0 | MIT | vendor-neutral; OpenAI-compatible client covers Ollama, vLLM and llama.cpp | Semantic Kernel as the core abstraction (heavier; SK now builds on M.E.AI) |
| Agent loops | Microsoft Agent Framework (`Microsoft.Agents.AI`) | 1.27.0 | MIT | tool-calling agents on top of M.E.AI, the successor to SK agents | LangChain (Python), custom loops |
| MCP | ModelContextProtocol C# SDK | 2.3.0 | MIT | exposes EKC tools to any MCP client (VS Code, other agents) | — |
| System of record | PostgreSQL | 18.x | PostgreSQL | metadata, ACL mirror, knowledge graph tables, FTS, jobs, audit in one ACID store | separate graph DB in v1 |
| Vector search | Qdrant | 1.20.0 | Apache-2.0 | dense + sparse (hybrid) in one query, payload filters for ACL and tenant, quantisation, sharding | pgvector only (no native sparse/hybrid at scale), Weaviate, Milvus (heavier) |
| Knowledge graph | PostgreSQL property-graph tables + recursive CTEs | — | — | no new database; transactional with ACLs; enough for millions of edges | Apache AGE (PG18 build still RC), Neo4j (GPL or commercial, JVM memory) |
| Object storage | SeaweedFS (S3 API), or filesystem on the laptop | 4.51 | Apache-2.0 | S3-compatible, light, actively maintained | MinIO (community repo archived; no community release since Oct 2025) |
| Messaging | NATS JetStream | 2.15.2 | Apache-2.0 | ~20 MB RAM, durable work queues, key-value store for sync cursors, runs everywhere | RabbitMQ (heavier), Kafka (overkill for v1) |
| Document extraction | Docling Serve | 1.38.2 | MIT | layout-aware PDF/DOCX/PPTX/XLSX/HTML to Markdown, tables, OCR | Apache Tika (weaker structure), Unstructured |
| Code parsing | tree-sitter (all languages) + Roslyn (C# deep) + SCIP indexers where available | tree-sitter 0.27.1; scip-dotnet 0.2.14, scip-typescript 0.5.2, scip-python 0.6.8 | MIT / Apache-2.0 | tree-sitter: fast, robust, 40+ grammars; SCIP: precise cross-repository references | regex/LLM-only parsing |
| Identity | Keycloak (brokers Entra ID, AD/LDAP, SAML) | 26.8.1 | Apache-2.0 | one OIDC issuer for all clients; works offline/on-prem; federates Entra | direct Entra only (no on-prem/offline path) |
| Fine-grained authorization | OpenFGA (ReBAC + conditions for ABAC) | 1.22.1 | Apache-2.0 | Zanzibar model fits inherited source permissions; `ListObjects` drives retrieval filters | OPA (policy, not relationships), Cedar (no relationship store) |
| Secrets | OpenBao | 2.7.2 | MPL-2.0 | Vault-compatible API; dynamic DB credentials; transit encryption for connector tokens | HashiCorp Vault (BSL licence) |
| Edge / ingress | Traefik (Compose), Envoy Gateway (Kubernetes Gateway API) | 3.7.17 / 1.9.3 | MIT / Apache-2.0 | TLS, routing, rate limits | ingress-nginx (retired by Kubernetes, Nov 2025 announcement) |
| Egress control | Envoy or Squid egress proxy + network policies | — | Apache-2.0 / GPL | only the Enrichment service may reach allow-listed hosts | app-level checks only |
| Observability | OpenTelemetry Collector → Prometheus, Loki, Tempo → Grafana | 0.164.0 / 3.15.1 / 3.7.9 / 3.1.1 / 13.3.0 | Apache-2.0 / AGPL (Grafana, Loki, Tempo, used unmodified) | one standard for traces, metrics and logs | Elastic stack (heavier) |
| LLM tracing and evaluations | Langfuse (self-hosted, server profile) | 4.72.0 | MIT core (enterprise folder excluded) | prompt/trace inspection, datasets, scores | — |
| Web portal | React + TypeScript + Vite | current LTS | MIT | — | — |
| VS Code extension | TypeScript, VS Code API ≥ 1.144 | — | MIT | inline completion, webview chat, chat participant, authentication provider | Continue fork (fork maintenance, less control over auth/ACL) |
| IaC | OpenTofu + Helm 4 + Docker Compose | 1.13.2 / 4.3.1 | MPL-2.0 / Apache-2.0 | open Terraform-compatible IaC; Helm for apps; Compose for laptop/single server | Terraform (BSL), Pulumi |
| Kubernetes on-prem | k3s | 1.37.1 | Apache-2.0 | small footprint, GPU operator compatible | — |

Licence watch: Grafana, Loki and Tempo are AGPL-3.0. Running them unmodified as separate services is standard practice, but legal should confirm. Langfuse's `ee/` folder is under a commercial licence and is not used.

## 3.2 Model runtimes

All runtimes are reached through **one** adapter (`OpenAiCompatibleChatClient`), with a capability registry per model (context window, tool calling, FIM support, JSON mode).

| Runtime | Use | Profile |
|---|---|---|
| **Ollama** | simplest; CPU and GPU; pulls models; OpenAI-compatible `/v1` | laptop, workstation |
| **vLLM** (0.32.0; CPU image available) | high-throughput GPU serving, continuous batching, prefix caching, many concurrent users | workstation-gpu, server |
| **llama.cpp server** | CPU/edge, GGUF, tight memory, FIM endpoint | laptop, air-gapped |
| **Text Embeddings Inference (TEI)** 1.9.4 | embeddings + cross-encoder rerank, CPU or GPU | workstation, server |

## 3.3 Model catalogue by hardware profile

Sizes are from the Ollama registry (Q4 quantisation unless noted). **Every model licence must be confirmed before production use.** The ones listed are distributed under Apache-2.0 at the time of writing.

| Task | laptop-cpu (16 GB RAM, no GPU) | workstation-gpu (24–32 GB VRAM) | server (≥ 1× 80 GB GPU) |
|---|---|---|---|
| Chat / reasoning | `qwen3:4b` (2.5 GB) or `qwen2.5-coder:1.5b` | `qwen3:8b` / `gpt-oss:20b` (13.8 GB) | `gpt-oss-120b` (vLLM) or `qwen3-coder` 30B-A3B for code-heavy |
| Code chat / generation | `qwen2.5-coder:1.5b` (1.0 GB) | `qwen3-coder:30b` (18.6 GB, MoE ~3B active) | `qwen3-coder` 30B via vLLM; larger coder models as GPUs allow |
| Inline completion (FIM) | `qwen2.5-coder:1.5b-base` (1.0 GB) | `qwen2.5-coder:7b` base (4.7 GB) | same, many replicas |
| Embeddings | `nomic-embed-text` (0.27 GB, 768-d) or `qwen3-embedding:0.6b` (0.64 GB) | `bge-m3` (1.2 GB, dense + sparse, multilingual) via TEI | `qwen3-embedding:4b` or `bge-m3` via TEI |
| Reranker | none (or small cross-encoder on CPU) | `bge-reranker-v2-m3` via TEI | same, GPU |
| Graph/entity extraction | rules only + 4B model in batch at night | 8B–20B model | 20B+ |

The embedding model is fixed per Qdrant collection. Changing it means re-embedding into a new collection, then switching the alias (blue/green); the platform supports this as a job.

## 3.4 .NET packages (initial)

`Microsoft.Extensions.AI` + `.OpenAI` 10.11.0 · `Microsoft.Agents.AI` 1.27.0 · `ModelContextProtocol` 2.3.0 · `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x · `Qdrant.Client` 1.20.0 · `NATS.Net` 3.3.2 · `OpenFga.Sdk` 0.10.4 · `OpenTelemetry.Extensions.Hosting` 1.20.1 · `Microsoft.Extensions.Http.Resilience` 10.11.0 · `Asp.Versioning.*` 10.2.x · xunit.v3 · Testcontainers 4.16.0.

Reuse from `LocalAIBrain`: the domain patterns proven there (knowledge tiers, document versioning by SHA-256, job leases, cloud-sharing policy, content-addressed storage). They are ported, not shared as a library, so the two products can evolve independently.

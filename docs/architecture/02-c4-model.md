# 2. C4 model

Diagrams are Mermaid and render on GitHub and in VS Code (Markdown Preview Mermaid Support).

## 2.1 Level 1 — System context

```mermaid
C4Context
  title EKC — System context

  Person(dev, "Developer", "Uses VS Code: chat, completion, refactoring")
  Person(analyst, "Architect / Analyst", "Asks about business flows, impact, generates HLD/LLD")
  Person(steward, "Knowledge steward", "Approves enrichment, curates domains, manages sources")
  Person(admin, "Platform admin", "Operates the platform, policies, models")

  System(ekc, "Enterprise Knowledge Context Platform", "Self-hosted knowledge engine, RAG, knowledge graph, code intelligence, self-hosted LLMs")

  System_Ext(idp, "Identity provider", "Microsoft Entra ID / AD / other OIDC-SAML")
  System_Ext(git, "Git hosting", "GitHub / GitHub Enterprise / Azure DevOps / GitLab")
  System_Ext(collab, "Collaboration", "SharePoint, Confluence, Jira")
  System_Ext(data, "Databases & APIs", "SQL Server, PostgreSQL, Oracle; OpenAPI/AsyncAPI specs")
  System_Ext(files, "File shares", "SMB/NFS, local folders")
  System_Ext(extai, "External LLMs (optional)", "OpenAI, Anthropic, Azure OpenAI — enrichment only")
  System_Ext(web, "Public web & docs (optional)", "Vendor docs, public GitHub — enrichment only")

  Rel(dev, ekc, "Chat, completions, search", "VS Code extension, HTTPS")
  Rel(analyst, ekc, "Questions, documents, diagrams", "Web UI, HTTPS")
  Rel(steward, ekc, "Reviews, approvals", "Web UI")
  Rel(admin, ekc, "Configures", "Web UI, GitOps")
  Rel(ekc, idp, "Federated sign-in, groups", "OIDC")
  Rel(ekc, git, "Clones, webhooks, PRs", "HTTPS/SSH")
  Rel(ekc, collab, "Content + permissions", "Graph API / REST")
  Rel(ekc, data, "Schema, lineage metadata", "Read-only")
  Rel(ekc, files, "Documents", "SMB/NFS")
  Rel(ekc, extai, "Gap-filling prompts", "Egress gateway, policy-gated")
  Rel(ekc, web, "Fetch approved sources", "Egress gateway, allow-list")
```

## 2.2 Level 2 — Containers

```mermaid
C4Container
  title EKC — Containers

  Person(dev, "Developer")
  Person(user, "Analyst / Steward / Admin")

  System_Boundary(ekc, "EKC") {
    Container(vsx, "VS Code extension", "TypeScript", "Chat panel, @ekc participant, inline completion, code actions, search, MCP client config")
    Container(web, "Web portal", "React + TypeScript", "Ask, explore graph, review queue, sources, admin")
    Container(gw, "Edge gateway", "Traefik 3 / Gateway API", "TLS, routing, rate limits")
    Container(api, "Knowledge API", ".NET 10 ASP.NET Core", "Query/RAG orchestration, chat, search, graph queries, admin, MCP server")
    Container(ing, "Ingestion workers", ".NET 10 workers", "Connectors, sync, extraction, chunking, embedding, indexing, ACL sync")
    Container(code, "Code intelligence workers", ".NET 10 + tree-sitter/Roslyn/SCIP", "Parse repos into symbols, dependencies, call graphs, API surfaces")
    Container(enr, "Enrichment service", ".NET 10", "Gap detection, external lookup, validation, review workflow; the only container with egress")
    Container(docling, "Docling Serve", "Python", "PDF/DOCX/PPTX/XLSX/HTML to structured Markdown + tables, OCR")
    Container(llm, "Model runtime", "Ollama / vLLM / llama.cpp", "Chat, code, FIM completion models (OpenAI-compatible API)")
    Container(tei, "Embedding & rerank", "TEI or Ollama", "Dense/sparse embeddings, cross-encoder reranking")
    ContainerDb(pg, "PostgreSQL 18", "System of record", "Sources, documents, versions, chunks, ACLs, knowledge graph, jobs, chat, audit")
    ContainerDb(qd, "Qdrant", "Vector DB", "Dense + sparse vectors with ACL/tenant payload filters")
    ContainerDb(obj, "Object storage", "SeaweedFS (S3) / filesystem", "Original files, repo snapshots, exports")
    Container(nats, "NATS JetStream", "Messaging", "Ingestion events, work queues, webhooks fan-out")
    Container(kc, "Keycloak", "IdP broker", "OIDC for all clients, federates Entra ID/AD")
    Container(fga, "OpenFGA", "Authorization", "ReBAC/ABAC: who may read which source/document/repo/domain")
    Container(bao, "OpenBao", "Secrets", "Connector credentials, API keys, DB creds, transit encryption")
    Container(egress, "Egress gateway", "Envoy/Squid", "Allow-listed outbound for enrichment only")
    Container(obs, "Observability", "OTel Collector, Prometheus, Loki, Tempo, Grafana, Langfuse", "Metrics, logs, traces, LLM traces, evals")
  }

  Rel(dev, vsx, "Uses")
  Rel(user, web, "Uses")
  Rel(vsx, gw, "HTTPS, OIDC token")
  Rel(web, gw, "HTTPS, OIDC token")
  Rel(gw, api, "HTTP")
  Rel(api, pg, "SQL")
  Rel(api, qd, "gRPC")
  Rel(api, llm, "OpenAI-compatible HTTP")
  Rel(api, tei, "HTTP")
  Rel(api, fga, "Check / ListObjects")
  Rel(api, nats, "Publish")
  Rel(ing, nats, "Consume / publish")
  Rel(ing, docling, "Convert")
  Rel(ing, tei, "Embed")
  Rel(ing, obj, "Store originals")
  Rel(ing, pg, "Metadata, ACLs, graph")
  Rel(ing, qd, "Upsert vectors")
  Rel(ing, fga, "Write tuples (ACL sync)")
  Rel(ing, bao, "Read connector secrets")
  Rel(code, nats, "Consume repo events")
  Rel(code, pg, "Symbols, dependency graph")
  Rel(enr, egress, "Outbound only via")
  Rel(enr, pg, "Quarantine, reviews")
  Rel(api, kc, "JWKS / token validation")
```

**Why these boundaries**

- **Knowledge API vs. workers.** Query latency must not suffer during bulk indexing, so they scale independently.
- **Enrichment is its own container** because it is the *only* workload allowed to leave the network. Network policy enforces this, not just code.
- **Code intelligence is separate from document ingestion**: different parsers, much heavier CPU, and repository-level incremental logic.
- **Model runtime and embedding are infrastructure, not code.** Swapping Ollama for vLLM is a deployment change.

## 2.3 Level 3 — Knowledge API components

```mermaid
flowchart LR
  subgraph API["Knowledge API (.NET 10)"]
    direction TB
    EP["Endpoints<br/>REST /api/v1 · SSE streaming · MCP server"]
    AUTH["AuthN/AuthZ<br/>JWT (Keycloak) · OpenFGA client · ABAC conditions"]
    QU["Query understanding<br/>intent · domain · entities · rewrite"]
    RET["Retrieval engine<br/>hybrid dense+sparse · ACL filter · graph expansion · rerank"]
    GQ["Graph query service<br/>paths · impact · lineage"]
    CTX["Context builder<br/>budgeting · dedupe · trust-tier ordering · injection guard"]
    GEN["Generation<br/>model router · prompts · citations · streaming"]
    SUF["Sufficiency check<br/>coverage & confidence → enrichment request"]
    AG["Agent runtime<br/>Microsoft Agent Framework · read-only tools"]
    ADM["Admin & catalog<br/>sources · domains · models · policies"]
    AUD["Audit & telemetry"]
  end
  EP --> AUTH --> QU --> RET --> CTX --> GEN --> SUF
  QU --> GQ --> CTX
  EP --> AG --> RET
  AG --> GQ
  EP --> ADM
  GEN --> AUD
  RET --> AUD
```

## 2.4 Level 3 — Ingestion pipeline components

```mermaid
flowchart LR
  SRC["Connector<br/>(files, Git, SharePoint, Confluence, Jira, DB, OpenAPI)"] --> CHG["Change detection<br/>cursor · webhook · ETag · SHA-256"]
  CHG --> ACL["ACL capture<br/>source permissions → principals"]
  CHG --> RAW["Store original<br/>object storage, content-addressed"]
  RAW --> EXT["Extract<br/>Docling · code parsers · schema readers"]
  EXT --> CLS["Classify<br/>domain · sensitivity · doc type · language"]
  CLS --> CHK["Chunk<br/>structure-aware: headings, tables, symbols"]
  CHK --> EMB["Embed<br/>dense + sparse"]
  EMB --> IDX["Index<br/>Qdrant + PostgreSQL FTS"]
  CLS --> KGX["Graph extraction<br/>rules + LLM-assisted entity/relation extraction"]
  KGX --> KG["Knowledge graph<br/>PostgreSQL"]
  ACL --> FGA["OpenFGA tuples"]
  IDX --> QA["Quality checks<br/>dedupe · freshness · eval sampling"]
```

## 2.5 Dynamic view — question with enrichment fallback

```mermaid
sequenceDiagram
  autonumber
  actor U as User (VS Code / Web)
  participant API as Knowledge API
  participant FGA as OpenFGA
  participant Q as Qdrant + PG FTS
  participant KG as Knowledge graph (PG)
  participant LLM as Self-hosted LLM
  participant ENR as Enrichment service
  participant EXT as External LLM / web (optional)
  actor S as Knowledge steward

  U->>API: Question + context (repo, file, domain)
  API->>FGA: ListObjects(user, can_read, source/domain)
  API->>Q: Hybrid search filtered by allowed principals/sources
  API->>KG: Expand related services/APIs/tables (allowed nodes only)
  API->>API: Rerank, re-check top-k with FGA, assemble context by trust tier
  API->>LLM: Prompt (context as quoted data) → stream answer with [n] citations
  API->>API: Sufficiency check (coverage, confidence, unanswered sub-questions)
  alt Internal knowledge sufficient
    API-->>U: Answer + citations
  else Insufficient and policy allows enrichment
    API-->>U: Answer (internal only) + "knowledge gap" notice + optional external lookup
    U->>API: Request external lookup (explicit opt-in)
    API->>ENR: Gap request (redacted question, no internal content unless policy allows)
    ENR->>EXT: Query via egress gateway (allow-list)
    ENR->>ENR: Validate, dedupe, label EXTERNAL + quarantine
    ENR-->>U: Provisional answer marked "External, unverified"
    ENR->>S: Review task
    S->>ENR: Approve / edit / reject
    ENR->>API: Promote approved item → ingestion as Verified (internal wins on conflict)
  end
```

## 2.6 Dynamic view — inline completion in VS Code

```mermaid
sequenceDiagram
  autonumber
  participant ED as VS Code editor
  participant EXT as EKC extension
  participant API as Knowledge API
  participant CI as Code index (PG + Qdrant)
  participant FIM as FIM model (self-hosted)

  ED->>EXT: provideInlineCompletionItems (debounced 150–250 ms)
  EXT->>EXT: Local context: prefix/suffix, open tabs, imports (budgeted)
  EXT->>API: POST /api/v1/code/complete (repo id, path, prefix, suffix)
  API->>CI: Symbols referenced in prefix → definitions, coding-standard snippets (cached)
  API->>FIM: FIM prompt (prefix + retrieved snippets + suffix), max 64–128 tokens
  FIM-->>API: Completion
  API-->>EXT: Completion (+ trace id)
  EXT-->>ED: Ghost text; accept/reject telemetry (no code stored by default)
```

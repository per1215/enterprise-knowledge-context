# 8. Deployment, infrastructure-as-code and operations

## 8.1 Deployment profiles

The same images and configuration schema are used everywhere. A profile selects the services, sizes and models.

| Profile | Target | Orchestration | Notes |
|---|---|---|---|
| `laptop` | 16 GB dev machine, CPU only (e.g. the 2017 Intel MacBook on macOS 13 via Colima) | Docker Compose | single replica; filesystem instead of SeaweedFS; Ollama; no Langfuse; Grafana optional |
| `workstation-gpu` | one Linux box with 24–32 GB VRAM | Docker Compose + NVIDIA Container Toolkit | vLLM or Ollama on GPU; TEI; full observability |
| `server` | on-prem cluster or private cloud | k3s/Kubernetes + Helm | HA API and workers (HPA on queue depth), PostgreSQL HA (CloudNativePG), Qdrant cluster, vLLM on GPU nodes (NVIDIA GPU Operator) |
| `cloud` | AKS / EKS / GKE | OpenTofu modules + same Helm chart | managed PostgreSQL optional, managed disks, private endpoints; LLMs still self-hosted on GPU node pools |

## 8.2 Repository layout (target)

```
enterprise-knowledge-context/
├── docs/                      architecture, ADRs, plan, runbooks
├── src/
│   ├── Ekc.Domain/            entities, tiers, graph types, policies (no deps)
│   ├── Ekc.Application/       ports, use cases, pipelines
│   ├── Ekc.Infrastructure/    PostgreSQL, Qdrant, NATS, OpenFGA, OpenBao, object store, model runtimes
│   ├── Ekc.Connectors.*/      FileSystem, Git, SharePoint, Confluence, Jira, Database, OpenApi, Web
│   ├── Ekc.CodeIntel/         tree-sitter, Roslyn, SCIP importers, surface extractors
│   ├── Ekc.Api/               REST, SSE, MCP server
│   ├── Ekc.Workers.Ingestion/ Ekc.Workers.Code/ Ekc.Enrichment/
│   └── Ekc.ServiceDefaults/   OTel, health, resilience, auth wiring
├── clients/
│   ├── vscode/                VS Code extension (TypeScript)
│   └── web/                   portal (React + TypeScript)
├── tests/                     unit, integration (Testcontainers), contract, ACL-leak, e2e
├── eval/                      golden sets, fixture corpus, evaluation runner
├── deploy/
│   ├── compose/               base + profile overlays (laptop, workstation-gpu), .env.example
│   ├── helm/ekc/              umbrella chart + values-{server,cloud}.yaml
│   ├── tofu/                  modules: network, k8s cluster, gpu-pool, storage, dns; envs: onprem, azure, aws
│   ├── keycloak/              realm exports (dev realm, Entra federation template)
│   ├── openfga/               model + tests
│   └── observability/         collector config, dashboards, alerts
├── scripts/                   bootstrap, models, backup/restore, health
└── .github/workflows/         build, test, eval, image signing, SBOM, release
```

## 8.3 Laptop profile memory budget (16 GB, no GPU)

| Service | Limit |
|---|---|
| PostgreSQL | 512 MB |
| Qdrant | 768 MB |
| NATS | 64 MB |
| Keycloak (dev mode, tuned JVM) | 512 MB |
| OpenFGA (PostgreSQL datastore) | 128 MB |
| Knowledge API | 512 MB |
| Ingestion + code workers (combined in one process on this profile) | 512 MB |
| Docling Serve (on demand, stopped after a batch) | 1.5 GB |
| Ollama: 1.5–4B chat + FIM + embedding | ~3 GB |
| **VM total** | **~7.5–8 GB** |

OpenBao, SeaweedFS, Langfuse and the Grafana stack are **off** on this profile (Docker secrets, filesystem storage, and console/OTLP logs replace them). This is the tightest fit, so the laptop runs one heavy job at a time.

## 8.4 Operations

- **Observability:** OTel traces across API → retrieval → model; RED metrics per endpoint; queue depth and lag per connector; model latency and tokens/s; GPU utilisation (DCGM exporter). Grafana dashboards and alert rules are shipped as code.
- **Backups:** PostgreSQL (pgBackRest or CloudNativePG WAL archiving, PITR), Qdrant snapshots (rebuildable from PostgreSQL + object store as the fallback), object store replication, Keycloak realm export, OpenFGA store (PostgreSQL). Restores are tested quarterly by a scripted drill.
- **Upgrades:** images pinned by digest; Renovate PRs; evaluation suite gates model and prompt changes; blue/green Qdrant collections for embedding-model changes.
- **Scaling knobs:** API replicas (CPU), ingestion workers (queue depth), vLLM replicas (GPU), Qdrant shards and replicas, PostgreSQL read replica for analytics.
- **CI/CD:** GitHub Actions: build, unit and integration tests (Testcontainers), OpenFGA model tests, ACL-leak suite, evaluation (small corpus), container build, SBOM (Syft), scan (Trivy/Grype), sign (cosign), Helm lint, OpenTofu validate/plan.

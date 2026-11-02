# Enterprise Knowledge Context Platform (EKC)

A self-hosted "enterprise AI brain". It ingests an organisation's documents, wikis, tickets, databases and source code. It models them as searchable content *and* as a knowledge graph of domains, applications, services, APIs and data. It answers questions and assists developers in VS Code with permission-aware, cited answers from **self-hosted** LLMs. External LLMs and the web are optional, policy-gated, and used only to fill knowledge gaps after review.

> **Status: v0.1 works.** The `ekc` CLI and MCP server build a system map of .NET estates from source and answer *"how does this flow?"* and *"what breaks if this changes?"* with file:line evidence, including a PR blast-radius GitHub Action. Start with **[Getting started](docs/getting-started.md)**.

```bash
dotnet run --project src/Ekc.Cli -- map samples/fabrikam
dotnet run --project src/Ekc.Cli -- flow "Submit a claim"
dotnet run --project src/Ekc.Cli -- impact ClaimValidator.EnsureWithinCoverage
dotnet run --project src/Ekc.Cli -- mcp            # for Copilot / Cursor / Claude Code / Continue
```

| Path | What |
|---|---|
| `src/Ekc.Graph` | system graph model, ids, resolver, JSON store |
| `src/Ekc.Extractors` | Roslyn (syntax-only) extractors: Aspire/Compose services, endpoints, HTTP/gRPC calls, events, dispatch, EF tables, call graph, rules; CODEOWNERS, OpenAPI, overlay |
| `src/Ekc.Analysis` | flow tracer, impact analyzer, service describer, diff mapper, Markdown/Mermaid/Structurizr output |
| `src/Ekc.Cli` | `ekc` command-line tool and MCP server |
| `tests/Ekc.Tests` | unit + end-to-end tests on the Fabrikam sample |
| `samples/fabrikam` | sample insurance estate (6 services) |
| `samples/eshop` | overlay + instructions for dotnet/eShop |
| `.github/actions/ekc-impact` | PR blast-radius action |
| `scripts/demo-setup.sh`, `scripts/demo.sh` | one-time demo preparation and guided walkthrough |

Next: an open accuracy benchmark for impact analysis, broader .NET framework coverage, then multi-repo maps and Java (see the [implementation plan](docs/implementation-plan.md)).

## Documents

**EKC as built (v0.1)**

| | |
|---|---|
| [Product plan](docs/product-plan.md) | vision, problem, users, principles, scope by release, success metrics |
| [Architecture (as built)](docs/architecture/ekc-architecture.md) | C4 context/container/component/code diagrams, graph schema, runtime views, algorithms, decisions, limits |
| [C4 model (Structurizr DSL)](docs/architecture/ekc-workspace.dsl) | the same model for Structurizr Lite |
| [Implementation plan](docs/implementation-plan.md) | v0.1 → v1.0 work packages, acceptance criteria, estimates, risks |
| [Development guide](docs/development-guide.md) | build, test, layout, conventions, adding extractors/tools/commands, release |
| [Getting started](docs/getting-started.md) | install, map, flow, impact, PR action, MCP setup, overlay, limits |
| [Demo runbook](docs/demo-runbook.md) | POC demo: setup, 10-minute script, Q&A, fallbacks |

**Target platform (vision)**

| | |
|---|---|
| [1. Context, goals and principles](docs/architecture/01-context-and-principles.md) | goals, non-goals, principles, quality targets |
| [2. C4 model](docs/architecture/02-c4-model.md) | system context, containers, components, runtime sequences |
| [3. Technology stack](docs/architecture/03-technology-stack.md) | choices, versions, licences, model catalogue per hardware profile |
| [4. Knowledge model](docs/architecture/04-knowledge-model.md) | trust tiers, provenance, content schema, knowledge graph |
| [5. Retrieval and enrichment](docs/architecture/05-retrieval-and-enrichment.md) | query pipeline, internal-first enrichment, injection defences, agent mode, evaluation |
| [6. Security](docs/architecture/06-security.md) | threat model, Keycloak/Entra, RBAC + OpenFGA ReBAC/ABAC, isolation, secrets, audit |
| [7. Code intelligence and VS Code](docs/architecture/07-code-intelligence-and-vscode.md) | repository indexing, code assistant APIs, extension design |
| [8. Deployment and operations](docs/architecture/08-deployment-and-operations.md) | profiles, repo layout, IaC, laptop memory budget, ops |

## At a glance

- **Backend:** .NET 10, Microsoft.Extensions.AI, Microsoft Agent Framework, MCP C# SDK.
- **Data:** PostgreSQL 18 (system of record + knowledge graph), Qdrant 1.20 (hybrid vectors), SeaweedFS, NATS JetStream.
- **Models:** Ollama, vLLM or llama.cpp (OpenAI-compatible API); embeddings and reranking via TEI or Ollama.
- **Security:** Keycloak (federates Entra ID), OpenFGA, PostgreSQL RLS, OpenBao, egress gateway, hash-chained audit.
- **Clients:** VS Code extension (TypeScript), React portal, MCP.
- **Ops:** Docker Compose profiles, Helm, OpenTofu, OpenTelemetry → Prometheus/Loki/Tempo/Grafana, Langfuse.

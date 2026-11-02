# EKC product plan

## Vision

Every engineer and every AI assistant should know how the system really works (which services, calls, events and data a business transaction touches) and what a change will break *before* it ships. EKC is an open-source, self-hosted system map that provides this from source code, with evidence.

## Problem

| Pain | Today |
|---|---|
| Changes break other services | Impact is found in production or in long review meetings; knowledge sits with a few people |
| Nobody can explain an end-to-end flow | Onboarding and incidents rely on stale diagrams and wiki pages |
| AI assistants guess | Copilot-style tools see files, not the system; they invent integrations |
| Architecture documentation is always out of date | Audits and modernisation projects start by rediscovering the estate |
| Code can't leave the network | Many SaaS context tools are blocked in regulated organisations |

## Users

| Persona | Needs | EKC gives |
|---|---|---|
| **Developer** | "What breaks if I change this?" "How does this request flow?" | `ekc impact`, `ekc flow`, PR blast-radius comment, answers inside their AI assistant |
| **Reviewer / tech lead** | Know a PR's real reach and who must review | risk level, affected services, owners to notify |
| **Architect** | Current, trustworthy view of the estate; impact of business-rule changes | service profiles, C4 diagrams, processes and rules linked to code |
| **Platform / DevEx team** | Roll out safely, no new SaaS | CLI + GitHub Action + MCP, runs on laptops and CI, no data leaves |
| **AI assistant** | Grounded context | six read-only MCP tools returning cited facts |

## Product principles

1. **Evidence or nothing.** Every fact links to file:line and carries a confidence score.
2. **Zero infrastructure first.** One CLI, one file, seconds to a map; servers are optional later.
3. **Works with the tools teams already use.** It plugs into any MCP assistant, GitHub and Mermaid/Structurizr, rather than being another chat UI.
4. **Deterministic core, AI at the edges.** The map is built without an LLM; LLMs only phrase answers.
5. **Business-to-code.** Processes and rules are first-class, linked to the code that implements them.

## Scope by release

| Release | Theme | Key capabilities | Status |
|---|---|---|---|
| **v0.1** | System map for .NET | Aspire/Compose services; minimal APIs, controllers, gRPC, YARP; HTTP/gRPC calls; events and in-process dispatch; EF tables; call graph; rules, CODEOWNERS, OpenAPI, overlay; `flow`, `impact`, `--diff`, C4 export; MCP server; PR action | **done** |
| **v0.2** | Accuracy you can measure | open impact benchmark (`ekc bench`); configuration-based URLs; more messaging frameworks (MassTransit, NServiceBus, Wolverine); Refit and named clients; local type inference; incremental mapping | next |
| **v0.3** | Team and multi-repo | multi-repository maps; `ekc serve` (HTTP API + remote MCP with OIDC); Azure DevOps task; Backstage catalog import; first Java (Spring) pack | planned |
| **v0.4** | Knowledge, not only code | architecture docs/ADRs indexed and cited in flows (local embeddings); LLM-suggested processes kept quarantined until approved | planned |
| **v1.0** | Production-ready | PostgreSQL store, Helm/OpenTofu, permissions (OpenFGA), Confluence/SharePoint links, TypeScript/Python packs, signed releases | planned |

Out of scope for now: replacing coding assistants, runtime tracing/APM, training models.

## Success metrics

| Metric | v0.1 baseline | v0.2 target |
|---|---|---|
| Time from install to first flow answer | < 5 min on a laptop | < 5 min |
| Map time for a ~500-file estate | 2.5–10 s | < 10 s cold |
| Impact recall / precision on the public benchmark | not measured | ≥ 0.8 / ≥ 0.7 |
| Cross-service calls resolved to an endpoint (eShop) | 13 of 15 (2 runtime-built URLs reported) | ≥ 95% on benchmark estates |
| Active usage at pilot teams | — | blast-radius comments on most PRs; MCP tool calls weekly |

## Delivery

The engineering breakdown, with work packages, acceptance criteria and estimates, is in the [implementation plan](implementation-plan.md). The architecture is in [`architecture/ekc-architecture.md`](architecture/ekc-architecture.md).

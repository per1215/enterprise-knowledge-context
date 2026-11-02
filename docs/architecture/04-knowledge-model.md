# 4. Knowledge model

EKC keeps three linked representations of the same knowledge:

1. **Content**: documents → immutable versions → chunks (for retrieval and citations).
2. **Graph**: typed entities and relationships (for flows, dependencies and impact).
3. **Permissions**: who can see which source, document, repository, domain or node (for every read).

## 4.1 Trust tiers and provenance

| Tier | Origin | May be indexed | Ranking weight | Can be overridden by |
|---|---|---|---|---|
| `Authoritative` | internal sources flagged as authoritative (e.g. architecture repo, policy library) | yes | highest | a newer version of the same source only |
| `Internal` | any other internal source | yes | high | Authoritative |
| `Verified` | AI-enriched or external content approved by a steward | yes | medium | Internal, Authoritative |
| `Quarantined` | AI-enriched or external content awaiting review | **no** (shown only as "unverified" in the enrichment flow) | — | — |
| `Rejected` | reviewed and refused | no | — | — |

Rules (enforced in the domain model, not just the UI):

- Original content is immutable; changes create new versions.
- External or AI content is stored separately and links to what it enriches; it never edits internal content in place.
- When a Verified item conflicts with Internal or Authoritative content on the same entity or fact, the internal one is used and the conflict is flagged for a steward.
- Every chunk, edge and answer carries a provenance record: source, version, ingestion run, extractor, model (if any), approver (if any).

## 4.2 Content schema (PostgreSQL, abbreviated)

| Table | Key columns |
|---|---|
| `tenants`, `teams` | isolation boundary; teams own sources and domains |
| `sources` | type, connector config (no secrets), `secret_ref` (OpenBao path), trust tier, classification, sync schedule, cursor |
| `documents` | `(source_id, external_id)` unique, title, uri, content type, language, domain, classification, `acl_hash` |
| `document_versions` | sha256, object key, size, extracted markdown, extractor version, captured_at |
| `chunks` | version, ordinal, kind (text/table/code/symbol), heading path, token count, tier, `vector_id`, embedding model |
| `document_acl` | principal (user/group/team/everyone-in-tenant), permission, source-derived flag |
| `enrichment_items` | gap question, origin (llm/web), raw content, validation result, status, reviewer, links |
| `ingestion_runs`, `jobs` | counts, errors, timings, leases |
| `conversations`, `messages`, `citations`, `feedback` | answer lineage and quality signals |
| `audit_events` | append-only, hash-chained for tamper evidence |

**Qdrant point payload:** `tenant_id`, `source_id`, `document_id`, `version`, `chunk_id`, `tier`, `domain`, `kind`, `language`, `classification`, `acl_principals[]` (keyword index), `updated_at`. `tenant_id` uses Qdrant's tenant-optimised payload index. Each point has a dense vector plus a sparse vector (BM25-style or bge-m3 sparse) for hybrid search.

## 4.3 Knowledge graph

A property graph stored in PostgreSQL (`kg_nodes`, `kg_edges`, `kg_node_aliases`, `kg_provenance`), with typed nodes and edges, temporal validity (`valid_from`, `valid_to`) and confidence.

### Node types

| Layer | Types |
|---|---|
| Business | `BusinessDomain`, `Capability`, `BusinessProcess`, `ProcessStep`, `BusinessRule`, `BusinessEntity` (e.g. Order, Policy), `Actor` |
| Application | `Application`, `Service`, `Api`, `Endpoint`, `Event`/`Topic`, `Queue`, `ScheduledJob`, `ExternalSystem` |
| Data | `DataStore`, `Schema`, `Table`, `Column`, `StoredProcedure`, `DataFlow` |
| Code | `Repository`, `Project`/`Module`, `Package`, `Namespace`, `Type`, `Method`, `ConfigKey` |
| Organisation | `Team`, `Owner`, `Environment` |
| Knowledge | `Document`, `Decision` (ADR), `Incident`, `Ticket` |

### Edge types (examples)

`PART_OF`, `IMPLEMENTS` (Service → Capability), `EXPOSES` (Service → Endpoint), `CALLS` (Service/Method → Endpoint/Method), `PUBLISHES`/`SUBSCRIBES` (Service → Topic), `READS`/`WRITES` (Service/Method/Proc → Table), `DEPENDS_ON` (Project → Package), `DEFINED_IN` (Type → Repository), `ENFORCES` (Method → BusinessRule), `STEP_OF`/`NEXT` (ProcessStep), `DOCUMENTED_BY` (any → Document), `OWNED_BY` (any → Team), `DEPLOYED_TO`.

### How the graph is built (most to least reliable)

1. **Deterministic extraction** (confidence 1.0): OpenAPI/AsyncAPI specs, database catalogues, project files (`.csproj`, `pom.xml`, `package.json`), SCIP and Roslyn references, HTTP client registrations, message-broker configuration, IaC (Helm, Terraform), CODEOWNERS.
2. **Pattern rules** (0.8–0.9): framework conventions such as ASP.NET controllers → endpoints, `[Topic]` attributes, EF `DbSet` → table, Spring annotations, outbound `HttpClient` base URLs matched to service endpoints.
3. **LLM-assisted extraction** (≤ 0.7, quarantined until it is confirmed by a rule or a steward): business processes, rules and flows from documents and tickets, using constrained JSON output against the node/edge schema.
4. **Human curation** in the portal: confirm, merge, rename or delete nodes and edges.

### What the graph enables

| Question | Graph operation |
|---|---|
| "Explain the end-to-end flow of *Place Order*" | `BusinessProcess` → ordered `ProcessStep`s → implementing `Service`/`Endpoint` → `CALLS`/`PUBLISHES` chain → `WRITES` tables; each hop cited |
| "How does a payment move across services?" | path search from entry `Endpoint` via `CALLS`/`PUBLISHES`/`SUBSCRIBES` with depth limit |
| "Impact of changing rule *Max credit limit*" | reverse traversal from `BusinessRule` ← `ENFORCES` ← methods → owning services → consumers of their endpoints/topics → owning teams |
| "Generate the HLD/LLD for service X" | subgraph around X + documents → templated generation + Mermaid C4/sequence diagrams |

Graph reads are permission-filtered: a node is visible only if the user can read at least one of its provenance sources.

## 4.4 Classification and metadata

Each document and chunk gets: knowledge domain (from source defaults + classifier), document type (ADR, runbook, spec, how-to, ticket, code), language (natural or programming), sensitivity (`Public`, `Internal`, `Confidential`, `Restricted`), freshness (last change, last verified), and quality signals (feedback, citation frequency, staleness).

`Restricted` content never leaves the self-hosted perimeter: the enrichment service refuses it outright, so no policy configuration can send it out.

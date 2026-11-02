# EKC implementation plan

From v0.1 (done) to v1.0. Each release lists work packages (WP) with acceptance criteria. Estimates are in engineer-weeks for one developer working with AI assistance; they are planning figures, not commitments.

**Conventions:** every WP ships with tests (unit + Fabrikam/eShop end-to-end where relevant), docs updates, and a changelog entry; `main` is always releasable.

## v0.1: system map for .NET (done)

| WP | Delivered | Evidence |
|---|---|---|
| 0.1-1 Graph core | node/edge model, stable ids, resolver, JSON store | `src/Ekc.Graph`, unit tests |
| 0.1-2 .NET extractors | Aspire/Compose services, endpoints (minimal APIs, controllers, gRPC, YARP), HTTP/gRPC calls, events, dispatch, EF tables, call graph, rules, CODEOWNERS, OpenAPI, overlay | `src/Ekc.Extractors`, extraction tests |
| 0.1-3 Analysis | flow tracer, impact analyzer, diff mapper, renderers (Markdown/Mermaid/Structurizr) | `src/Ekc.Analysis`, analysis tests |
| 0.1-4 Interfaces | `ekc` CLI, MCP server (6 tools), PR action, CI | `src/Ekc.Cli`, `.github/` |
| 0.1-5 Samples and demo | Fabrikam estate, eShop overlay, demo scripts and runbook | `samples/`, `scripts/`, `docs/demo-runbook.md` |

## v0.2: accuracy you can measure (≈ 6 weeks)

| WP | Scope | Acceptance criteria | Est. |
|---|---|---|---|
| 0.2-1 **Impact benchmark** | `bench/` dataset: seeded changes on dotnet/eShop + 2 more OSS .NET microservice repos, each with hand-verified expected impact; `ekc bench` computes recall/precision per category (endpoints, callers, consumers, tables) | dataset ≥ 60 cases; report in CI; numbers published in README | 1.5 |
| 0.2-2 Configuration URLs | resolve base addresses and service names from `appsettings*.json`, environment variables in Aspire/Compose, `IConfiguration["..."]` lookups | eShop runtime-built calls reduced; new tests with config-driven clients | 1 |
| 0.2-3 Messaging frameworks | MassTransit (`Publish<T>`, `IConsumer<T>`, `Send`), NServiceBus (`IHandleMessages<T>`), Wolverine, Azure Service Bus/Kafka topic names | fixture per framework; topics link publishers to consumers | 1 |
| 0.2-4 Client patterns | Refit interfaces, named `HttpClient`s, `IHttpClientFactory.CreateClient("x")`, Dapr service invocation | fixtures; calls resolve to endpoints | 0.5 |
| 0.2-5 Local type inference | infer `var x = new T()`, `var x = await repo.GetAsync()` via return types; DI registrations (`AddScoped<IFoo, Foo>`) to resolve interface calls | Invokes recall up on benchmark without precision loss | 1 |
| 0.2-6 Incremental mapping | cache per-file syntax results by content hash; `ekc map` re-parses only changed files | second map of eShop < 1 s | 0.5 |
| 0.2-7 Packaging | publish `Ekc.Cli` to NuGet; signed GitHub release with SBOM; action uses the released tool | `dotnet tool install -g Ekc.Cli` works | 0.5 |

**Exit criteria:** benchmark recall ≥ 0.8 and precision ≥ 0.7 for impact; at least 2 external estates mapped by design partners.

## v0.3: teams and multi-repo (≈ 8 weeks)

| WP | Scope | Acceptance criteria | Est. |
|---|---|---|---|
| 0.3-1 Multi-repo maps | `ekc map repoA repoB …` or `ekc merge`; services matched across repos by Aspire/Compose names, OpenAPI, base addresses | cross-repo flow in a split Fabrikam fixture | 1.5 |
| 0.3-2 `ekc serve` | HTTP API over the graph + MCP streamable HTTP; graph reload on change | assistants connect remotely; integration tests | 1.5 |
| 0.3-3 Authentication | OIDC (Keycloak/Entra) for `serve`; read-only API keys for CI | unauthenticated requests rejected; tests with a dev Keycloak | 1 |
| 0.3-4 Azure DevOps | pipeline task equivalent to the GitHub action; PR thread comments | demo pipeline green | 0.5 |
| 0.3-5 Backstage import | read `catalog-info.yaml`: systems, components, APIs, owners, domains | Fabrikam catalog fixture enriches graph | 1 |
| 0.3-6 Java pack (Spring) | tree-sitter Java: `@RestController`, `@*Mapping`, `RestTemplate`/`WebClient`/Feign, `@KafkaListener`, JPA entities | Spring sample estate mapped; benchmark subset | 2.5 |

## v0.4: knowledge, not only code (≈ 6 weeks)

| WP | Scope | Acceptance criteria | Est. |
|---|---|---|---|
| 0.4-1 Document index | ADRs, READMEs, `docs/**/*.md`, OpenAPI descriptions chunked and embedded locally (Ollama); linked to services/endpoints by mention | flows cite relevant design docs | 2 |
| 0.4-2 `explain` | `ekc explain <target>`: LLM summary grounded only on graph facts + cited docs; refuses without evidence | answers carry citations; eval set passes | 1.5 |
| 0.4-3 Suggested processes | LLM proposes process steps from docs and flows; stored as quarantined overlay suggestions; approve via PR | no suggestion enters the graph without a merged overlay change | 1.5 |
| 0.4-4 Quality evaluation | golden questions per sample estate; citation-faithfulness checks in CI | eval runs in CI | 1 |

## v1.0: production-ready (≈ 10 weeks)

| WP | Scope | Acceptance criteria | Est. |
|---|---|---|---|
| 1.0-1 PostgreSQL store | graph in PostgreSQL (nodes/edges tables, recursive CTE traversal); JSON remains for CLI | parity tests between stores | 2 |
| 1.0-2 Permissions | OpenFGA: repository/service-level access mirrored from Git hosting; queries filtered per user | ACL leak tests pass | 2 |
| 1.0-3 Deployment | Docker image, Helm chart, OpenTofu examples (k3s, Azure); health checks; OpenTelemetry | staging deploy from code | 2 |
| 1.0-4 Language packs | TypeScript (NestJS/Express), Python (FastAPI) | sample estates mapped | 3 |
| 1.0-5 Hardening | threat model, pen-test checklist, signed images, SBOMs, performance test on a 5,000-file estate | report published | 1 |

## Cross-cutting

| Area | Approach |
|---|---|
| Testing | unit tests per extractor/analyzer; end-to-end on `samples/`; benchmark in CI from v0.2; MCP smoke test over stdio |
| Compatibility | graph schema versioned (`SchemaVersion`); additive changes only within a major version |
| Performance budget | map ≤ 10 s per 500 files cold; queries ≤ 200 ms on a laptop |
| Security | read-only by design; no code leaves the machine; secrets never stored in the graph |
| Documentation | architecture as-built updated with every release; ADR rows for each new decision |

## Risks

| Risk | Mitigation |
|---|---|
| Real codebases are messier than samples (config-driven routing, reflection) | benchmark on real OSS estates early (v0.2); report unresolved links instead of guessing |
| Framework diversity (messaging, HTTP clients) | pattern library with fixtures; contributions per framework |
| Graph size in very large monorepos | incremental mapping; PostgreSQL store at v1.0 |
| MCP client differences | integration smoke tests per client; stick to the stable MCP spec |

# Getting started with EKC 0.1

EKC builds a **system map** of a .NET estate from source code, with no build, restore, database or GPU needed. It answers two questions with evidence (file:line):

- **How does this flow?** A request or event, across services, events and data.
- **What could break if this changes?** Endpoints, callers in other services, event consumers, shared tables, business rules, processes and owners.

It serves those answers to people (CLI, PR comments, diagrams) and to AI assistants (MCP).

## 1. Install

Requirements: .NET 10 SDK, git.

```bash
git clone <this repository> ekc && cd ekc
dotnet pack src/Ekc.Cli -c Release -o ./nupkg
dotnet tool install --global Ekc.Cli --add-source ./nupkg --version 0.1.0
ekc --help
```

Or run it without installing: `dotnet run --project src/Ekc.Cli -- <command>`.

## 2. Map a repository

```bash
cd /path/to/your/estate
ekc map .
```

```
Mapped 19 projects (474 files) in 2.5s → .ekc/graph.json
  services 9 · endpoints 45 · events 13 · tables 9 · methods 332
  rules 3 · processes 3 · teams 0 · edges 660
  2 call(s) resolved to a service but not to an endpoint (URL built at runtime)
```

(This output is from dotnet/eShop. See `samples/eshop`.)

## 3. Ask questions

```bash
ekc services                                         # table of services
ekc describe ordering-api                            # endpoints, calls, callers, events, tables, owners
ekc flow "ordering-api: POST /api/orders"            # how a request flows
ekc flow "topic: GracePeriodConfirmedIntegrationEvent"
ekc flow "Place an order"                            # a business process from the overlay
ekc flow "POST /api/orders" --format mermaid         # sequence diagram
ekc impact Order.SetPaidStatus                       # what could break
ekc impact src/Ordering.Domain/AggregatesModel/OrderAggregate/Order.cs:120
ekc impact "table: orders"                           # who reads/writes it
ekc search Payment
ekc export c4 --format mermaid                       # container diagram
ekc export c4 --format structurizr --name eShop      # C4 model for Structurizr
```

References accepted everywhere: `[service:] VERB /path`, `topic: Name`, `table: Name`, `rule: Name`, `process: Name`, `Type.Method`, `path/File.cs[:line]`, a service name, or free text.

## 4. Pull-request blast radius

```bash
ekc impact --diff origin/main...HEAD --pr-comment --fail-on high
```

In GitHub Actions, copy [`docs/examples/ekc-pr-impact.yml`](examples/ekc-pr-impact.yml). The composite action at `.github/actions/ekc-impact` builds EKC, maps the PR head, comments (and updates) the blast radius on the PR, writes it to the job summary, and can fail the check at a chosen risk level. Exit code `3` means the threshold was reached.

## 5. Use it from your AI assistant (MCP)

`ekc mcp` runs a stdio MCP server with six read-only tools: `list_services`, `describe_service`, `trace_flow`, `impact_of_change`, `find_owners` and `search_system`. Point your client at it:

| Client | Configuration |
|---|---|
| VS Code (Copilot agent mode), `.vscode/mcp.json` | `{"servers": {"ekc": {"type": "stdio", "command": "ekc", "args": ["mcp", "-g", "${workspaceFolder}/.ekc/graph.json"]}}}` |
| Continue, `config.yaml` | `mcpServers:` → `- name: ekc` / `command: ekc` / `args: ["mcp", "-g", ".ekc/graph.json"]` (MCP tools are used in agent mode) |
| Claude Code | `claude mcp add ekc -- ekc mcp -g .ekc/graph.json` |
| Cursor, `.cursor/mcp.json` | `{"mcpServers": {"ekc": {"command": "ekc", "args": ["mcp", "-g", ".ekc/graph.json"]}}}` |

Configuration formats change between client versions; check your client's MCP documentation if a key is rejected. Then ask: *"Using ekc, how does a claim payment flow, and what breaks if I change ClaimValidator?"*

## 6. Add business knowledge (overlay)

Code can't tell EKC what a business process is. Add `.ekc/overlay.yaml`:

```yaml
domains:
  - name: Claims
    services: [claims-api, fraud-worker, payments-api]
rules:
  - name: Fraud score above 0.8 rejects the claim
    enforcedBy: [FraudScorer.Score]
processes:
  - name: Submit a claim
    steps:
      - "portal-web: POST /claims"
      - "claims-api: POST /api/claims"
      - "topic: ClaimSubmittedIntegrationEvent"
```

Rules can also live in code: `[BusinessRule("Name")]` on a method, or a `// ekc:rule Name` comment directly above it. Ownership comes from `CODEOWNERS`. Unresolved overlay references are reported as warnings by `ekc map`.

## 7. What EKC understands (v0.1, .NET)

| Area | Detected from |
|---|---|
| Services, infrastructure, orchestration dependencies | .NET Aspire AppHost (`AddProject`, `AddDotnetProject`, `WithReference`, databases, brokers, YARP), docker-compose (`build`, `depends_on`), executable projects |
| Endpoints | minimal APIs with `MapGroup` prefixes (method groups, lambdas, local functions), MVC controllers (`[Route]`, `[Http*]`, `[controller]`), gRPC services (`MapGrpcService`), YARP `MapForwarder`, OpenAPI files |
| Service-to-service calls | typed `HttpClient`s with service-discovery base addresses, URL templates from literals/interpolation/constants, `HttpRequestMessage`, gRPC clients (`AddGrpcClient`), using-aliases |
| Events | integration-event types, `AddSubscription<TEvent, THandler>`, `IIntegrationEventHandler<T>`, `IConsumer<T>`, publishing by event creation |
| In-process dispatch | commands/queries/notifications/domain events to `IRequestHandler`, `INotificationHandler` and similar handlers |
| Data | EF Core `DbContext`/`DbSet`, `ToTable` (configurations and `Entity<T>()`), reads vs writes, table names in raw SQL |
| Calls inside a service | receiver-type resolution (fields, constructor/primary-constructor parameters, lambda parameters), unique-name fallback |
| Ownership, rules, processes, domains | CODEOWNERS, `[BusinessRule]`, `// ekc:rule`, overlay |

**Known limits.** Analysis is syntax-only, so some links are heuristic and carry a confidence below 1.0:
- URLs built at runtime or read from configuration resolve to the service, not the endpoint.
- Reflection, dynamic dispatch and DI-registered interface implementations with several candidates lower confidence.
- `var` locals have no known type.

Java, TypeScript and Python support are planned.

# Development guide

## Prerequisites

- .NET 10 SDK (10.0.402 or later; see `global.json`)
- git
- Optional: VS Code with the C# extension and *Markdown Preview Mermaid Support*

## Build, test, run

```bash
dotnet build Ekc.slnx
dotnet test --solution Ekc.slnx                                   # 47 tests
dotnet test --project tests/Ekc.Tests --filter-class "*AnalysisTests"
dotnet run --project src/Ekc.Cli -- map samples/fabrikam -g /tmp/fab.json
dotnet run --project src/Ekc.Cli -- flow "Submit a claim" -g /tmp/fab.json
```

Install the local build as the `ekc` command:

```bash
dotnet pack src/Ekc.Cli -c Release -o ./nupkg
dotnet tool update --global Ekc.Cli --add-source ./nupkg --version 0.1.0
```

## Repository layout

```
Ekc.slnx                     solution
global.json                  SDK pin + Microsoft.Testing.Platform runner
Directory.Build.props        net10.0, nullable, lock files, NuGet audit
Directory.Packages.props     central package versions
src/
  Ekc.Graph/                 model, NodeIds, SystemGraph, GraphResolver, RouteMatcher, GraphStore (no dependencies)
  Ekc.Extractors/
    DotNet/                  ProjectLoader, CodeModel, SyntaxHelpers, ServiceDiscovery, EndpointExtractor,
                             HttpClientExtractor, MessagingExtractor, DataAccessExtractor, InvocationExtractor, RuleExtractor
    Common/                  RepositoryFiles, CodeOwners, OpenApi, Overlay
    GraphBuilder.cs          extractor pipeline
  Ekc.Analysis/              FlowTracer, ImpactAnalyzer, ServiceDescriber, DiffMapper, MarkdownRenderer, DiagramExporter
  Ekc.Cli/                   Program.cs (commands), McpTools.cs (MCP server)
tests/Ekc.Tests/             UnitTests, ExtractionTests, AnalysisTests (Fabrikam fixture)
samples/fabrikam/            sample estate (not built, only read)
samples/eshop/               overlay for dotnet/eShop
scripts/                     demo-setup.sh, demo.sh
.github/                     CI workflow, PR blast-radius action
docs/                        architecture (as built + target), plans, guides
```

See [`architecture/ekc-architecture.md`](architecture/ekc-architecture.md) for how the pieces fit.

## Conventions

- **Determinism first.** Extractors add facts they can point to. Each edge gets `Evidence(file, line, extractor, snippet)` and an honest confidence (see the edge table in the architecture doc). Never add an edge you cannot cite.
- **Ids are contracts.** Use `NodeIds.*` only; changing an id format is a schema change and needs `SchemaVersion` + migration notes.
- **Syntax only.** No `Compilation`/`SemanticModel`; resolve types from declarations (`CodeType.MemberTypes`, `CodeMethod.ParameterTypes`), using-aliases, and naming. Keep extractors fast and tolerant of code that doesn't compile.
- **Body traversal:** use `CodeMethod.BodyNodes()` (it skips lambdas and local functions modelled as separate methods), not `Body.DescendantNodes()`.
- **Style:** file-scoped namespaces, nullable enabled, `sealed` by default, source-generated regexes, no warnings (CI builds with `ContinuousIntegrationBuild=true`, which treats warnings as errors).
- **Packages:** add versions only in `Directory.Packages.props`; lock files are committed (`dotnet restore --locked-mode` in CI).

## How to …

### Add an extractor (e.g. a new messaging framework)

1. Add a fixture: extend `samples/fabrikam` (or add `samples/<name>`) with the smallest realistic code using the pattern.
2. Write a failing test in `tests/Ekc.Tests/ExtractionTests.cs` asserting the expected nodes and edges.
3. Implement in `src/Ekc.Extractors/DotNet/<Area>Extractor.cs`:
   - iterate `context.Services` → `context.Scope(service)` → `scope.Methods`/`scope.Types`;
   - create nodes with `NodeIds`, and edges with `context.MethodNodeId(method)` as the source;
   - attach `Evidence` and a confidence.
4. Register it in `GraphBuilder.Build` in the right order (after anything it depends on).
5. Run the eShop map and compare counts (`ekc map samples/external/eshop`) to catch regressions.

### Add a node or edge kind

Add it to `NodeKind`/`EdgeKind` (`src/Ekc.Graph/Model.cs`), document it in the architecture doc's tables, then decide how `FlowTracer` and `ImpactAnalyzer` should traverse it, with a test for each.

### Add an MCP tool

Add a method to `McpTools` with `[McpServerTool(Name = "...", ReadOnly = true, Idempotent = true)]` and a `[Description]` that tells the model *when* to use it. Return Markdown with evidence. Add an assertion in `AnalysisTests.Mcp_tools_answer_with_evidence`.

### Add a CLI command

Add a `Command` in `src/Ekc.Cli/Program.cs`, reusing `graphOption` (a recursive option). Keep output Markdown-friendly; exit codes: `0` ok, `2` not found, `3` risk threshold.

## Debugging

```bash
ekc map <repo> -g /tmp/g.json
jq '.nodes | to_entries | map(.value.kind) | group_by(.) | map({(.[0]): length}) | add' /tmp/g.json
jq '.edges[] | select(.kind=="CallsService")' /tmp/g.json        # unresolved calls
jq '.warnings' /tmp/g.json                                        # overlay or extraction warnings
ekc search "<name>" -g /tmp/g.json                                # what does a reference resolve to?
```

MCP over stdio by hand:

```bash
printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | ekc mcp -g /tmp/g.json
```

## Release process

1. Update `VersionPrefix` in `Directory.Build.props`, `ServerInfo.Version` in `Program.cs`, and `CHANGELOG` notes in the release description.
2. `dotnet test --solution Ekc.slnx -c Release`.
3. Tag: `git tag -a vX.Y.Z -m "EKC vX.Y.Z"` and `git push origin vX.Y.Z`.
4. Create a GitHub release with the packed `.nupkg` (`dotnet pack src/Ekc.Cli -c Release`).

## Contributing

Open an issue describing the pattern or problem with a minimal code sample. Pull requests need tests, docs updates and green CI, and are reviewed against the conventions above. By contributing you agree your work is licensed under Apache-2.0.

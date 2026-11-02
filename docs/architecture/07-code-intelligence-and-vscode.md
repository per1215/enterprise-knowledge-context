# 7. Code intelligence and the VS Code extension

## 7.1 Repository ingestion

| Stage | Detail |
|---|---|
| Discover | Git connector: org/project allow-list, branch policy (default branch + release branches), webhooks for push and PR merge, or polling |
| Snapshot | shallow fetch at commit SHA → object storage; skip vendored code, binaries and generated files (`.ekcignore`, `.gitattributes linguist-generated`) |
| Parse | **tree-sitter** for every supported language (C#, Java, Python, JS/TS, Go, SQL, YAML, Terraform...) → files, symbols, imports, signatures, doc comments |
| Precise index | **SCIP** indexers where available (scip-dotnet, scip-typescript, scip-python) and **Roslyn** for C# semantic analysis → definitions, references, type hierarchy, cross-project and cross-repository links |
| Project model | `.csproj`/`.sln(x)`, `pom.xml`/Gradle, `package.json`, `pyproject.toml` → modules and package dependencies (with versions) |
| Surfaces | ASP.NET/Spring/Express/FastAPI routes → `Endpoint`; OpenAPI files; message attributes and configuration → `Topic`; EF/JPA/SQL → `Table`; HttpClient and Feign base URLs → `CALLS` |
| Chunk and embed | per symbol (class/method) with a header: repo · path · symbol · signature; README and docs as text |
| Quality signals | test coverage files, analyzer/SARIF results, CODEOWNERS → owners |
| Incremental | only changed files are re-parsed per commit; symbol ids are stable (SCIP monikers); deleted symbols are tombstoned |

**"Learning" from approved changes.** On PR merge, EKC indexes the diff, the PR description and review comments as knowledge (`Decision`/`Change` nodes linked to the symbols touched), and refreshes standards examples ("approved patterns") from repositories marked authoritative. No model weights change. Optional later: periodic LoRA adapters trained on approved code (Phase 12, opt-in).

## 7.2 Code assistant capabilities (server side)

| Capability | Endpoint | Notes |
|---|---|---|
| Inline completion (FIM) | `POST /api/v1/code/complete` | small base coder model; retrieved definitions for symbols in the prefix; 64–128 tokens; cached |
| Chat with repo context | `POST /api/v1/chat` (SSE) | workspace descriptor + selected files + retrieval |
| Explain / review / refactor / optimise | `POST /api/v1/code/actions/{kind}` | returns Markdown + optional unified diff |
| Generate tests | `.../actions/tests` | uses the repo's test framework, conventions and fixtures found in the index |
| Bug, security and performance review | `.../actions/review` | LLM review **plus** deterministic analyzers (Roslyn analyzers, Semgrep rules) whose findings are cited |
| Documentation | `.../actions/document` | XML/JSDoc/docstrings, README sections, HLD/LLD from the graph |
| Search | `GET /api/v1/search?q=&scope=` | docs + code + graph entities |
| MCP | `/mcp` | tools from 5.5 for any MCP-capable agent |

## 7.3 VS Code extension (`ekc-vscode`)

**Principles:** works with any VS Code build without other AI extensions; never sends code the user did not allow; every server call is authenticated with the user's identity, so ACLs apply in the IDE too.

| Feature | VS Code API |
|---|---|
| Sign-in | `AuthenticationProvider` (OIDC PKCE against Keycloak; tokens in `SecretStorage`) |
| Chat panel | `WebviewViewProvider` (React), with streaming, citations as clickable links (docs open in the browser; code opens in the editor), "Apply diff" buttons |
| `@ekc` chat participant | `vscode.chat.createChatParticipant`, when the Chat view is available; same backend |
| Inline completion | `InlineCompletionItemProvider` with debounce, cancellation and a per-language enable switch |
| Code actions | `CodeActionProvider` + commands: Explain, Refactor, Generate tests, Review, Document |
| Apply changes | `WorkspaceEdit` with diff preview (`vscode.diff`), never silent writes |
| Enterprise search | QuickPick + tree view (results grouped: Docs, Code, Services, APIs) |
| Dependencies | tree view of the current project's graph neighbourhood (callers, consumers, tables) |
| Context controls | status-bar toggle: *Local workspace only* / *Workspace + enterprise*; `.ekcignore`; send-preview of attached context |
| Telemetry | OpenTelemetry (opt-in), accept/reject rates; no code content |
| Distribution | `.vsix` from CI, signed; private marketplace or Open VSX (self-hosted) |

**Workspace context sent per request (budgeted):** repo remote URL + commit (to match the server-side index), active file path, selection or cursor window, imports, a list of open files (names), plus snippets the user attaches. When the repo is indexed server-side, the extension sends references, not content.

**Offline / local-only mode:** the extension can talk directly to a local Ollama for completion when the server is unreachable. This is disabled by tenant policy if needed.

/*
 * EKC v0.1 — C4 model (Structurizr DSL).
 * Render: docker run -it --rm -p 8080:8080 -v "$PWD/docs/architecture:/usr/local/structurizr" structurizr/lite
 * (then open http://localhost:8080). Views: SystemContext, Containers, Components.
 */
workspace "EKC" "Open-source, self-hosted system map for AI agents (v0.1, as built)" {

  model {
    developer = person "Developer" "Asks how code flows and what a change breaks; reviews PR blast radius."
    architect = person "Architect / tech lead" "Explores services, flows and diagrams; curates processes and rules."

    repo = softwareSystem "Source repositories" ".NET solutions, Aspire AppHost or docker-compose, OpenAPI, CODEOWNERS." {
      tags "External"
    }
    assistant = softwareSystem "AI assistant" "Claude Code, GitHub Copilot, Cursor, Continue (MCP clients)." {
      tags "External"
    }
    github = softwareSystem "GitHub" "Pull requests and Actions runners." {
      tags "External"
    }

    ekc = softwareSystem "EKC" "Builds a system map from source code; answers flow and impact questions with evidence." {
      cli = container "ekc CLI" "map, services, describe, flow, impact, search, export." ".NET 10 global tool" {
        commands = component "Commands" "CLI command handlers." "System.CommandLine"
        builder = component "GraphBuilder" "Runs extractors in order." "C#"
        loader = component "ProjectLoader" "csproj + C# syntax trees to a code model." "Roslyn (syntax)"
        discovery = component "ServiceDiscovery" "Aspire, docker-compose, executable projects." "Roslyn, YamlDotNet"
        endpoints = component "EndpointExtractor" "Minimal APIs, controllers, gRPC, YARP." "Roslyn"
        httpCalls = component "HttpClientExtractor" "Typed HttpClient and gRPC client calls." "Roslyn"
        messaging = component "MessagingExtractor" "Integration events and in-process dispatch." "Roslyn"
        dataAccess = component "DataAccessExtractor" "EF Core tables, reads/writes, raw SQL." "Roslyn"
        invocations = component "InvocationExtractor" "Receiver-typed call graph." "Roslyn"
        knowledge = component "Rules, CODEOWNERS, OpenAPI, Overlay" "Business and ownership knowledge." "Roslyn, YamlDotNet, System.Text.Json"
        graphModel = component "Ekc.Graph" "SystemGraph, NodeIds, GraphResolver, GraphStore." "C#"
        flowTracer = component "FlowTracer" "Forward traversal of requests, events and processes." "C#"
        impactAnalyzer = component "ImpactAnalyzer" "Upstream + downstream traversal, risk, owners." "C#"
        diffMapper = component "DiffMapper" "Unified diff to changed methods." "C#"
        renderers = component "Renderers" "Markdown, Mermaid, Structurizr." "C#"
      }
      mcp = container "MCP server" "Six read-only tools (ekc mcp)." ".NET 10, ModelContextProtocol SDK" {
        mcpTools = component "McpTools" "list_services, describe_service, trace_flow, impact_of_change, find_owners, search_system." "C#"
      }
      graphFile = container "System map" "Nodes, typed edges, evidence, confidence." "JSON file (.ekc/graph.json)" {
        tags "Database"
      }
      overlay = container "Overlay" "Domains, business processes, business rules." "YAML (.ekc/overlay.yaml)" {
        tags "File"
      }
      action = container "PR blast-radius action" "Maps the PR head and comments the impact." "GitHub composite action"
    }

    # Relationships — people and external systems
    developer -> cli "Runs ekc commands" "CLI"
    architect -> overlay "Curates" "YAML in git"
    architect -> cli "Exports diagrams" "CLI"
    developer -> assistant "Asks questions in the IDE"
    assistant -> mcp "Calls read-only tools" "MCP JSON-RPC over stdio"
    github -> action "Runs on pull_request"
    action -> cli "map + impact --diff --pr-comment"
    action -> github "Creates/updates PR comment" "gh API"

    # Relationships — containers
    cli -> repo "Parses source; reads git diff" "file system, git"
    cli -> overlay "Reads"
    cli -> graphFile "Writes (map) / reads (queries)"
    mcp -> graphFile "Loads at start"

    # Relationships — components
    commands -> builder "map"
    builder -> loader "1. load projects"
    builder -> discovery "2. services"
    builder -> endpoints "3. endpoints"
    builder -> httpCalls "4. calls"
    builder -> messaging "5. events"
    builder -> dataAccess "6. data"
    builder -> invocations "7. call graph"
    builder -> knowledge "8. rules, owners, overlay"
    builder -> graphModel "Adds nodes and edges"
    commands -> flowTracer "flow"
    commands -> impactAnalyzer "impact"
    commands -> diffMapper "impact --diff"
    flowTracer -> graphModel "Traverses"
    impactAnalyzer -> graphModel "Traverses"
    flowTracer -> renderers "Formats"
    impactAnalyzer -> renderers "Formats"
    mcpTools -> flowTracer "trace_flow"
    mcpTools -> impactAnalyzer "impact_of_change"
    mcpTools -> graphModel "list/describe/search"
  }

  views {
    systemContext ekc "SystemContext" {
      include *
      autoLayout lr
    }
    container ekc "Containers" {
      include *
      autoLayout lr
    }
    component cli "Components" {
      include *
      autoLayout lr
    }
    component mcp "McpComponents" {
      include *
      autoLayout lr
    }

    styles {
      element "Person" {
        shape person
        background #08427b
        color #ffffff
      }
      element "Software System" {
        background #1168bd
        color #ffffff
      }
      element "External" {
        background #999999
        color #ffffff
      }
      element "Container" {
        background #438dd5
        color #ffffff
      }
      element "Component" {
        background #85bbf0
        color #000000
      }
      element "Database" {
        shape cylinder
      }
      element "File" {
        shape folder
      }
    }
  }
}

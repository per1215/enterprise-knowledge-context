using System.ComponentModel;
using Ekc.Analysis;
using Ekc.Graph;
using ModelContextProtocol.Server;

namespace Ekc.Cli;

/// <summary>Holds the loaded system map for the MCP server.</summary>
public sealed class GraphSession(SystemGraph graph)
{
    public SystemGraph Graph { get; } = graph;
}

/// <summary>
/// MCP tools for AI assistants (Copilot, Cursor, Claude Code, Continue). All read-only; results are Markdown with
/// evidence (file:line) so the assistant can open the code and cite it.
/// </summary>
[McpServerToolType]
public sealed class McpTools(GraphSession session)
{
    private SystemGraph Graph => session.Graph;

    [McpServerTool(Name = "list_services", ReadOnly = true, Idempotent = true)]
    [Description("Lists the services in the system map with endpoint/event counts and owners. Start here to learn the system's shape.")]
    public string ListServices() => MarkdownRenderer.ServiceList(Graph);

    [McpServerTool(Name = "describe_service", ReadOnly = true, Idempotent = true)]
    [Description("Describes one service: endpoints and handlers, outbound calls, callers, events published/consumed, tables, infrastructure, owners, rules and business processes.")]
    public string DescribeService([Description("Service name, e.g. 'ordering-api'.")] string service)
    {
        var node = GraphResolver.Resolve(Graph, $"service: {service}").FirstOrDefault(n => n.Kind == NodeKind.Service);
        return node is null
            ? $"No service named `{service}`. Known services: {string.Join(", ", Graph.OfKind(NodeKind.Service).Select(s => s.Name))}"
            : MarkdownRenderer.Service(new ServiceDescriber(Graph).Describe(node));
    }

    [McpServerTool(Name = "trace_flow", ReadOnly = true, Idempotent = true)]
    [Description("Traces how a request or event flows through the system: handlers, internal dispatch, HTTP/gRPC calls to other services, events published and who consumes them, tables read/written. Every step has file:line evidence.")]
    public string TraceFlow(
        [Description("Where the flow starts: 'POST /api/orders', 'ordering-api: POST /api/orders', 'topic: OrderStartedIntegrationEvent', a business process name, or 'Type.Method'.")] string start,
        [Description("Optional: also return a Mermaid sequence diagram.")] bool includeDiagram = false)
    {
        var starts = GraphResolver.Resolve(Graph, start);
        if (starts.Count == 0)
        {
            return MarkdownRenderer.Nodes(starts, start);
        }

        var flow = new FlowTracer(Graph).Trace(starts.Take(3).ToList(), start);
        var text = MarkdownRenderer.Flow(flow);
        return includeDiagram ? $"{text}\n```mermaid\n{DiagramExporter.FlowSequence(flow)}```\n" : text;
    }

    [McpServerTool(Name = "impact_of_change", ReadOnly = true, Idempotent = true)]
    [Description("Answers 'what could break if this changes?': affected endpoints, callers in other services, event consumers, shared tables, business rules and processes, risk level and owners to notify.")]
    public string ImpactOfChange(
        [Description("What changes: 'Type.Method', 'path/File.cs:42', 'GET /api/catalog/items/{id}', 'topic: X', 'table: Orders', 'rule: Name'.")] string target)
    {
        var nodes = GraphResolver.Resolve(Graph, target);
        if (nodes.Count == 0)
        {
            return MarkdownRenderer.Nodes(nodes, target);
        }

        return MarkdownRenderer.Impact(new ImpactAnalyzer(Graph).Analyze(nodes.Take(10).ToList()), target);
    }

    [McpServerTool(Name = "find_owners", ReadOnly = true, Idempotent = true)]
    [Description("Finds the owning team(s) of a service, endpoint, method, file or table (from CODEOWNERS).")]
    public string FindOwners([Description("Service, endpoint, 'Type.Method', file path or table.")] string target)
    {
        var nodes = GraphResolver.Resolve(Graph, target);
        var services = nodes.SelectMany(n => n.Kind == NodeKind.Table
                ? Graph.Incoming(n.Id, EdgeKind.ReadsTable, EdgeKind.WritesTable).Select(e => Graph.Find(e.From)).OfType<Node>().SelectMany(m => m.ServicesOf())
                : n.ServicesOf())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (services.Count == 0)
        {
            return MarkdownRenderer.Nodes(nodes, target);
        }

        var owners = Graph.OwnersOf(services);
        return $"**Services:** {string.Join(", ", services)}\n\n**Owners:** {(owners.Count == 0 ? "none found in CODEOWNERS" : string.Join(", ", owners.Select(o => "@" + o)))}";
    }

    [McpServerTool(Name = "search_system", ReadOnly = true, Idempotent = true)]
    [Description("Searches the system map for services, endpoints, events, tables, business rules, processes and methods by name.")]
    public string Search([Description("Free text or a qualified reference such as 'topic: Order'.")] string query) =>
        MarkdownRenderer.Nodes(GraphResolver.Resolve(Graph, query), query);
}

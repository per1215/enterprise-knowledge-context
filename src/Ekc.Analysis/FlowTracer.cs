using Ekc.Graph;

namespace Ekc.Analysis;

public enum FlowStepKind
{
    Entry,
    Handler,
    Invoke,
    Dispatch,
    HttpCall,
    Publish,
    Consume,
    DataRead,
    DataWrite,
    Unresolved,
}

/// <param name="Depth">Nesting level for display (only significant steps increase it).</param>
/// <param name="Confidence">Product of edge confidences from the start of the flow.</param>
public sealed record FlowStep(int Depth, FlowStepKind Kind, string Service, string Description, string NodeId, double Confidence, Evidence? Evidence);

public sealed record FlowResult(string Title, IReadOnlyList<Node> Starts, IReadOnlyList<FlowStep> Steps, bool Truncated);

/// <summary>
/// Follows a request or event forward through the system: endpoint → handler → internal calls → outbound HTTP/gRPC
/// calls → published events → their consumers → data access. Internal call chains are shown only when they lead somewhere.
/// </summary>
public sealed class FlowTracer(SystemGraph graph)
{
    private static readonly EdgeKind[] MethodEdges =
    [
        EdgeKind.Dispatches, EdgeKind.CallsEndpoint, EdgeKind.CallsService, EdgeKind.Publishes,
        EdgeKind.WritesTable, EdgeKind.ReadsTable, EdgeKind.Invokes,
    ];

    public int MaxSteps { get; init; } = 120;

    public int MaxDepth { get; init; } = 14;

    public double MinConfidence { get; init; } = 0.5;

    public FlowResult Trace(IReadOnlyList<Node> starts, string? title = null)
    {
        var steps = new List<FlowStep>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var start in OrderStarts(starts))
        {
            var produced = Explore(start, depth: 0, confidence: 1.0, visited, ref truncated, isStart: true);
            steps.AddRange(produced);
        }

        return new FlowResult(title ?? string.Join(", ", starts.Select(s => s.Name)), starts, steps, truncated);
    }

    private IEnumerable<Node> OrderStarts(IReadOnlyList<Node> starts)
    {
        foreach (var start in starts)
        {
            if (start.Kind == NodeKind.BusinessProcess)
            {
                // A process expands to its ordered steps.
                foreach (var step in graph.Outgoing(start.Id, EdgeKind.ProcessStep)
                    .OrderBy(e => int.TryParse(e.Properties.GetValueOrDefault("order"), out var o) ? o : int.MaxValue))
                {
                    if (graph.Find(step.To) is { } node)
                    {
                        yield return node;
                    }
                }
            }
            else
            {
                yield return start;
            }
        }
    }

    private List<FlowStep> Explore(Node node, int depth, double confidence, HashSet<string> visited, ref bool truncated, bool isStart = false)
    {
        var result = new List<FlowStep>();
        if (depth > MaxDepth || !visited.Add(node.Id))
        {
            return result;
        }

        switch (node.Kind)
        {
            case NodeKind.Endpoint:
            {
                if (isStart)
                {
                    result.Add(new FlowStep(depth, FlowStepKind.Entry, node.ServiceOf() ?? "?", $"{node.Name}", node.Id, confidence, node.Evidence.FirstOrDefault()));
                }

                foreach (var edge in graph.Outgoing(node.Id, EdgeKind.ImplementedBy, EdgeKind.CallsEndpoint))
                {
                    if (graph.Find(edge.To) is not { } target)
                    {
                        continue;
                    }

                    if (edge.Kind == EdgeKind.CallsEndpoint)
                    {
                        result.Add(new FlowStep(depth + 1, FlowStepKind.HttpCall, node.ServiceOf() ?? "?", $"forwards to {target.ServiceOf()}: {target.Name}", target.Id, confidence * edge.Confidence, edge.Evidence.FirstOrDefault()));
                        result.AddRange(Explore(target, depth + 2, confidence * edge.Confidence, visited, ref truncated));
                    }
                    else
                    {
                        result.Add(new FlowStep(depth + 1, FlowStepKind.Handler, target.ServiceOf() ?? "?", $"handled by {target.Name}", target.Id, confidence, target.Evidence.FirstOrDefault()));
                        result.AddRange(ExploreMethodBody(target, depth + 1, confidence, visited, ref truncated));
                    }
                }

                break;
            }

            case NodeKind.Topic:
            {
                if (isStart)
                {
                    result.Add(new FlowStep(depth, FlowStepKind.Entry, "broker", $"event {node.Name}", node.Id, confidence, null));
                }

                foreach (var edge in graph.Outgoing(node.Id, EdgeKind.HandledBy))
                {
                    if (graph.Find(edge.To) is not { } handler)
                    {
                        continue;
                    }

                    var service = edge.Properties.GetValueOrDefault("service") ?? handler.ServiceOf() ?? "?";
                    result.Add(new FlowStep(depth + 1, FlowStepKind.Consume, service, $"{service} consumes {node.Name} in {handler.Name}", handler.Id, confidence * edge.Confidence, edge.Evidence.FirstOrDefault()));
                    result.AddRange(ExploreMethodBody(handler, depth + 1, confidence * edge.Confidence, visited, ref truncated));
                }

                break;
            }

            case NodeKind.Method:
            {
                if (isStart)
                {
                    result.Add(new FlowStep(depth, FlowStepKind.Entry, node.ServiceOf() ?? "?", node.Name, node.Id, confidence, node.Evidence.FirstOrDefault()));
                }

                result.AddRange(ExploreMethodBody(node, depth, confidence, visited, ref truncated));
                break;
            }

            default:
                if (isStart)
                {
                    result.Add(new FlowStep(depth, FlowStepKind.Entry, node.ServiceOf() ?? "?", node.Name, node.Id, confidence, node.Evidence.FirstOrDefault()));
                }

                break;
        }

        if (result.Count > MaxSteps)
        {
            truncated = true;
            result.RemoveRange(MaxSteps, result.Count - MaxSteps);
        }

        return result;
    }

    /// <summary>What a method does: its significant outgoing edges, recursively; internal calls kept only if they lead somewhere.</summary>
    private List<FlowStep> ExploreMethodBody(Node method, int depth, double confidence, HashSet<string> visited, ref bool truncated)
    {
        var result = new List<FlowStep>();
        visited.Add(method.Id);
        if (depth > MaxDepth)
        {
            return result;
        }

        var service = method.ServiceOf() ?? "?";
        foreach (var edge in graph.Outgoing(method.Id)
            .Where(e => MethodEdges.Contains(e.Kind) && e.Confidence >= MinConfidence)
            // Source order approximates execution order inside the method body.
            .OrderBy(e => e.Evidence.FirstOrDefault()?.Line ?? int.MaxValue)
            .ThenBy(e => Array.IndexOf(MethodEdges, e.Kind)))
        {
            if (graph.Find(edge.To) is not { } target)
            {
                continue;
            }

            var c = confidence * edge.Confidence;
            var evidence = edge.Evidence.FirstOrDefault();
            switch (edge.Kind)
            {
                case EdgeKind.CallsEndpoint:
                    result.Add(new FlowStep(depth + 1, FlowStepKind.HttpCall, service, $"{service} calls {target.ServiceOf()}: {target.Name}", target.Id, c, evidence));
                    if (!visited.Contains(target.Id))
                    {
                        result.AddRange(Explore(target, depth + 1, c, visited, ref truncated));
                    }

                    break;
                case EdgeKind.CallsService:
                    result.Add(new FlowStep(depth + 1, FlowStepKind.Unresolved, service,
                        $"{service} calls {target.Name} ({edge.Properties.GetValueOrDefault("verb")} {edge.Properties.GetValueOrDefault("path") ?? "URL built at runtime"})",
                        target.Id, c, evidence));
                    break;
                case EdgeKind.Publishes:
                    result.Add(new FlowStep(depth + 1, FlowStepKind.Publish, service, $"{service} publishes {target.Name}", target.Id, c, evidence));
                    if (!visited.Contains(target.Id))
                    {
                        result.AddRange(Explore(target, depth + 1, c, visited, ref truncated));
                    }

                    break;
                case EdgeKind.WritesTable or EdgeKind.ReadsTable:
                    var verb = edge.Kind == EdgeKind.WritesTable ? "writes" : "reads";
                    var step = new FlowStep(depth + 1, edge.Kind == EdgeKind.WritesTable ? FlowStepKind.DataWrite : FlowStepKind.DataRead, service,
                        $"{service} {verb} table {target.Name}", target.Id, c, evidence);
                    if (!result.Any(r => r.NodeId == step.NodeId && r.Kind == step.Kind && r.Depth == step.Depth))
                    {
                        result.Add(step);
                    }

                    break;
                case EdgeKind.Dispatches or EdgeKind.Invokes:
                    if (visited.Contains(target.Id))
                    {
                        break;
                    }

                    var inner = ExploreMethodBody(target, depth + 1, c, visited, ref truncated);
                    if (edge.Kind == EdgeKind.Dispatches || inner.Count > 0)
                    {
                        var label = edge.Kind == EdgeKind.Dispatches
                            ? $"dispatches {edge.Properties.GetValueOrDefault("message")} → {target.Name}"
                            : $"→ {target.Name}";
                        result.Add(new FlowStep(depth + 1, edge.Kind == EdgeKind.Dispatches ? FlowStepKind.Dispatch : FlowStepKind.Invoke, service, label, target.Id, c, evidence));
                        result.AddRange(inner);
                    }

                    break;
            }

            if (result.Count > MaxSteps)
            {
                truncated = true;
                break;
            }
        }

        return result;
    }
}

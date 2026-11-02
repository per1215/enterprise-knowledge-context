using Ekc.Graph;

namespace Ekc.Analysis;

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <param name="Path">Readable chain from the change to this item, e.g. "Order.SetPaidStatus ← SetPaidOrderStatusCommandHandler.Handle".</param>
public sealed record ImpactItem(string NodeId, string Name, string? Service, string Reason, double Confidence, string? Path, Evidence? Evidence);

public sealed class ImpactReport
{
    public required IReadOnlyList<Node> Changed { get; init; }

    public List<ImpactItem> Endpoints { get; } = [];

    public List<ImpactItem> Callers { get; } = [];

    public List<ImpactItem> Topics { get; } = [];

    public List<ImpactItem> EventConsumers { get; } = [];

    public List<ImpactItem> Tables { get; } = [];

    public List<ImpactItem> DataConsumers { get; } = [];

    public List<ImpactItem> Rules { get; } = [];

    public List<ImpactItem> Processes { get; } = [];

    public List<string> Services { get; } = [];

    public List<string> Teams { get; } = [];

    public RiskLevel Risk { get; set; }

    public List<string> RiskReasons { get; } = [];

    public bool IsEmpty => Endpoints.Count + Callers.Count + EventConsumers.Count + Tables.Count + DataConsumers.Count + Rules.Count == 0;
}

/// <summary>
/// "What could break if this changes?" Walks the graph from the changed code:
/// upstream callers inside the service → endpoints whose behaviour changes → other services calling those endpoints;
/// events the changed code publishes → their consumers; tables it writes → other readers/writers;
/// business rules it enforces; processes that include any of the above; owning teams.
/// </summary>
public sealed class ImpactAnalyzer(SystemGraph graph)
{
    public int MaxCallerDepth { get; init; } = 6;

    public double MinConfidence { get; init; } = 0.5;

    public ImpactReport Analyze(IReadOnlyList<Node> changed)
    {
        var report = new ImpactReport { Changed = changed };
        var seeds = changed.Where(n => n.Kind == NodeKind.Method).ToList();

        // Non-method changes are expressed through the methods/edges that touch them.
        foreach (var node in changed.Where(n => n.Kind != NodeKind.Method))
        {
            switch (node.Kind)
            {
                case NodeKind.Endpoint:
                    AddEndpoint(report, node, "endpoint contract changed", 1.0, null);
                    break;
                case NodeKind.Topic:
                    AddTopicConsumers(report, node, 1.0, "event contract changed");
                    foreach (var publisher in graph.Incoming(node.Id, EdgeKind.Publishes).Select(e => graph.Find(e.From)).OfType<Node>())
                    {
                        Add(report.DataConsumers, publisher, $"publishes {node.Name}", 1.0, null);
                    }

                    break;
                case NodeKind.Table:
                    AddTableUsers(report, node, excluded: [], 1.0, "schema changed");
                    break;
                case NodeKind.BusinessRule:
                    seeds.AddRange(graph.Outgoing(node.Id, EdgeKind.EnforcedBy).Select(e => graph.Find(e.To)).OfType<Node>().Where(n => n.Kind == NodeKind.Method));
                    break;
                case NodeKind.Service:
                    foreach (var endpoint in graph.Outgoing(node.Id, EdgeKind.Exposes).Select(e => graph.Find(e.To)).OfType<Node>())
                    {
                        AddEndpoint(report, endpoint, "service changed", 1.0, null);
                    }

                    break;
            }
        }

        // 1. Upstream: who calls the changed methods (within and across services).
        var affected = UpstreamMethods(seeds);

        // 2. Endpoints whose handler is (transitively) affected.
        foreach (var (method, (confidence, path)) in affected)
        {
            foreach (var edge in graph.Incoming(method.Id, EdgeKind.ImplementedBy))
            {
                if (graph.Find(edge.From) is { } endpoint)
                {
                    AddEndpoint(report, endpoint, path is null ? "handler changed" : "handler calls changed code", confidence, path);
                }
            }
        }

        // 3. Downstream contracts produced by the changed code itself.
        foreach (var seed in seeds)
        {
            foreach (var producer in graph.Outgoing(seed.Id, EdgeKind.Dispatches).Select(e => graph.Find(e.To)).OfType<Node>().Prepend(seed))
            {
                foreach (var edge in graph.Outgoing(producer.Id, EdgeKind.Publishes).Where(e => e.Confidence >= MinConfidence))
                {
                    if (graph.Find(edge.To) is { } topic)
                    {
                        AddTopicConsumers(report, topic, edge.Confidence, $"{seed.Name} publishes {topic.Name}");
                    }
                }
            }

            foreach (var edge in graph.Outgoing(seed.Id, EdgeKind.WritesTable).Where(e => e.Confidence >= MinConfidence))
            {
                if (graph.Find(edge.To) is { } table)
                {
                    AddTableUsers(report, table, excluded: seeds.Select(s => s.Id).ToHashSet(), edge.Confidence, $"{seed.Name} writes {table.Name}");
                }
            }
        }

        // 4. Business rules implemented by changed (or calling) code.
        foreach (var (method, (confidence, path)) in affected)
        {
            foreach (var edge in graph.Incoming(method.Id, EdgeKind.EnforcedBy))
            {
                if (graph.Find(edge.From) is { } rule)
                {
                    Add(report.Rules, rule, path is null ? "rule implementation changed" : "rule enforced by code that calls the change", confidence * edge.Confidence, path);
                }
            }
        }

        // 5. Business processes that include an affected endpoint, topic or method.
        var touched = report.Endpoints.Select(i => i.NodeId)
            .Concat(report.Topics.Select(i => i.NodeId))
            .Concat(report.EventConsumers.Select(i => i.NodeId))
            .Concat(affected.Keys.Select(m => m.Id))
            .Concat(changed.Select(c => c.Id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var process in graph.OfKind(NodeKind.BusinessProcess))
        {
            var hit = graph.Outgoing(process.Id, EdgeKind.ProcessStep).FirstOrDefault(e => touched.Contains(e.To));
            if (hit is not null)
            {
                Add(report.Processes, process, $"step {hit.Properties.GetValueOrDefault("order")}: {graph.Find(hit.To)?.Name}", 1.0, null);
            }
        }

        // 6. Services, owners, risk.
        var services = changed.SelectMany(n => n.ServicesOf())
            .Concat(report.Endpoints.Select(i => i.Service))
            .Concat(report.Callers.Select(i => i.Service))
            .Concat(report.EventConsumers.Select(i => i.Service))
            .Concat(report.DataConsumers.Select(i => i.Service))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        report.Services.AddRange(services);
        report.Teams.AddRange(graph.OwnersOf(services));
        AssessRisk(report);
        return report;
    }

    /// <summary>Methods that (transitively) call any seed, with confidence and the call path.</summary>
    private Dictionary<Node, (double Confidence, string? Path)> UpstreamMethods(IEnumerable<Node> seeds)
    {
        var result = new Dictionary<Node, (double, string?)>();
        var queue = new Queue<(Node Node, double Confidence, int Depth, string? Path)>();
        foreach (var seed in seeds)
        {
            result[seed] = (1.0, null);
            queue.Enqueue((seed, 1.0, 0, null));
        }

        while (queue.Count > 0)
        {
            var (node, confidence, depth, path) = queue.Dequeue();
            if (depth >= MaxCallerDepth)
            {
                continue;
            }

            foreach (var edge in graph.Incoming(node.Id, EdgeKind.Invokes, EdgeKind.Dispatches))
            {
                if (edge.Confidence < MinConfidence || graph.Find(edge.From) is not { } caller || result.ContainsKey(caller))
                {
                    continue;
                }

                var callerConfidence = confidence * edge.Confidence;
                var callerPath = path is null ? $"{caller.Name} → {node.Name}" : $"{caller.Name} → {path}";
                result[caller] = (callerConfidence, callerPath);
                queue.Enqueue((caller, callerConfidence, depth + 1, callerPath));
            }
        }

        return result;
    }

    private void AddEndpoint(ImpactReport report, Node endpoint, string reason, double confidence, string? path)
    {
        if (!Add(report.Endpoints, endpoint, reason, confidence, path))
        {
            return;
        }

        // Other services (or gateways) calling this endpoint, and what in them depends on the call.
        foreach (var edge in graph.Incoming(endpoint.Id, EdgeKind.CallsEndpoint))
        {
            if (graph.Find(edge.From) is not { } caller)
            {
                continue;
            }

            var callerConfidence = confidence * edge.Confidence;
            Add(report.Callers, caller, $"calls {endpoint.Name}", callerConfidence, null, edge.Evidence.FirstOrDefault());

            // Endpoints of the calling service that reach this call (e.g. a BFF route or page handler).
            foreach (var (upstream, (c, p)) in UpstreamMethods([caller]))
            {
                foreach (var implemented in graph.Incoming(upstream.Id, EdgeKind.ImplementedBy))
                {
                    if (graph.Find(implemented.From) is { } callerEndpoint && callerEndpoint.Id != endpoint.Id)
                    {
                        Add(report.Callers, callerEndpoint, $"reaches {endpoint.Name} via {caller.Name}", callerConfidence * c, p);
                    }
                }
            }
        }
    }

    private void AddTopicConsumers(ImpactReport report, Node topic, double confidence, string reason)
    {
        Add(report.Topics, topic, reason, confidence, null);
        foreach (var edge in graph.Outgoing(topic.Id, EdgeKind.HandledBy))
        {
            if (graph.Find(edge.To) is { } handler && !report.EventConsumers.Any(i => i.NodeId == handler.Id))
            {
                var service = edge.Properties.GetValueOrDefault("service") ?? handler.ServiceOf();
                report.EventConsumers.Add(new ImpactItem(handler.Id, handler.Name, service, $"consumes {topic.Name}", confidence * edge.Confidence, null, edge.Evidence.FirstOrDefault()));
            }
        }
    }

    private void AddTableUsers(ImpactReport report, Node table, HashSet<string> excluded, double confidence, string reason)
    {
        Add(report.Tables, table, reason, confidence, null);
        foreach (var edge in graph.Incoming(table.Id, EdgeKind.ReadsTable, EdgeKind.WritesTable))
        {
            if (excluded.Contains(edge.From) || graph.Find(edge.From) is not { } user)
            {
                continue;
            }

            var verb = edge.Kind == EdgeKind.WritesTable ? "writes" : "reads";
            Add(report.DataConsumers, user, $"{verb} {table.Name}", confidence * edge.Confidence, null, edge.Evidence.FirstOrDefault());
        }
    }

    private static bool Add(List<ImpactItem> list, Node node, string reason, double confidence, string? path, Evidence? evidence = null)
    {
        var existing = list.FindIndex(i => i.NodeId == node.Id);
        if (existing >= 0)
        {
            if (list[existing].Confidence < confidence)
            {
                list[existing] = list[existing] with { Confidence = confidence };
            }

            return false;
        }

        list.Add(new ImpactItem(node.Id, node.Name, node.ServiceOf(), reason, confidence, path, evidence ?? node.Evidence.FirstOrDefault()));
        return true;
    }

    private static void AssessRisk(ImpactReport report)
    {
        var changedServices = report.Changed.SelectMany(n => n.ServicesOf()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var crossServiceCallers = report.Callers.Where(c => c.Service is not null && !changedServices.Contains(c.Service)).Select(c => c.Service!).Distinct().ToList();
        var eventServices = report.EventConsumers.Select(c => c.Service).OfType<string>().Distinct().ToList();
        var sharedData = report.DataConsumers.Where(c => c.Service is not null && !changedServices.Contains(c.Service)).Select(c => c.Service!).Distinct().ToList();

        if (crossServiceCallers.Count > 0)
        {
            report.RiskReasons.Add($"called by other services: {string.Join(", ", crossServiceCallers)}");
        }

        if (eventServices.Count > 0)
        {
            report.RiskReasons.Add($"changes events consumed by: {string.Join(", ", eventServices)}");
        }

        if (sharedData.Count > 0)
        {
            report.RiskReasons.Add($"writes data read by: {string.Join(", ", sharedData)}");
        }

        if (report.Rules.Count > 0)
        {
            report.RiskReasons.Add($"touches business rules: {string.Join(", ", report.Rules.Select(r => r.Name))}");
        }

        report.Risk = report.RiskReasons.Count > 0 ? RiskLevel.High
            : report.Endpoints.Count > 0 || report.DataConsumers.Count > 0 ? RiskLevel.Medium
            : RiskLevel.Low;

        if (report.Risk == RiskLevel.Medium)
        {
            report.RiskReasons.Add($"changes behaviour of {report.Endpoints.Count} endpoint(s) inside the service");
        }
    }
}

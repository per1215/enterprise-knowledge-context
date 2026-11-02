using Ekc.Graph;

namespace Ekc.Analysis;

public sealed record ServiceDescription(
    Node Service,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Domains,
    IReadOnlyList<(Node Endpoint, IReadOnlyList<string> Handlers)> Endpoints,
    IReadOnlyList<(Node Target, string Via)> Calls,
    IReadOnlyList<(Node Caller, Node Endpoint)> CalledBy,
    IReadOnlyList<Node> Publishes,
    IReadOnlyList<Node> Subscribes,
    IReadOnlyList<(Node Table, bool Writes)> Tables,
    IReadOnlyList<Node> Resources,
    IReadOnlyList<Node> DependsOn,
    IReadOnlyList<Node> Rules,
    IReadOnlyList<Node> Processes);

public sealed class ServiceDescriber(SystemGraph graph)
{
    public IReadOnlyList<Node> Services() => graph.OfKind(NodeKind.Service).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public ServiceDescription Describe(Node service)
    {
        var name = service.Name;
        var methods = graph.OfKind(NodeKind.Method).Where(m => m.ServicesOf().Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        var methodIds = methods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        var endpoints = graph.Outgoing(service.Id, EdgeKind.Exposes).Select(e => graph.Find(e.To)).OfType<Node>()
            .OrderBy(e => e.Properties.GetValueOrDefault("path")).ThenBy(e => e.Properties.GetValueOrDefault("verb"))
            .Select(e => (e, (IReadOnlyList<string>)graph.Outgoing(e.Id, EdgeKind.ImplementedBy).Select(h => graph.Find(h.To)?.Name).OfType<string>().ToList()))
            .ToList();

        var calls = methods.SelectMany(m => graph.Outgoing(m.Id, EdgeKind.CallsEndpoint, EdgeKind.CallsService).Select(e => (Edge: e, Method: m)))
            .Concat(endpoints.SelectMany(e => graph.Outgoing(e.Item1.Id, EdgeKind.CallsEndpoint).Select(x => (Edge: x, Method: e.Item1))))
            .Select(x => (Target: graph.Find(x.Edge.To), Via: x.Method.Name))
            .Where(x => x.Target is not null && !string.Equals(x.Target.ServiceOf(), name, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Target!, x.Via))
            .DistinctBy(x => x.Item1.Id)
            .ToList();

        var calledBy = endpoints.SelectMany(e => graph.Incoming(e.Item1.Id, EdgeKind.CallsEndpoint).Select(c => (Caller: graph.Find(c.From), Endpoint: e.Item1)))
            .Where(x => x.Caller is not null && !string.Equals(x.Caller.ServiceOf(), name, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Caller!, x.Endpoint))
            .ToList();

        var publishes = methods.SelectMany(m => graph.Outgoing(m.Id, EdgeKind.Publishes)).Select(e => graph.Find(e.To)).OfType<Node>().DistinctBy(n => n.Id).OrderBy(n => n.Name).ToList();
        var subscribes = graph.Outgoing(service.Id, EdgeKind.Subscribes).Select(e => graph.Find(e.To)).OfType<Node>().OrderBy(n => n.Name).ToList();
        var tables = methods.SelectMany(m => graph.Outgoing(m.Id, EdgeKind.ReadsTable, EdgeKind.WritesTable))
            .GroupBy(e => e.To)
            .Select(g => (Table: graph.Find(g.Key), Writes: g.Any(e => e.Kind == EdgeKind.WritesTable)))
            .Where(x => x.Table is not null)
            .Select(x => (x.Table!, x.Writes))
            .OrderBy(x => x.Item1.Name)
            .ToList();
        var resources = graph.Outgoing(service.Id, EdgeKind.UsesResource).Select(e => graph.Find(e.To)).OfType<Node>().ToList();
        var dependsOn = graph.Outgoing(service.Id, EdgeKind.DependsOn).Select(e => graph.Find(e.To)).OfType<Node>().ToList();
        var rules = graph.OfKind(NodeKind.BusinessRule)
            .Where(r => graph.Outgoing(r.Id, EdgeKind.EnforcedBy).Any(e => methodIds.Contains(e.To)))
            .ToList();
        var touched = endpoints.Select(e => e.Item1.Id).Concat(subscribes.Select(s => s.Id)).Concat(methodIds).ToHashSet(StringComparer.Ordinal);
        var processes = graph.OfKind(NodeKind.BusinessProcess)
            .Where(p => graph.Outgoing(p.Id, EdgeKind.ProcessStep).Any(e => touched.Contains(e.To)))
            .ToList();
        var domains = graph.Outgoing(service.Id, EdgeKind.PartOfDomain).Select(e => graph.Find(e.To)?.Name).OfType<string>().ToList();

        return new ServiceDescription(service, graph.OwnersOf([name]), domains, endpoints, calls, calledBy, publishes, subscribes,
            tables, resources, dependsOn, rules, processes);
    }
}

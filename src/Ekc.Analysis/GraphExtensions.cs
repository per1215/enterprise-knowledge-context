using Ekc.Graph;

namespace Ekc.Analysis;

public static class GraphExtensions
{
    /// <summary>The service a node belongs to: its own service, or the first service whose scope contains it.</summary>
    public static string? ServiceOf(this Node node) =>
        node.Kind == NodeKind.Service ? node.Name
        : node.Service ?? node.Properties.GetValueOrDefault("services")?.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    public static IEnumerable<string> ServicesOf(this Node node) =>
        node.Kind == NodeKind.Service ? [node.Name]
        : node.Service is not null ? [node.Service]
        : node.Properties.GetValueOrDefault("services")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public static string Location(this Node node) =>
        node.File is null ? string.Empty : node.StartLine is null ? node.File : $"{node.File}:{node.StartLine}";

    public static string Location(this Evidence evidence) => $"{evidence.File}:{evidence.Line}";

    /// <summary>Teams owning the given services.</summary>
    public static IReadOnlyList<string> OwnersOf(this SystemGraph graph, IEnumerable<string> services) =>
        services.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(s => graph.Outgoing(NodeIds.Service(s), EdgeKind.OwnedBy))
            .Select(e => graph.Find(e.To)?.Name)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

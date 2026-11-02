using System.Text.RegularExpressions;

namespace Ekc.Graph;

/// <summary>
/// Turns a human reference into graph nodes. Accepted forms:
/// node id · <c>[service:] VERB /path</c> · <c>topic: Name</c> · <c>method: Type.Method</c> · <c>Type.Method</c> ·
/// <c>table: Name</c> · <c>process: Name</c> · <c>rule: Name</c> · <c>service: name</c> · <c>path/to/File.cs[:line]</c> · free text.
/// </summary>
public static partial class GraphResolver
{
    private static readonly string[] Verbs = ["GET", "POST", "PUT", "DELETE", "PATCH", "GRPC"];

    public static IReadOnlyList<Node> Resolve(SystemGraph graph, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        if (graph.Find(q) is { } exact)
        {
            return [exact];
        }

        var file = FileReference().Match(q);
        if (file.Success)
        {
            var path = file.Groups["path"].Value.Replace('\\', '/');
            int? line = file.Groups["line"].Success ? int.Parse(file.Groups["line"].Value) : null;
            var inFile = graph.OfKind(NodeKind.Method)
                .Where(m => m.File is not null && (m.File.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
                .Where(m => line is null || (m.StartLine <= line && m.EndLine >= line))
                .ToList();
            return inFile;
        }

        var prefixed = KindPrefix().Match(q);
        if (prefixed.Success)
        {
            var kind = prefixed.Groups["kind"].Value.ToLowerInvariant();
            var rest = prefixed.Groups["rest"].Value.Trim();
            return kind switch
            {
                "topic" or "event" => ByName(graph, NodeKind.Topic, rest),
                "method" => Methods(graph, rest),
                "table" => ByName(graph, NodeKind.Table, rest),
                "process" => ByName(graph, NodeKind.BusinessProcess, rest),
                "rule" => ByName(graph, NodeKind.BusinessRule, rest),
                "team" => ByName(graph, NodeKind.Team, rest),
                "domain" => ByName(graph, NodeKind.Domain, rest),
                "service" when !StartsWithVerb(rest) => ByName(graph, NodeKind.Service, rest),
                _ => Endpoints(graph, rest, prefixed.Groups["kind"].Value),
            };
        }

        if (StartsWithVerb(q))
        {
            return Endpoints(graph, q, service: null);
        }

        // Unqualified: try the most specific kinds first.
        foreach (var kind in new[] { NodeKind.Service, NodeKind.BusinessProcess, NodeKind.Topic, NodeKind.BusinessRule, NodeKind.Table })
        {
            var exactName = graph.OfKind(kind).Where(n => string.Equals(n.Name, q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactName.Count > 0)
            {
                return exactName;
            }
        }

        var methods = Methods(graph, q);
        if (methods.Count > 0)
        {
            return methods;
        }

        return graph.Nodes.Values
            .Where(n => n.Kind is not NodeKind.Method && n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Kind)
            .ThenBy(n => n.Name.Length)
            .Take(20)
            .ToList();
    }

    /// <summary>Resolves "VERB /path" against endpoints, optionally restricted to one service.</summary>
    public static IReadOnlyList<Node> Endpoints(SystemGraph graph, string verbAndPath, string? service)
    {
        var parts = verbAndPath.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return [];
        }

        var verb = parts[0].ToUpperInvariant();
        var candidates = graph.OfKind(NodeKind.Endpoint)
            .Where(n => service is null || string.Equals(n.Service, service, StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Properties.GetValueOrDefault("verb") == verb);

        if (verb == "GRPC")
        {
            return candidates.Where(n => n.Name.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var wanted = NodeIds.NormalizePath(parts[1]);
        return candidates
            .Select(n => (Node: n, Score: RouteMatcher.Score(wanted, n.Properties.GetValueOrDefault("normalizedPath") ?? string.Empty)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .GroupBy(x => x.Score)
            .FirstOrDefault()?
            .Select(x => x.Node)
            .ToList() ?? [];
    }

    private static IReadOnlyList<Node> Methods(SystemGraph graph, string name)
    {
        var suffix = name.Trim();
        return graph.OfKind(NodeKind.Method)
            .Where(m => m.Name.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || m.Name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
                || (m.Properties.TryGetValue("fullName", out var full) && full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IReadOnlyList<Node> ByName(SystemGraph graph, NodeKind kind, string name)
    {
        var nodes = graph.OfKind(kind).ToList();
        var exact = nodes.Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return nodes.Where(n => n.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool StartsWithVerb(string value)
    {
        var first = value.Split(' ', 2)[0].ToUpperInvariant();
        return Verbs.Contains(first);
    }

    [GeneratedRegex(@"^(?<kind>[A-Za-z][\w.-]*)\s*:\s*(?<rest>.+)$")]
    private static partial Regex KindPrefix();

    [GeneratedRegex(@"^(?<path>[^\s:]+\.(cs|ya?ml|json))(:(?<line>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex FileReference();
}

/// <summary>Matches normalised routes segment by segment; <c>{}</c> matches any single segment.</summary>
public static class RouteMatcher
{
    /// <returns>-1 when the routes do not match; otherwise a score: 2 per identical literal segment,
    /// 1 per parameter matching a parameter, 0 per parameter matching a literal.</returns>
    public static int Score(string normalizedA, string normalizedB)
    {
        var a = normalizedA.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var b = normalizedB.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length != b.Length)
        {
            return -1;
        }

        var score = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                score += a[i] == "{}" ? 1 : 2;
            }
            else if (a[i] == "{}" || b[i] == "{}")
            {
                // A parameter on one side matches a literal on the other, which is weaker than an exact match.
            }
            else
            {
                return -1;
            }
        }

        return score;
    }
}

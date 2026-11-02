namespace Ekc.Graph;

/// <summary>The system map: nodes and typed edges with evidence. Adds merge; indexes are rebuilt on demand.</summary>
public sealed class SystemGraph
{
    public const int CurrentSchemaVersion = 1;

    private Dictionary<string, List<Edge>>? _outgoing;
    private Dictionary<string, List<Edge>>? _incoming;
    private Dictionary<(string From, string To, EdgeKind Kind), Edge>? _edgeKeys;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Root { get; set; } = string.Empty;

    public string? Commit { get; set; }

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, Node> Nodes { get; init; } = new(StringComparer.Ordinal);

    public List<Edge> Edges { get; init; } = [];

    /// <summary>Things the extractors saw but could not resolve (e.g. an HTTP call whose URL comes from configuration).</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>Adds the node, or merges evidence/properties into the existing node with the same id.</summary>
    public Node AddNode(Node node)
    {
        if (Nodes.TryGetValue(node.Id, out var existing))
        {
            foreach (var (key, value) in node.Properties)
            {
                existing.Properties.TryAdd(key, value);
            }

            MergeEvidence(existing.Evidence, node.Evidence);
            existing.Service ??= node.Service;
            existing.File ??= node.File;
            existing.StartLine ??= node.StartLine;
            existing.EndLine ??= node.EndLine;
            return existing;
        }

        Nodes[node.Id] = node;
        return node;
    }

    public Node AddNode(string id, NodeKind kind, string name, string? service = null, Evidence? evidence = null) =>
        AddNode(new Node { Id = id, Kind = kind, Name = name, Service = service, Evidence = evidence is null ? [] : [evidence] });

    /// <summary>Adds the edge, or merges into an existing edge with the same (from, to, kind), keeping the highest confidence.</summary>
    public Edge AddEdge(string from, string to, EdgeKind kind, double confidence = 1.0, Evidence? evidence = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        InvalidateIndexes();
        _edgeKeys ??= Edges.GroupBy(e => (e.From, e.To, e.Kind)).ToDictionary(g => g.Key, g => g.First());
        if (!_edgeKeys.TryGetValue((from, to, kind), out var existing))
        {
            existing = new Edge { From = from, To = to, Kind = kind, Confidence = confidence };
            Edges.Add(existing);
            _edgeKeys[(from, to, kind)] = existing;
        }
        else
        {
            existing.Confidence = Math.Max(existing.Confidence, confidence);
        }

        if (evidence is not null)
        {
            MergeEvidence(existing.Evidence, [evidence]);
        }

        if (properties is not null)
        {
            foreach (var (key, value) in properties)
            {
                existing.Properties.TryAdd(key, value);
            }
        }

        return existing;
    }

    public Node? Find(string id) => Nodes.GetValueOrDefault(id);

    public IEnumerable<Node> OfKind(NodeKind kind) => Nodes.Values.Where(n => n.Kind == kind);

    public IReadOnlyList<Edge> Outgoing(string id)
    {
        EnsureIndexes();
        return _outgoing!.TryGetValue(id, out var edges) ? edges : [];
    }

    public IReadOnlyList<Edge> Incoming(string id)
    {
        EnsureIndexes();
        return _incoming!.TryGetValue(id, out var edges) ? edges : [];
    }

    public IEnumerable<Edge> Outgoing(string id, params EdgeKind[] kinds) => Outgoing(id).Where(e => kinds.Contains(e.Kind));

    public IEnumerable<Edge> Incoming(string id, params EdgeKind[] kinds) => Incoming(id).Where(e => kinds.Contains(e.Kind));

    /// <summary>Removes edges whose endpoints do not exist (e.g. references to filtered-out code).</summary>
    public int PruneDanglingEdges()
    {
        var removed = Edges.RemoveAll(e => !Nodes.ContainsKey(e.From) || !Nodes.ContainsKey(e.To));
        InvalidateIndexes();
        _edgeKeys = null;
        return removed;
    }

    private static void MergeEvidence(List<Evidence> target, IEnumerable<Evidence> source)
    {
        foreach (var evidence in source)
        {
            if (target.Count < 5 && !target.Contains(evidence))
            {
                target.Add(evidence);
            }
        }
    }

    private void InvalidateIndexes()
    {
        _outgoing = null;
        _incoming = null;
    }

    private void EnsureIndexes()
    {
        if (_outgoing is not null)
        {
            return;
        }

        _outgoing = Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _incoming = Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }
}

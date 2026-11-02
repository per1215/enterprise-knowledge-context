using System.Text.Json.Serialization;

namespace Ekc.Graph;

[JsonConverter(typeof(JsonStringEnumConverter<NodeKind>))]
public enum NodeKind
{
    Service,
    Endpoint,
    Topic,
    Method,
    Table,
    Resource,
    Team,
    BusinessProcess,
    BusinessRule,
    Domain,
}

/// <summary>Edge direction always reads "From {kind} To", e.g. service Exposes endpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EdgeKind>))]
public enum EdgeKind
{
    /// <summary>Service → Endpoint.</summary>
    Exposes,

    /// <summary>Endpoint → Method that handles it.</summary>
    ImplementedBy,

    /// <summary>Method → Method (name-resolved call).</summary>
    Invokes,

    /// <summary>Method → handler Method of an in-process command/notification it creates.</summary>
    Dispatches,

    /// <summary>Method → Endpoint of another service (HTTP/gRPC).</summary>
    CallsEndpoint,

    /// <summary>Method → Service when the exact endpoint could not be resolved.</summary>
    CallsService,

    /// <summary>Method → Topic (integration event it creates/publishes).</summary>
    Publishes,

    /// <summary>Service → Topic.</summary>
    Subscribes,

    /// <summary>Topic → handler Method in a subscribing service.</summary>
    HandledBy,

    /// <summary>Method → Table.</summary>
    ReadsTable,

    /// <summary>Method → Table.</summary>
    WritesTable,

    /// <summary>Service → Resource (database, cache, broker).</summary>
    UsesResource,

    /// <summary>Service → Service (declared in orchestration, e.g. Aspire WithReference).</summary>
    DependsOn,

    /// <summary>Service → Team.</summary>
    OwnedBy,

    /// <summary>BusinessRule → Method.</summary>
    EnforcedBy,

    /// <summary>BusinessProcess → Endpoint/Topic/Method (property "order").</summary>
    ProcessStep,

    /// <summary>Service → Domain.</summary>
    PartOfDomain,

    /// <summary>Table → Resource (database).</summary>
    StoredIn,
}

/// <summary>Where a fact came from. Every node and edge keeps at least one.</summary>
public sealed record Evidence(string File, int Line, string Extractor, string? Snippet = null);

public sealed class Node
{
    public required string Id { get; init; }

    public required NodeKind Kind { get; init; }

    public required string Name { get; set; }

    /// <summary>Owning service for endpoints, methods; null for shared nodes (topics, tables, teams).</summary>
    public string? Service { get; set; }

    /// <summary>Repository-relative source file, for methods and other code-defined nodes.</summary>
    public string? File { get; set; }

    public int? StartLine { get; set; }

    public int? EndLine { get; set; }

    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    public List<Evidence> Evidence { get; init; } = [];

    public override string ToString() => $"{Kind} {Name}";
}

public sealed class Edge
{
    public required string From { get; init; }

    public required string To { get; init; }

    public required EdgeKind Kind { get; init; }

    /// <summary>0..1. 1.0 = deterministic fact (declaration); lower = heuristic resolution.</summary>
    public double Confidence { get; set; } = 1.0;

    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    public List<Evidence> Evidence { get; init; } = [];

    public override string ToString() => $"{From} -{Kind}-> {To} ({Confidence:0.00})";
}

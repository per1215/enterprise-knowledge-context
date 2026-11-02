using Ekc.Extractors;

namespace Ekc.Tests;

/// <summary>Builds the system map of samples/fabrikam once for all end-to-end tests.</summary>
public sealed class FabrikamFixture
{
    public FabrikamFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
        SampleRoot = Path.Combine(RepositoryRoot, "samples", "fabrikam");
        (Graph, Report) = GraphBuilder.Build(new GraphBuildOptions { Root = SampleRoot });
    }

    public string RepositoryRoot { get; }

    public string SampleRoot { get; }

    public SystemGraph Graph { get; }

    public GraphBuildReport Report { get; }

    public Node Single(string query)
    {
        var nodes = GraphResolver.Resolve(Graph, query);
        Assert.True(nodes.Count == 1, $"'{query}' resolved to {nodes.Count} nodes: {string.Join(", ", nodes.Select(n => n.Id))}");
        return nodes[0];
    }

    public bool HasEdge(string from, EdgeKind kind, string to) =>
        Graph.Edges.Any(e => e.Kind == kind && e.From == from && e.To == to);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ekc.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ekc.slnx not found above the test output directory.");
    }
}

[CollectionDefinition(Name)]
public sealed class FabrikamCollection : ICollectionFixture<FabrikamFixture>
{
    public const string Name = "fabrikam";
}

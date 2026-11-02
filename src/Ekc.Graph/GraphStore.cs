using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ekc.Graph;

/// <summary>Reads and writes the graph as a single JSON file (default <c>.ekc/graph.json</c>).</summary>
public static class GraphStore
{
    public const string DefaultPath = ".ekc/graph.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SaveAsync(SystemGraph graph, string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, graph, Options, cancellationToken);
    }

    public static async Task<SystemGraph> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No graph at '{path}'. Run 'ekc map <repository>' first.", path);
        }

        await using var stream = File.OpenRead(path);
        var graph = await JsonSerializer.DeserializeAsync<SystemGraph>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"'{path}' is not a valid EKC graph.");

        if (graph.SchemaVersion != SystemGraph.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Graph schema {graph.SchemaVersion} is not supported (expected {SystemGraph.CurrentSchemaVersion}). Re-run 'ekc map'.");
        }

        return graph;
    }
}

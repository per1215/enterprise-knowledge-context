using System.Diagnostics;
using Ekc.Extractors.Common;
using Ekc.Extractors.DotNet;
using Ekc.Graph;

namespace Ekc.Extractors;

public sealed class GraphBuildOptions
{
    /// <summary>Repository root to analyse.</summary>
    public required string Root { get; init; }

    /// <summary>Overlay file with domains/processes/rules; defaults to &lt;root&gt;/.ekc/overlay.yaml.</summary>
    public string? OverlayPath { get; init; }

    public bool IncludeTests { get; init; }
}

public sealed record GraphBuildReport(TimeSpan Duration, int Projects, int SourceFiles, IReadOnlyDictionary<NodeKind, int> Nodes,
    IReadOnlyDictionary<EdgeKind, int> Edges, int Warnings);

/// <summary>Runs every extractor over a repository and returns the system graph.</summary>
public static class GraphBuilder
{
    public static (SystemGraph Graph, GraphBuildReport Report) Build(GraphBuildOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var root = Path.GetFullPath(options.Root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository '{options.Root}' does not exist.");
        }

        var graph = new SystemGraph { Root = root, Commit = GitCommit(root) };
        var projects = ProjectLoader.Load(root, options.IncludeTests);
        var context = new AnalysisContext(root, graph, projects);

        ServiceDiscovery.Discover(context);
        EndpointExtractor.Extract(context);
        OpenApiExtractor.Extract(context);
        HttpClientExtractor.Extract(context);
        MessagingExtractor.Extract(context);
        DataAccessExtractor.Extract(context);
        InvocationExtractor.Extract(context);
        RuleExtractor.Extract(context);
        CodeOwnersExtractor.Extract(context);
        OverlayExtractor.Extract(context, options.OverlayPath);

        var pruned = graph.PruneDanglingEdges();
        if (pruned > 0)
        {
            graph.Warnings.Add($"{pruned} edge(s) referenced nodes outside the analysed services and were dropped");
        }

        var report = new GraphBuildReport(
            stopwatch.Elapsed,
            projects.Count,
            projects.Sum(p => p.Trees.Count),
            graph.Nodes.Values.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count()),
            graph.Edges.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
            graph.Warnings.Count);
        return (graph, report);
    }

    private static string? GitCommit(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length == 40 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

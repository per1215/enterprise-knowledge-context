using System.Text.Json;
using Ekc.Extractors.DotNet;
using Ekc.Graph;
using YamlDotNet.RepresentationModel;

namespace Ekc.Extractors.Common;

/// <summary>Endpoints declared in OpenAPI documents inside a service's folder (contract-first or generated specs).</summary>
internal static class OpenApiExtractor
{
    private static readonly string[] Verbs = ["get", "post", "put", "delete", "patch"];

    public static void Extract(AnalysisContext context)
    {
        foreach (var file in RepositoryFiles.Enumerate(context.Root, "*.json", "*.yaml", "*.yml"))
        {
            if (new FileInfo(file).Length > 2_000_000)
            {
                continue;
            }

            var service = context.Services
                .Where(s => file.StartsWith(s.Entry.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderByDescending(s => s.Entry.Directory.Length)
                .FirstOrDefault();
            if (service is null)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("openapi", StringComparison.Ordinal) && !text.Contains("swagger", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = RepositoryFiles.Relative(context.Root, file);
            foreach (var (verb, path) in Operations(file, text))
            {
                var id = NodeIds.Endpoint(service.Name, verb, path);
                var evidence = new Evidence(relative, 1, "openapi", $"{verb} {path}");
                var node = context.Graph.AddNode(id, NodeKind.Endpoint, $"{verb} /{path.Trim('/')}", service.Name, evidence);
                node.Properties.TryAdd("verb", verb);
                node.Properties.TryAdd("path", "/" + path.Trim('/'));
                node.Properties.TryAdd("normalizedPath", NodeIds.NormalizePath(path));
                node.Properties["openapi"] = relative;
                context.Graph.AddEdge(service.NodeId, id, EdgeKind.Exposes, 1.0, evidence);
            }
        }
    }

    internal static IEnumerable<(string Verb, string Path)> Operations(string file, string text)
    {
        var results = new List<(string, string)>();
        try
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(text);
                if (!document.RootElement.TryGetProperty("openapi", out _) && !document.RootElement.TryGetProperty("swagger", out _))
                {
                    return results;
                }

                if (document.RootElement.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
                {
                    foreach (var path in paths.EnumerateObject())
                    {
                        foreach (var operation in path.Value.EnumerateObject().Where(o => Verbs.Contains(o.Name)))
                        {
                            results.Add((operation.Name.ToUpperInvariant(), path.Name));
                        }
                    }
                }
            }
            else
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(text));
                if (yaml.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root
                    || !(root.Children.ContainsKey(new YamlScalarNode("openapi")) || root.Children.ContainsKey(new YamlScalarNode("swagger")))
                    || !root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
                    || pathsNode is not YamlMappingNode paths)
                {
                    return results;
                }

                foreach (var (key, value) in paths.Children)
                {
                    if (value is YamlMappingNode operations)
                    {
                        foreach (var operation in operations.Children.Keys.OfType<YamlScalarNode>().Where(k => Verbs.Contains(k.Value)))
                        {
                            results.Add((operation.Value!.ToUpperInvariant(), ((YamlScalarNode)key).Value!));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException)
        {
            // Not an OpenAPI document (or not valid): ignore.
        }

        return results;
    }
}

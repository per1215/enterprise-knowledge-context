using Ekc.Extractors.DotNet;
using Ekc.Graph;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ekc.Extractors.Common;

/// <summary>
/// Curated business knowledge that code cannot express: domains, business processes (ordered steps that
/// reference endpoints/topics/methods), and business rules. Read from <c>.ekc/overlay.yaml</c> or a given path.
/// </summary>
internal static class OverlayExtractor
{
    public const string DefaultPath = ".ekc/overlay.yaml";

    public sealed class Overlay
    {
        public List<DomainSpec> Domains { get; set; } = [];

        public List<RuleSpec> Rules { get; set; } = [];

        public List<ProcessSpec> Processes { get; set; } = [];
    }

    public sealed class DomainSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public List<string> Services { get; set; } = [];
    }

    public sealed class RuleSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public List<string> EnforcedBy { get; set; } = [];
    }

    public sealed class ProcessSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? Domain { get; set; }

        public string? Description { get; set; }

        public List<string> Steps { get; set; } = [];
    }

    public static void Extract(AnalysisContext context, string? overlayPath)
    {
        var path = overlayPath ?? Path.Combine(context.Root, DefaultPath);
        if (!File.Exists(path))
        {
            return;
        }

        var overlay = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<Overlay>(File.ReadAllText(path)) ?? new Overlay();
        var source = Path.GetFullPath(path).StartsWith(context.Root, StringComparison.Ordinal)
            ? RepositoryFiles.Relative(context.Root, path)
            : Path.GetFileName(path);

        foreach (var domain in overlay.Domains.Where(d => d.Name.Length > 0))
        {
            var id = NodeIds.Domain(domain.Name);
            var node = context.Graph.AddNode(id, NodeKind.Domain, domain.Name, evidence: new Evidence(source, 1, "overlay"));
            if (domain.Description is not null)
            {
                node.Properties["description"] = domain.Description;
            }

            foreach (var service in domain.Services)
            {
                if (context.Graph.Nodes.ContainsKey(NodeIds.Service(service)))
                {
                    context.Graph.AddEdge(NodeIds.Service(service), id, EdgeKind.PartOfDomain, 1.0, new Evidence(source, 1, "overlay"));
                }
                else
                {
                    context.Graph.Warnings.Add($"overlay: domain '{domain.Name}' references unknown service '{service}'");
                }
            }
        }

        foreach (var rule in overlay.Rules.Where(r => r.Name.Length > 0))
        {
            var id = NodeIds.Rule(rule.Name);
            var node = context.Graph.AddNode(id, NodeKind.BusinessRule, rule.Name, evidence: new Evidence(source, 1, "overlay"));
            if (rule.Description is not null)
            {
                node.Properties["description"] = rule.Description;
            }

            foreach (var reference in rule.EnforcedBy)
            {
                var targets = GraphResolver.Resolve(context.Graph, reference.Contains(':') ? reference : $"method: {reference}");
                if (targets.Count == 0)
                {
                    context.Graph.Warnings.Add($"overlay: rule '{rule.Name}' references '{reference}', which was not found");
                }

                foreach (var target in targets)
                {
                    context.Graph.AddEdge(id, target.Id, EdgeKind.EnforcedBy, 1.0, new Evidence(source, 1, "overlay", reference));
                }
            }
        }

        foreach (var process in overlay.Processes.Where(p => p.Name.Length > 0))
        {
            var id = NodeIds.Process(process.Name);
            var node = context.Graph.AddNode(id, NodeKind.BusinessProcess, process.Name, evidence: new Evidence(source, 1, "overlay"));
            if (process.Description is not null)
            {
                node.Properties["description"] = process.Description;
            }

            if (process.Domain is not null)
            {
                node.Properties["domain"] = process.Domain;
            }

            for (var i = 0; i < process.Steps.Count; i++)
            {
                var targets = GraphResolver.Resolve(context.Graph, process.Steps[i]);
                if (targets.Count == 0)
                {
                    context.Graph.Warnings.Add($"overlay: process '{process.Name}' step '{process.Steps[i]}' was not found");
                }

                foreach (var target in targets.Take(3))
                {
                    context.Graph.AddEdge(id, target.Id, EdgeKind.ProcessStep, 1.0, new Evidence(source, 1, "overlay", process.Steps[i]),
                        new Dictionary<string, string> { ["order"] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
            }
        }
    }
}

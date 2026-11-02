using System.Globalization;
using System.Text;
using Ekc.Graph;

namespace Ekc.Analysis;

/// <summary>Diagrams from the graph: a Mermaid sequence diagram for a flow, and container views in Mermaid and Structurizr DSL (C4).</summary>
public static class DiagramExporter
{
    private sealed record Link(string From, string To, string Label, string Style);

    public static string FlowSequence(FlowResult flow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("sequenceDiagram");
        sb.AppendLine("  autonumber");
        var participants = new List<string>();
        var lines = new List<string>();

        string P(string name)
        {
            var id = Id(name);
            if (!participants.Contains(name))
            {
                participants.Add(name);
            }

            return id;
        }

        foreach (var step in flow.Steps)
        {
            var service = step.Service;
            switch (step.Kind)
            {
                case FlowStepKind.Entry when !step.NodeId.StartsWith("topic:", StringComparison.Ordinal):
                    lines.Add($"  {P("client")}->>{P(service)}: {Escape(step.Description)}");
                    break;
                case FlowStepKind.HttpCall:
                {
                    var target = step.Description.Split(':', 2)[0].Split(' ').Last();
                    var call = step.Description.Contains(": ", StringComparison.Ordinal) ? step.Description.Split(": ", 2)[1] : step.Description;
                    lines.Add($"  {P(service)}->>{P(target)}: {Escape(call)}");
                    break;
                }

                case FlowStepKind.Publish:
                    lines.Add($"  {P(service)}-){P("event bus")}: {Escape(step.Description.Split(' ').Last())}");
                    break;
                case FlowStepKind.Consume:
                    lines.Add($"  {P("event bus")}-){P(service)}: {Escape(step.Description.Split(" in ")[0].Split(' ').Last())}");
                    break;
                case FlowStepKind.DataWrite or FlowStepKind.DataRead:
                    lines.Add($"  {P(service)}->>{P("database")}: {(step.Kind == FlowStepKind.DataWrite ? "write" : "read")} {Escape(step.Description.Split("table ").Last())}");
                    break;
                case FlowStepKind.Dispatch:
                    lines.Add($"  Note over {P(service)}: {Escape(step.Description)}");
                    break;
            }
        }

        foreach (var participant in participants)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  participant {Id(participant)} as {participant}");
        }

        foreach (var line in lines.Distinct())
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    /// <summary>Service-level container diagram (Mermaid flowchart): HTTP/gRPC calls, events, data stores.</summary>
    public static string MermaidContainers(SystemGraph graph)
    {
        var links = Links(graph);
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domain in graph.OfKind(NodeKind.Domain))
        {
            var members = graph.Incoming(domain.Id, EdgeKind.PartOfDomain).Select(e => graph.Find(e.From)).OfType<Node>().ToList();
            if (members.Count == 0)
            {
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  subgraph {Id("domain-" + domain.Name)}[\"{domain.Name}\"]");
            foreach (var member in members)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {ServiceNode(member)}");
                declared.Add(member.Name);
            }

            sb.AppendLine("  end");
        }

        foreach (var service in graph.OfKind(NodeKind.Service).Where(s => !declared.Contains(s.Name)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {ServiceNode(service)}");
        }

        foreach (var resource in graph.OfKind(NodeKind.Resource).Where(r => links.Any(l => l.To == r.Name || l.From == r.Name)))
        {
            var shape = IsDatabase(resource) ? $"[(\"{resource.Name}\")]" : $"{{{{\"{resource.Name}\"}}}}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {Id(resource.Name)}{shape}");
        }

        foreach (var link in links)
        {
            var arrow = link.Style == "event" ? "-.->" : "-->";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {Id(link.From)} {arrow}|\"{Escape(link.Label)}\"| {Id(link.To)}");
        }

        return sb.ToString();
    }

    /// <summary>C4 model as Structurizr DSL (container view), for Structurizr Lite/CLI.</summary>
    public static string Structurizr(SystemGraph graph, string systemName)
    {
        var links = Links(graph);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"workspace \"{systemName}\" \"Generated by EKC from source code\" {{");
        sb.AppendLine("  model {");
        sb.AppendLine("    user = person \"User\"");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    system = softwareSystem \"{systemName}\" {{");
        foreach (var service in graph.OfKind(NodeKind.Service))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"      {Id(service.Name)} = container \"{service.Name}\" \"{service.Properties.GetValueOrDefault("project")}\" \".NET\"");
        }

        foreach (var resource in graph.OfKind(NodeKind.Resource).Where(r => links.Any(l => l.To == r.Name || l.From == r.Name)))
        {
            var tags = IsDatabase(resource) ? " \"Database\"" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"      {Id(resource.Name)} = container \"{resource.Name}\" \"\" \"{resource.Properties.GetValueOrDefault("type")}\"{(tags.Length > 0 ? " {\n        tags" + tags + "\n      }" : string.Empty)}");
        }

        sb.AppendLine("    }");
        foreach (var link in links)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {Id(link.From)} -> {Id(link.To)} \"{Escape(link.Label)}\"");
        }

        sb.AppendLine("  }");
        sb.AppendLine("  views {");
        sb.AppendLine("    container system \"Containers\" {");
        sb.AppendLine("      include *");
        sb.AppendLine("      autoLayout lr");
        sb.AppendLine("    }");
        sb.AppendLine("    styles {");
        sb.AppendLine("      element \"Database\" {");
        sb.AppendLine("        shape cylinder");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Service-to-service and service-to-resource links, aggregated.</summary>
    private static List<Link> Links(SystemGraph graph)
    {
        var links = new Dictionary<(string, string, string), SortedSet<string>>();

        void Add(string from, string to, string style, string label)
        {
            if (from == to)
            {
                return;
            }

            if (!links.TryGetValue((from, to, style), out var labels))
            {
                links[(from, to, style)] = labels = new SortedSet<string>(StringComparer.Ordinal);
            }

            labels.Add(label);
        }

        foreach (var edge in graph.Edges)
        {
            var from = graph.Find(edge.From);
            var to = graph.Find(edge.To);
            if (from is null || to is null)
            {
                continue;
            }

            switch (edge.Kind)
            {
                case EdgeKind.CallsEndpoint when from.ServiceOf() is { } a && to.ServiceOf() is { } b:
                    Add(a, b, "sync", to.Properties.GetValueOrDefault("verb") == "GRPC" ? "gRPC" : "HTTP");
                    break;
                case EdgeKind.CallsService when from.ServiceOf() is { } a:
                    Add(a, to.Name, "sync", "HTTP");
                    break;
                case EdgeKind.Publishes when from.ServiceOf() is { } publisher:
                    foreach (var handler in graph.Outgoing(to.Id, EdgeKind.HandledBy))
                    {
                        var subscriber = handler.Properties.GetValueOrDefault("service") ?? graph.Find(handler.To)?.ServiceOf();
                        if (subscriber is not null)
                        {
                            Add(publisher, subscriber, "event", ShortEvent(to.Name));
                        }
                    }

                    break;
                case EdgeKind.UsesResource:
                    Add(from.Name, to.Name, "sync", to.Properties.GetValueOrDefault("type") ?? "uses");
                    break;
                case EdgeKind.DependsOn when from.Kind == NodeKind.Resource:
                    Add(from.Name, to.Name, "sync", "routes");
                    break;
            }
        }

        return links.Select(l => new Link(l.Key.Item1, l.Key.Item2,
            l.Value.Count <= 3 ? string.Join(", ", l.Value) : $"{string.Join(", ", l.Value.Take(2))} +{l.Value.Count - 2}", l.Key.Item3)).ToList();
    }

    private static string ServiceNode(Node service) =>
        $"{Id(service.Name)}[\"{service.Name}<br/><small>{service.Properties.GetValueOrDefault("project")}</small>\"]";

    private static bool IsDatabase(Node resource) =>
        resource.Properties.GetValueOrDefault("type") is { } type
        && (type.Contains("Database", StringComparison.OrdinalIgnoreCase) || type.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Sql", StringComparison.OrdinalIgnoreCase) || type.Contains("Redis", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Mongo", StringComparison.OrdinalIgnoreCase));

    private static string ShortEvent(string name) => name.Replace("IntegrationEvent", string.Empty, StringComparison.Ordinal);

    private static string Id(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return char.IsDigit(sb[0]) ? "n" + sb : sb.ToString();
    }

    private static string Escape(string text) => text.Replace("\"", "'", StringComparison.Ordinal).Replace(";", ",", StringComparison.Ordinal);
}

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Ekc.Analysis;
using Ekc.Cli;
using Ekc.Extractors;
using Ekc.Graph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var graphOption = new Option<string>("--graph", "-g")
{
    Description = "Path of the system map file.",
    DefaultValueFactory = _ => GraphStore.DefaultPath,
    Recursive = true,
};

var root = new RootCommand("EKC: the open-source, self-hosted system map for AI agents.");
root.Add(graphOption);

// ekc map <repo>
var repoArgument = new Argument<string>("repository") { Description = "Repository root to analyse.", DefaultValueFactory = _ => "." };
var overlayOption = new Option<string?>("--overlay") { Description = "Overlay YAML with domains, processes and rules (default: <repo>/.ekc/overlay.yaml)." };
var testsOption = new Option<bool>("--include-tests") { Description = "Also analyse test projects." };
var map = new Command("map", "Build the system map from source code (no build or restore needed).") { repoArgument, overlayOption, testsOption };
map.SetAction(async (parse, ct) =>
{
    var (graph, report) = GraphBuilder.Build(new GraphBuildOptions
    {
        Root = parse.GetValue(repoArgument)!,
        OverlayPath = parse.GetValue(overlayOption),
        IncludeTests = parse.GetValue(testsOption),
    });
    var output = parse.GetValue(graphOption)!;
    await GraphStore.SaveAsync(graph, output, ct);

    Console.WriteLine($"Mapped {report.Projects} projects ({report.SourceFiles} files) in {report.Duration.TotalSeconds:0.0}s → {output}");
    Console.WriteLine($"  services {Count(NodeKind.Service)} · endpoints {Count(NodeKind.Endpoint)} · events {Count(NodeKind.Topic)} · tables {Count(NodeKind.Table)} · methods {Count(NodeKind.Method)}");
    Console.WriteLine($"  rules {Count(NodeKind.BusinessRule)} · processes {Count(NodeKind.BusinessProcess)} · teams {Count(NodeKind.Team)} · edges {graph.Edges.Count}");
    var unresolved = graph.Edges.Count(e => e.Kind == EdgeKind.CallsService);
    if (unresolved > 0)
    {
        Console.WriteLine($"  {unresolved} call(s) resolved to a service but not to an endpoint (URL built at runtime)");
    }

    foreach (var warning in graph.Warnings.Take(10))
    {
        Console.WriteLine($"  warning: {warning}");
    }

    return 0;

    int Count(NodeKind kind) => report.Nodes.GetValueOrDefault(kind);
});
root.Add(map);

// ekc services
var services = new Command("services", "List services.");
services.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    Console.WriteLine(MarkdownRenderer.ServiceList(graph));
});
root.Add(services);

// ekc describe <service>
var serviceArgument = new Argument<string>("service");
var describe = new Command("describe", "Describe a service.") { serviceArgument };
describe.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    Console.WriteLine(new McpTools(new GraphSession(graph)).DescribeService(parse.GetValue(serviceArgument)!));
});
root.Add(describe);

// ekc flow <start>
var startArgument = new Argument<string>("start") { Description = "'POST /api/orders', 'topic: Name', a process name, or 'Type.Method'." };
var formatOption = new Option<string>("--format") { Description = "md | mermaid", DefaultValueFactory = _ => "md" };
var flow = new Command("flow", "Trace how a request or event flows through the system.") { startArgument, formatOption };
flow.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    var start = parse.GetValue(startArgument)!;
    var starts = GraphResolver.Resolve(graph, start);
    if (starts.Count == 0)
    {
        Console.Error.WriteLine(MarkdownRenderer.Nodes(starts, start));
        return 2;
    }

    var result = new FlowTracer(graph).Trace(starts.Take(3).ToList(), start);
    Console.WriteLine(parse.GetValue(formatOption) == "mermaid" ? DiagramExporter.FlowSequence(result) : MarkdownRenderer.Flow(result));
    return 0;
});
root.Add(flow);

// ekc impact <target...> | --diff <range>
var targetsArgument = new Argument<string[]>("targets") { Description = "What changes ('Type.Method', 'File.cs:42', endpoint, topic, table, rule).", Arity = ArgumentArity.ZeroOrMore };
var diffOption = new Option<string?>("--diff") { Description = "Git range to analyse, e.g. origin/main...HEAD (runs git diff -U0)." };
var diffFileOption = new Option<string?>("--diff-file") { Description = "Read a unified diff from a file instead of running git." };
var repoOption = new Option<string>("--repo") { Description = "Repository for --diff.", DefaultValueFactory = _ => "." };
var prOption = new Option<bool>("--pr-comment") { Description = "Format as a pull-request comment." };
var impactFormat = new Option<string>("--format") { Description = "md | json", DefaultValueFactory = _ => "md" };
var outOption = new Option<string?>("--out") { Description = "Write the report to a file." };
var failOnOption = new Option<string?>("--fail-on") { Description = "Exit with code 3 at this risk or above: medium | high." };
var impact = new Command("impact", "What could break if this changes? Endpoints, callers, event consumers, data, rules, processes, owners.")
{
    targetsArgument, diffOption, diffFileOption, repoOption, prOption, impactFormat, outOption, failOnOption,
};
impact.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    var changed = new List<Node>();
    var notes = new List<string>();

    string? diffText = null;
    if (parse.GetValue(diffFileOption) is { } diffFile)
    {
        diffText = await File.ReadAllTextAsync(diffFile, ct);
    }
    else if (parse.GetValue(diffOption) is { } range)
    {
        diffText = RunGit(parse.GetValue(repoOption)!, $"diff -U0 --no-color {range}");
    }

    if (diffText is not null)
    {
        var (methods, unmapped) = DiffMapper.Map(graph, DiffMapper.Parse(diffText));
        changed.AddRange(methods);
        notes.AddRange(unmapped.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(f => $"changed outside mapped methods: {f}"));
    }

    foreach (var target in parse.GetValue(targetsArgument) ?? [])
    {
        var resolved = GraphResolver.Resolve(graph, target);
        if (resolved.Count == 0)
        {
            Console.Error.WriteLine($"'{target}' not found in the system map.");
        }

        changed.AddRange(resolved.Where(r => !changed.Contains(r)));
    }

    var report = new ImpactAnalyzer(graph).Analyze(changed);
    var text = parse.GetValue(impactFormat) == "json"
        ? JsonSerializer.Serialize(new
        {
            risk = report.Risk.ToString(), report.RiskReasons, changed = report.Changed.Select(c => c.Id), report.Endpoints, report.Callers,
            report.Topics, report.EventConsumers, report.Tables, report.DataConsumers, report.Rules, report.Processes, report.Services, report.Teams, notes,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })
        : MarkdownRenderer.Impact(report, forPullRequest: parse.GetValue(prOption))
            + (notes.Count > 0 ? "\n" + string.Join('\n', notes.Select(n => $"> {n}")) + "\n" : string.Empty);

    if (parse.GetValue(outOption) is { } outPath)
    {
        await File.WriteAllTextAsync(outPath, text, ct);
    }
    else
    {
        Console.WriteLine(text);
    }

    var failOn = parse.GetValue(failOnOption)?.ToLowerInvariant();
    return failOn switch
    {
        "high" when report.Risk == RiskLevel.High => 3,
        "medium" when report.Risk >= RiskLevel.Medium => 3,
        _ => 0,
    };
});
root.Add(impact);

// ekc search <query>
var queryArgument = new Argument<string>("query");
var search = new Command("search", "Find services, endpoints, events, tables, rules, processes or methods.") { queryArgument };
search.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    var query = parse.GetValue(queryArgument)!;
    Console.WriteLine(MarkdownRenderer.Nodes(GraphResolver.Resolve(graph, query), query));
});
root.Add(search);

// ekc export c4
var exportFormat = new Option<string>("--format") { Description = "mermaid | structurizr", DefaultValueFactory = _ => "mermaid" };
var systemName = new Option<string>("--name") { Description = "System name for the C4 model.", DefaultValueFactory = _ => "System" };
var export = new Command("export", "Export diagrams.");
var c4 = new Command("c4", "Container diagram (C4) of services, data stores and their interactions.") { exportFormat, systemName };
c4.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    Console.WriteLine(parse.GetValue(exportFormat) == "structurizr"
        ? DiagramExporter.Structurizr(graph, parse.GetValue(systemName)!)
        : DiagramExporter.MermaidContainers(graph));
});
export.Add(c4);
root.Add(export);

// ekc mcp
var mcp = new Command("mcp", "Run the MCP server over stdio for AI assistants (Copilot, Cursor, Claude Code, Continue).");
mcp.SetAction(async (parse, ct) =>
{
    var graph = await GraphStore.LoadAsync(parse.GetValue(graphOption)!, ct);
    var builder = Host.CreateApplicationBuilder();
    // stdout carries the MCP protocol; logs must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddSingleton(new GraphSession(graph));
    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "ekc", Version = "0.1.0" })
        .WithStdioServerTransport()
        .WithTools<McpTools>();
    await builder.Build().RunAsync(ct);
});
root.Add(mcp);

return await root.Parse(args).InvokeAsync();

static string RunGit(string repository, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = repository,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException("Could not start git.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {arguments} failed: {error.Trim()}");
    }

    return output;
}

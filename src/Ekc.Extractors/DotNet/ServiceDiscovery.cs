using Ekc.Extractors.Common;
using Ekc.Graph;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.RepresentationModel;

namespace Ekc.Extractors.DotNet;

/// <summary>
/// Finds services and infrastructure resources from orchestration: .NET Aspire AppHosts first, then
/// docker-compose files, then standalone executable projects.
/// </summary>
internal static partial class ServiceDiscovery
{
    // Fluent calls that relate resources without meaning "A depends on B".
    private static readonly HashSet<string> IgnoredRelations = new(StringComparer.Ordinal)
    {
        "WaitFor", "WaitForCompletion", "WithParentRelationship", "WithEnvironment", "WithUrls", "WithAnnotation",
    };

    public static void Discover(AnalysisContext context)
    {
        foreach (var appHost in context.Projects.Where(p => p.IsAppHost))
        {
            DiscoverAspire(context, appHost);
        }

        if (context.Services.Count == 0)
        {
            DiscoverCompose(context);
        }

        if (context.Services.Count == 0)
        {
            foreach (var project in context.Projects.Where(p => p.IsExecutable && !p.IsAppHost && !p.IsTest))
            {
                AddService(context, KebabCase(project.Name), project, "project", new Evidence(Relative(context, project.CsprojPath), 1, "csproj"));
            }
        }

        context.ResolveScopes();
    }

    private static void DiscoverAspire(AnalysisContext context, ProjectInfo appHost)
    {
        // variable name → node id (service or resource)
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var statements = appHost.Trees
            .SelectMany(t => t.GetCompilationUnitRoot().DescendantNodes())
            .Where(n => n is LocalDeclarationStatementSyntax or ExpressionStatementSyntax)
            .ToList();

        // Pass 1: declarations (what each variable is).
        foreach (var statement in statements)
        {
            foreach (var (name, expression) in DeclaredOrRoot(statement))
            {
                var nodeId = DeclareResource(context, appHost, expression);
                if (nodeId is not null && name is not null)
                {
                    variables[name] = nodeId;
                }
            }
        }

        foreach (var resource in context.Graph.OfKind(NodeKind.Resource).ToList())
        {
            if (resource.Properties.Remove("parentVariable", out var parent) && variables.TryGetValue(parent, out var parentId)
                && context.Graph.Find(parentId) is { } parentNode)
            {
                resource.Properties["parent"] = parentNode.Name;
            }
        }

        // Pass 2: relations (WithReference and helpers that take other resources as arguments).
        foreach (var statement in statements)
        {
            foreach (var (name, expression) in DeclaredOrRoot(statement))
            {
                var (root, calls) = SyntaxHelpers.FlattenChain(expression);
                var subject = DeclareResource(context, appHost, expression, createNodes: false)
                    ?? (root is IdentifierNameSyntax id ? variables.GetValueOrDefault(id.Identifier.ValueText) : null)
                    ?? (name is not null ? variables.GetValueOrDefault(name) : null);
                if (subject is null)
                {
                    continue;
                }

                foreach (var call in calls)
                {
                    var method = SyntaxHelpers.InvokedName(call)?.Identifier.ValueText ?? string.Empty;
                    if (IgnoredRelations.Contains(method) || method.StartsWith("Add", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var argument in call.ArgumentList.Arguments)
                    {
                        if (argument.Expression is IdentifierNameSyntax reference
                            && variables.TryGetValue(reference.Identifier.ValueText, out var target)
                            && target != subject)
                        {
                            var evidence = new Evidence(call.SyntaxTree.FilePath, SyntaxHelpers.Line(call), "aspire", call.ToString().Split('\n')[0].Trim());
                            AddRelation(context, subject, target, method == "WithReference" ? 1.0 : 0.7, evidence);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<(string? Name, ExpressionSyntax Expression)> DeclaredOrRoot(Microsoft.CodeAnalysis.SyntaxNode statement)
    {
        if (statement is LocalDeclarationStatementSyntax local)
        {
            foreach (var variable in local.Declaration.Variables.Where(v => v.Initializer is not null))
            {
                yield return (variable.Identifier.ValueText, variable.Initializer!.Value);
            }
        }
        else if (statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
        {
            yield return (null, invocation);
        }
    }

    /// <summary>Returns the node id of the resource created by the first Add* call in the chain.</summary>
    private static string? DeclareResource(AnalysisContext context, ProjectInfo appHost, ExpressionSyntax expression, bool createNodes = true)
    {
        var (_, calls) = SyntaxHelpers.FlattenChain(expression);
        var add = calls.FirstOrDefault(c => SyntaxHelpers.InvokedName(c)?.Identifier.ValueText.StartsWith("Add", StringComparison.Ordinal) == true);
        if (add is null)
        {
            return null;
        }

        var nameSyntax = SyntaxHelpers.InvokedName(add)!;
        var method = nameSyntax.Identifier.ValueText;
        var arguments = add.ArgumentList.Arguments.Select(a => SyntaxHelpers.LiteralString(a.Expression)).ToList();
        var resourceName = arguments.FirstOrDefault();
        if (resourceName is null || !ResourceName().IsMatch(resourceName))
        {
            // Not a resource (e.g. YARP route patterns such as "/api/{**catch-all}").
            return null;
        }

        var evidence = new Evidence(add.SyntaxTree.FilePath, SyntaxHelpers.Line(add), "aspire", add.ToString().Split('\n')[0].Trim());
        ProjectInfo? project = null;
        if (method is "AddProject" or "AddDotnetProject" or "AddCSharpApp")
        {
            var genericProject = SyntaxHelpers.GenericArguments(nameSyntax).FirstOrDefault();
            if (genericProject is not null)
            {
                project = FindProjectByAspireName(context, genericProject);
            }
            else if (arguments.Skip(1).FirstOrDefault() is { } path)
            {
                project = FindProjectByPath(context, appHost, path);
            }
        }

        if (project is not null)
        {
            if (!createNodes)
            {
                return NodeIds.Service(resourceName);
            }

            return AddService(context, resourceName, project, "aspire", evidence).NodeId;
        }

        var id = NodeIds.Resource(resourceName);
        if (createNodes)
        {
            var node = context.Graph.AddNode(id, NodeKind.Resource, resourceName, evidence: evidence);
            node.Properties["type"] = method[3..];
            if (SyntaxHelpers.RootIdentifier(expression) is { } parent && method == "AddDatabase")
            {
                node.Properties["parentVariable"] = parent;
            }
        }

        return id;
    }

    private static void AddRelation(AnalysisContext context, string subject, string target, double confidence, Evidence evidence)
    {
        var subjectIsService = subject.StartsWith("service:", StringComparison.Ordinal);
        var targetIsService = target.StartsWith("service:", StringComparison.Ordinal);
        if (subjectIsService && targetIsService)
        {
            context.Graph.AddEdge(subject, target, EdgeKind.DependsOn, confidence, evidence);
        }
        else if (subjectIsService)
        {
            context.Graph.AddEdge(subject, target, EdgeKind.UsesResource, confidence, evidence);
        }
        else if (targetIsService)
        {
            // A gateway/proxy resource routing to services (e.g. YARP): treat the gateway as depending on them.
            context.Graph.AddEdge(subject, target, EdgeKind.DependsOn, confidence, evidence);
        }
    }

    private static void DiscoverCompose(AnalysisContext context)
    {
        foreach (var file in RepositoryFiles.Enumerate(context.Root, "docker-compose*.yml", "docker-compose*.yaml", "compose*.yml", "compose*.yaml"))
        {
            YamlStream yaml;
            try
            {
                yaml = new YamlStream();
                using var reader = new StreamReader(file);
                yaml.Load(reader);
            }
            catch (YamlDotNet.Core.YamlException)
            {
                context.Graph.Warnings.Add($"Could not parse {Relative(context, file)}");
                continue;
            }

            if (yaml.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode)
                || servicesNode is not YamlMappingNode services)
            {
                continue;
            }

            var composeDirectory = Path.GetDirectoryName(file)!;
            var relativeFile = Relative(context, file);
            var names = new List<string>();
            foreach (var (key, value) in services.Children)
            {
                var name = ((YamlScalarNode)key).Value!;
                names.Add(name);
                if (value is not YamlMappingNode service)
                {
                    continue;
                }

                var evidence = new Evidence(relativeFile, (int)key.Start.Line, "compose");
                var project = ComposeProject(context, composeDirectory, service);
                if (project is not null)
                {
                    AddService(context, name, project, "compose", evidence);
                }
                else if (Scalar(service, "image") is { } image)
                {
                    var node = context.Graph.AddNode(NodeIds.Resource(name), NodeKind.Resource, name, evidence: evidence);
                    node.Properties["type"] = image.Split(':')[0].Split('/')[^1];
                }
            }

            foreach (var (key, value) in services.Children)
            {
                if (value is not YamlMappingNode service || !service.Children.TryGetValue(new YamlScalarNode("depends_on"), out var dependsOn))
                {
                    continue;
                }

                var subject = ResolveComposeName(context, ((YamlScalarNode)key).Value!);
                var targets = dependsOn switch
                {
                    YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value!),
                    YamlMappingNode mapping => mapping.Children.Keys.OfType<YamlScalarNode>().Select(s => s.Value!),
                    _ => [],
                };
                foreach (var target in targets)
                {
                    if (subject is not null && ResolveComposeName(context, target) is { } targetId)
                    {
                        AddRelation(context, subject, targetId, 0.9, new Evidence(relativeFile, (int)key.Start.Line, "compose"));
                    }
                }
            }
        }
    }

    private static string? ResolveComposeName(AnalysisContext context, string name)
    {
        if (context.Graph.Nodes.ContainsKey(NodeIds.Service(name)))
        {
            return NodeIds.Service(name);
        }

        return context.Graph.Nodes.ContainsKey(NodeIds.Resource(name)) ? NodeIds.Resource(name) : null;
    }

    private static ProjectInfo? ComposeProject(AnalysisContext context, string composeDirectory, YamlMappingNode service)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("build"), out var build))
        {
            return null;
        }

        string? contextPath;
        string? dockerfile = null;
        if (build is YamlScalarNode scalar)
        {
            contextPath = scalar.Value;
        }
        else if (build is YamlMappingNode mapping)
        {
            contextPath = Scalar(mapping, "context");
            dockerfile = Scalar(mapping, "dockerfile");
        }
        else
        {
            return null;
        }

        var buildDirectory = Path.GetFullPath(Path.Combine(composeDirectory, contextPath ?? "."));
        var lookIn = dockerfile is not null
            ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(buildDirectory, dockerfile)))!
            : buildDirectory;

        var candidates = context.Projects
            .Where(p => !p.IsTest && (p.Directory == lookIn || p.Directory.StartsWith(lookIn + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault(p => p.IsExecutable && p.Directory == lookIn);
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    private static ServiceInfo AddService(AnalysisContext context, string name, ProjectInfo project, string discoveredBy, Evidence evidence)
    {
        var existing = context.Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var service = new ServiceInfo { Name = name.ToLowerInvariant(), Entry = project, DiscoveredBy = discoveredBy };
        context.Services.Add(service);
        var node = context.Graph.AddNode(service.NodeId, NodeKind.Service, service.Name, service.Name, evidence);
        node.Properties["project"] = project.Name;
        node.Properties["path"] = project.RelativeDirectory;
        node.Properties["discoveredBy"] = discoveredBy;
        return service;
    }

    private static ProjectInfo? FindProjectByAspireName(AnalysisContext context, string aspireName)
    {
        // Aspire generates Projects.Catalog_API for Catalog.API.csproj.
        var normalized = aspireName.Replace("Projects.", string.Empty, StringComparison.Ordinal);
        return context.Projects.FirstOrDefault(p =>
            string.Equals(p.Name.Replace('.', '_').Replace('-', '_'), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(p.CsprojPath).Replace('.', '_').Replace('-', '_'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectInfo? FindProjectByPath(AnalysisContext context, ProjectInfo appHost, string path)
    {
        var full = Path.GetFullPath(Path.Combine(appHost.Directory, path.Replace('\\', Path.DirectorySeparatorChar)));
        return context.Projects.FirstOrDefault(p => p.CsprojPath == full || p.Directory == full.TrimEnd(Path.DirectorySeparatorChar));
    }

    internal static string KebabCase(string projectName)
    {
        var chars = new List<char>();
        for (var i = 0; i < projectName.Length; i++)
        {
            var c = projectName[i];
            if (c is '.' or '_' or ' ')
            {
                chars.Add('-');
            }
            else if (char.IsUpper(c) && i > 0 && char.IsLower(projectName[i - 1]))
            {
                chars.Add('-');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(char.ToLowerInvariant(c));
            }
        }

        return new string(chars.ToArray()).Replace("--", "-", StringComparison.Ordinal);
    }

    private static string Relative(AnalysisContext context, string path) => RepositoryFiles.Relative(context.Root, path);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z][\w.-]*$")]
    private static partial System.Text.RegularExpressions.Regex ResourceName();
}

using Ekc.Graph;

namespace Ekc.Extractors.DotNet;

/// <summary>A deployable unit: an orchestrated project (Aspire/Compose) or a standalone executable project.</summary>
internal sealed class ServiceInfo
{
    public required string Name { get; init; }

    public required ProjectInfo Entry { get; init; }

    /// <summary>"aspire", "compose" or "project".</summary>
    public required string DiscoveredBy { get; init; }

    /// <summary>The entry project plus every project it references, transitively.</summary>
    public HashSet<ProjectInfo> Scope { get; } = [];

    public string NodeId => NodeIds.Service(Name);

    public override string ToString() => Name;
}

/// <summary>Shared state for one extraction run.</summary>
internal sealed class AnalysisContext(string root, SystemGraph graph, List<ProjectInfo> projects)
{
    private readonly Dictionary<ServiceInfo, ScopeIndex> _scopes = [];

    public string Root { get; } = root;

    public SystemGraph Graph { get; } = graph;

    public List<ProjectInfo> Projects { get; } = projects;

    public List<ServiceInfo> Services { get; } = [];

    /// <summary>Names of integration-event types (topics), across the repository.</summary>
    public HashSet<string> IntegrationEvents { get; } = new(StringComparer.Ordinal);

    public ScopeIndex Scope(ServiceInfo service)
    {
        if (!_scopes.TryGetValue(service, out var index))
        {
            index = new ScopeIndex(service.Scope);
            _scopes[service] = index;
        }

        return index;
    }

    public void ResolveScopes()
    {
        var byPath = Projects.ToDictionary(p => p.CsprojPath, StringComparer.Ordinal);
        foreach (var service in Services)
        {
            var pending = new Stack<ProjectInfo>([service.Entry]);
            while (pending.Count > 0)
            {
                var project = pending.Pop();
                if (!service.Scope.Add(project))
                {
                    continue;
                }

                foreach (var reference in project.ProjectReferences)
                {
                    if (byPath.TryGetValue(reference, out var referenced))
                    {
                        pending.Push(referenced);
                    }
                }
            }
        }
    }

    /// <summary>Re-reads method lists after synthetic methods (endpoint lambdas) were added.</summary>
    public void RefreshScopes()
    {
        foreach (var scope in _scopes.Values)
        {
            scope.Refresh();
        }
    }

    /// <summary>The service a project is the entry point of, if exactly one.</summary>
    public ServiceInfo? OwningService(ProjectInfo project) => Services.SingleOrDefault(s => s.Entry == project);

    public string MethodNodeId(CodeMethod method)
    {
        if (!Graph.Nodes.ContainsKey(method.Id))
        {
            var node = Graph.AddNode(new Node
            {
                Id = method.Id,
                Kind = NodeKind.Method,
                Name = method.DisplayName,
                Service = OwningService(method.Project)?.Name,
                File = method.File,
                StartLine = method.StartLine,
                EndLine = method.EndLine,
                Evidence = [new Evidence(method.File, method.StartLine, "roslyn")],
            });
            node.Properties["project"] = method.Project.Name;
            var services = Services.Where(s => s.Scope.Contains(method.Project)).Select(s => s.Name).ToList();
            node.Service ??= services.Count == 1 ? services[0] : null;
            if (services.Count > 0)
            {
                node.Properties["services"] = string.Join(',', services);
            }
            node.Properties["fullName"] = method.Type is null ? method.Name : $"{method.Type.FullName}.{method.Name}";
        }

        return method.Id;
    }
}

/// <summary>Lookups over the projects visible to one service.</summary>
internal sealed class ScopeIndex
{
    public ScopeIndex(IEnumerable<ProjectInfo> projects)
    {
        Projects = projects.ToList();
        Types = Projects.SelectMany(p => p.Types).ToList();
        Methods = Projects.SelectMany(p => p.Methods).ToList();
        TypesByName = Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        MethodsByName = Methods.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public List<ProjectInfo> Projects { get; }

    public List<CodeType> Types { get; }

    public List<CodeMethod> Methods { get; private set; }

    public Dictionary<string, List<CodeType>> TypesByName { get; }

    public Dictionary<string, List<CodeMethod>> MethodsByName { get; private set; }

    /// <summary>Types with the given simple name, plus types that implement or extend it.</summary>
    public IEnumerable<CodeType> TypesNamedOrImplementing(string simpleName)
    {
        foreach (var type in Types)
        {
            if (type.Name == simpleName || type.BaseTypeNames.Contains(simpleName))
            {
                yield return type;
            }
        }
    }

    public void Refresh()
    {
        Methods = Projects.SelectMany(p => p.Methods).ToList();
        MethodsByName = Methods.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }
}

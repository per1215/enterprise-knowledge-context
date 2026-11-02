using System.Text.RegularExpressions;
using Ekc.Graph;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>
/// Service-to-service calls: typed <c>HttpClient</c>s registered with a base address that names another service
/// (Aspire/Compose service discovery, e.g. <c>http://catalog-api</c>) and gRPC clients registered with <c>AddGrpcClient</c>.
/// </summary>
internal static partial class HttpClientExtractor
{
    private static readonly Dictionary<string, string> VerbMethods = new(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetFromJsonAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetStreamAsync"] = "GET", ["GetByteArrayAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["DeleteAsync"] = "DELETE", ["DeleteFromJsonAsync"] = "DELETE",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["SendAsync"] = "SEND",
    };

    private sealed record ClientTarget(string Service, string BasePath, bool IsGrpc);

    public static void Extract(AnalysisContext context)
    {
        foreach (var service in context.Services)
        {
            var scope = context.Scope(service);
            var clients = Registrations(context, scope);
            if (clients.Count == 0)
            {
                continue;
            }

            foreach (var type in scope.Types)
            {
                if (clients.TryGetValue(type.Name, out var target) && !target.IsGrpc)
                {
                    foreach (var method in type.Methods)
                    {
                        ExtractHttpCalls(context, method, target);
                    }
                }

                var grpcMembers = type.MemberTypes
                    .Where(m => clients.TryGetValue(SyntaxHelpers.SimpleTypeName(m.Value), out var t) && t.IsGrpc)
                    .ToDictionary(m => m.Key, m => clients[SyntaxHelpers.SimpleTypeName(m.Value)], StringComparer.Ordinal);
                if (grpcMembers.Count > 0)
                {
                    foreach (var method in type.Methods)
                    {
                        ExtractGrpcCalls(context, method, grpcMembers);
                    }
                }
            }
        }
    }

    /// <summary>Client type simple name → target service, from AddHttpClient&lt;T&gt; / AddGrpcClient&lt;T&gt; registrations.</summary>
    private static Dictionary<string, ClientTarget> Registrations(AnalysisContext context, ScopeIndex scope)
    {
        var clients = new Dictionary<string, ClientTarget>(StringComparer.Ordinal);
        foreach (var method in scope.Methods)
        {
            foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
            {
                if (SyntaxHelpers.InvokedName(invocation) is not GenericNameSyntax generic
                    || generic.Identifier.ValueText is not ("AddHttpClient" or "AddGrpcClient"))
                {
                    continue;
                }

                var isGrpc = generic.Identifier.ValueText == "AddGrpcClient";
                var clientType = SyntaxHelpers.GenericArguments(generic).LastOrDefault();
                if (clientType is null)
                {
                    continue;
                }

                // The address is assigned in the configure lambda or in a chained ConfigureHttpClient(...).
                var searchRoot = invocation.Parent?.Parent is InvocationExpressionSyntax chained ? (Microsoft.CodeAnalysis.SyntaxNode)chained : invocation;
                var address = searchRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left.ToString().EndsWith("Address", StringComparison.Ordinal))
                    .Select(a => a.Right switch
                    {
                        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression,
                        var other => other,
                    })
                    .Select(e => SyntaxHelpers.ResolveString(e, method, method.Type))
                    .FirstOrDefault(v => v is not null);

                if (address is null)
                {
                    context.Graph.Warnings.Add($"{method.File}:{SyntaxHelpers.Line(invocation)} {clientType}: base address not found in code (configuration?)");
                    continue;
                }

                if (ParseServiceUrl(address) is { } parsed)
                {
                    clients[clientType] = new ClientTarget(parsed.Service, parsed.BasePath, isGrpc);
                }
            }
        }

        return clients;
    }

    private static void ExtractHttpCalls(AnalysisContext context, CodeMethod method, ClientTarget target)
    {
        foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
        {
            if (SyntaxHelpers.InvokedName(invocation) is not { } name
                || !VerbMethods.TryGetValue(name.Identifier.ValueText, out var verb)
                || invocation.ArgumentList.Arguments.Count == 0)
            {
                continue;
            }

            var receiver = SyntaxHelpers.Receiver(invocation)?.ToString() ?? string.Empty;
            if (!receiver.Contains("client", StringComparison.OrdinalIgnoreCase) && !receiver.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var urlExpression = invocation.ArgumentList.Arguments[0].Expression;
            if (verb == "SEND")
            {
                (verb, urlExpression) = ResolveRequestMessage(method, urlExpression);
                if (urlExpression is null)
                {
                    continue;
                }
            }

            var template = SyntaxHelpers.ResolveString(urlExpression, method, method.Type);
            var evidence = new Evidence(method.File, SyntaxHelpers.Line(invocation), "http-client", invocation.ToString().Split('\n')[0].Trim());
            var from = context.MethodNodeId(method);
            if (template is null || template.StartsWith("{}", StringComparison.Ordinal))
            {
                context.Graph.AddEdge(from, NodeIds.Service(target.Service), EdgeKind.CallsService, 0.6, evidence,
                    new Dictionary<string, string> { ["verb"] = verb, ["reason"] = "URL built at runtime" });
                continue;
            }

            var path = template.StartsWith('/') ? template : EndpointExtractor.Combine(target.BasePath, template);
            LinkCall(context, from, target.Service, verb, path, evidence);
        }
    }

    /// <summary><c>new HttpRequestMessage(HttpMethod.Post, url)</c> (inline or via a local variable) → (verb, url expression).</summary>
    private static (string Verb, ExpressionSyntax? Url) ResolveRequestMessage(CodeMethod method, ExpressionSyntax argument)
    {
        var creation = argument as BaseObjectCreationExpressionSyntax;
        if (creation is null && argument is IdentifierNameSyntax identifier)
        {
            creation = method.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == identifier.Identifier.ValueText)?.Initializer?.Value as BaseObjectCreationExpressionSyntax;
        }

        var arguments = creation?.ArgumentList?.Arguments;
        if (arguments is not { Count: >= 2 })
        {
            return ("GET", null);
        }

        var verb = arguments.Value[0].Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText.ToUpperInvariant() : "GET";
        return (verb, arguments.Value[1].Expression);
    }

    private static void ExtractGrpcCalls(AnalysisContext context, CodeMethod method, Dictionary<string, ClientTarget> members)
    {
        foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
        {
            if (SyntaxHelpers.Receiver(invocation) is not IdentifierNameSyntax receiver
                || !members.TryGetValue(receiver.Identifier.ValueText, out var target)
                || SyntaxHelpers.InvokedName(invocation) is not { } name)
            {
                continue;
            }

            var memberType = SyntaxHelpers.SimpleTypeName(method.Type!.MemberTypes[receiver.Identifier.ValueText]);
            var grpcService = memberType.EndsWith("Client", StringComparison.Ordinal) ? memberType[..^"Client".Length] : memberType;
            var rpc = name.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal) ? name.Identifier.ValueText[..^"Async".Length] : name.Identifier.ValueText;
            var endpointId = NodeIds.GrpcEndpoint(target.Service, grpcService, rpc);
            var evidence = new Evidence(method.File, SyntaxHelpers.Line(invocation), "grpc-client", invocation.ToString().Split('\n')[0].Trim());
            var from = context.MethodNodeId(method);
            if (context.Graph.Nodes.ContainsKey(endpointId))
            {
                context.Graph.AddEdge(from, endpointId, EdgeKind.CallsEndpoint, 0.95, evidence);
            }
            else
            {
                context.Graph.AddEdge(from, NodeIds.Service(target.Service), EdgeKind.CallsService, 0.7, evidence,
                    new Dictionary<string, string> { ["verb"] = "GRPC", ["path"] = $"{grpcService}/{rpc}" });
            }
        }
    }

    /// <summary>Links a call to the best-matching endpoint of the target service, or to the service itself.</summary>
    internal static void LinkCall(AnalysisContext context, string from, string targetService, string verb, string path, Evidence evidence)
    {
        var normalized = NodeIds.NormalizePath(path);
        var serviceId = NodeIds.Service(targetService);
        var best = context.Graph.Outgoing(serviceId, EdgeKind.Exposes)
            .Select(e => context.Graph.Find(e.To)!)
            .Where(n => n.Properties.GetValueOrDefault("verb") == verb)
            .Select(n => (Node: n, Score: RouteMatcher.Score(normalized, n.Properties.GetValueOrDefault("normalizedPath") ?? string.Empty)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (best.Count > 0 && (best.Count == 1 || best[0].Score > best[1].Score))
        {
            context.Graph.AddEdge(from, best[0].Node.Id, EdgeKind.CallsEndpoint, 0.9, evidence);
        }
        else if (best.Count > 1)
        {
            foreach (var (node, _) in best.Where(b => b.Score == best[0].Score))
            {
                context.Graph.AddEdge(from, node.Id, EdgeKind.CallsEndpoint, 0.5, evidence);
            }
        }
        else if (context.Graph.Nodes.ContainsKey(serviceId))
        {
            context.Graph.AddEdge(from, serviceId, EdgeKind.CallsService, 0.6, evidence,
                new Dictionary<string, string> { ["verb"] = verb, ["path"] = "/" + path.Trim('/'), ["reason"] = "no matching endpoint found" });
        }
        else
        {
            var resourceId = NodeIds.Resource(targetService);
            var resource = context.Graph.AddNode(resourceId, NodeKind.Resource, targetService, evidence: evidence);
            resource.Properties.TryAdd("type", "external-http");
            context.Graph.AddEdge(from, resourceId, EdgeKind.CallsService, 0.8, evidence,
                new Dictionary<string, string> { ["verb"] = verb, ["path"] = "/" + path.Trim('/') });
        }
    }

    /// <summary>"https+http://catalog-api/api/" → ("catalog-api", "api"). Returns null for non-URLs.</summary>
    internal static (string Service, string BasePath)? ParseServiceUrl(string url)
    {
        var match = ServiceUrl().Match(url.Trim());
        if (!match.Success)
        {
            return null;
        }

        return (match.Groups["host"].Value.ToLowerInvariant(), match.Groups["path"].Value.Trim('/'));
    }

    [GeneratedRegex(@"^[a-z+]+://(?<host>[^/:?{}]+)(:\d+)?(?<path>/[^?]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceUrl();
}

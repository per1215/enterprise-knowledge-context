using Ekc.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>Endpoints exposed by each service: minimal APIs (with route groups), MVC controllers, gRPC services, YARP forwarders.</summary>
internal static class EndpointExtractor
{
    private static readonly Dictionary<string, string> MinimalApiVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    private static readonly Dictionary<string, string> ControllerVerbs = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE", ["HttpPatch"] = "PATCH",
    };

    public static void Extract(AnalysisContext context)
    {
        foreach (var service in context.Services)
        {
            var scope = context.Scope(service);
            foreach (var method in scope.Methods.ToList())
            {
                ExtractMinimalApis(context, service, scope, method);
            }

            foreach (var type in scope.Types)
            {
                ExtractController(context, service, type);
            }

            ExtractGrpc(context, service, scope);
        }

        context.RefreshScopes();
    }

    private static void ExtractMinimalApis(AnalysisContext context, ServiceInfo service, ScopeIndex scope, CodeMethod method)
    {
        // variable → route prefix accumulated through MapGroup.
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameterNames = method.ParameterTypes.Keys.ToHashSet();

        foreach (var node in method.BodyNodes())
        {
            if (node is VariableDeclaratorSyntax { Initializer.Value: var initializer } declarator
                && SyntaxHelpers.FlattenChain(initializer).Calls.Any(c => SyntaxHelpers.InvokedName(c)?.Identifier.ValueText == "MapGroup"))
            {
                prefixes[declarator.Identifier.ValueText] = Prefix(initializer, prefixes, method);
                continue;
            }

            if (node is not InvocationExpressionSyntax invocation || SyntaxHelpers.InvokedName(invocation) is not { } name)
            {
                continue;
            }

            var methodName = name.Identifier.ValueText;
            if (methodName == "MapForwarder")
            {
                ExtractForwarder(context, service, method, invocation);
                continue;
            }

            if (!MinimalApiVerbs.TryGetValue(methodName, out var verb) || invocation.ArgumentList.Arguments.Count < 2)
            {
                continue;
            }

            var route = SyntaxHelpers.ResolveString(invocation.ArgumentList.Arguments[0].Expression, method, method.Type);
            if (route is null)
            {
                continue;
            }

            var receiver = SyntaxHelpers.Receiver(invocation);
            var prefix = receiver is null ? string.Empty : Prefix(receiver, prefixes, method);
            var path = Combine(prefix, route);
            var endpointId = AddEndpoint(context, service, verb, path, invocation, "minimal-api");

            var handler = ResolveHandler(context, scope, method, invocation.ArgumentList.Arguments[1].Expression);
            if (handler is not null)
            {
                context.Graph.AddEdge(endpointId, context.MethodNodeId(handler), EdgeKind.ImplementedBy, 1.0,
                    new Evidence(method.File, SyntaxHelpers.Line(invocation), "minimal-api"));
            }
        }
    }

    /// <summary>Route prefix of an expression such as <c>app.MapGroup("api").MapGroup("orders")</c> or a group variable.</summary>
    private static string Prefix(ExpressionSyntax expression, Dictionary<string, string> prefixes, CodeMethod method)
    {
        var (root, calls) = SyntaxHelpers.FlattenChain(expression);
        var prefix = root is IdentifierNameSyntax id && prefixes.TryGetValue(id.Identifier.ValueText, out var known) ? known : string.Empty;
        foreach (var call in calls)
        {
            if (SyntaxHelpers.InvokedName(call)?.Identifier.ValueText == "MapGroup" && call.ArgumentList.Arguments.Count > 0)
            {
                prefix = Combine(prefix, SyntaxHelpers.ResolveString(call.ArgumentList.Arguments[0].Expression, method, method.Type) ?? "{}");
            }
        }

        return prefix;
    }

    private static CodeMethod? ResolveHandler(AnalysisContext context, ScopeIndex scope, CodeMethod method, ExpressionSyntax handler)
    {
        switch (handler)
        {
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
            {
                var name = handler is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : ((IdentifierNameSyntax)handler).Identifier.ValueText;
                var sameType = method.Type?.Methods.Where(x => x.Name == name).ToList() ?? [];
                if (sameType.Count > 0)
                {
                    return sameType[0];
                }

                return scope.MethodsByName.TryGetValue(name, out var candidates) && candidates.Count == 1 ? candidates[0] : null;
            }

            case AnonymousFunctionExpressionSyntax lambda:
            {
                var line = SyntaxHelpers.Line(lambda);
                var typeName = method.Type?.FullName ?? "Program";
                var id = NodeIds.Method(method.Project.Name, typeName, $"lambda@{line}", 0);
                var existing = method.Project.Methods.FirstOrDefault(x => x.Id == id);
                if (existing is not null)
                {
                    return existing;
                }

                var parameters = lambda switch
                {
                    ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.ToList(),
                    SimpleLambdaExpressionSyntax s => [s.Parameter],
                    _ => [],
                };
                var synthetic = new CodeMethod
                {
                    Id = id,
                    Name = $"lambda@{line}",
                    Type = method.Type,
                    Project = method.Project,
                    File = method.File,
                    StartLine = line,
                    EndLine = SyntaxHelpers.EndLine(lambda),
                    ParameterCount = parameters.Count,
                    Body = lambda,
                    IsSynthetic = true,
                    ParameterTypes = parameters.Where(p => p.Type is not null)
                        .GroupBy(p => p.Identifier.ValueText)
                        .ToDictionary(g => g.Key, g => g.First().Type!.ToString(), StringComparer.Ordinal),
                };
                method.Project.Methods.Add(synthetic);
                method.Project.SeparatelyModelled.Add(lambda);
                return synthetic;
            }

            default:
                return null;
        }
    }

    private static void ExtractController(AnalysisContext context, ServiceInfo service, CodeType type)
    {
        var syntax = type.Syntax;
        var isController = type.Name.EndsWith("Controller", StringComparison.Ordinal)
            && (type.BaseTypeNames.Any(b => b is "Controller" or "ControllerBase") || SyntaxHelpers.Attributes(syntax).Any(a => SyntaxHelpers.AttributeName(a) == "ApiController"));
        if (!isController)
        {
            return;
        }

        var controllerName = type.Name[..^"Controller".Length];
        var classRoute = SyntaxHelpers.Attributes(syntax).Where(a => SyntaxHelpers.AttributeName(a) == "Route")
            .Select(SyntaxHelpers.FirstAttributeString).FirstOrDefault(r => r is not null) ?? string.Empty;

        foreach (var method in type.Methods)
        {
            var declaration = (MethodDeclarationSyntax)method.Body;
            if (!SyntaxHelpers.HasModifier(declaration, SyntaxKind.PublicKeyword))
            {
                continue;
            }

            var attributes = SyntaxHelpers.Attributes(declaration).ToList();
            var methodRoute = attributes.Where(a => SyntaxHelpers.AttributeName(a) == "Route").Select(SyntaxHelpers.FirstAttributeString).FirstOrDefault(r => r is not null);
            foreach (var attribute in attributes)
            {
                if (!ControllerVerbs.TryGetValue(SyntaxHelpers.AttributeName(attribute), out var verb))
                {
                    continue;
                }

                var template = SyntaxHelpers.FirstAttributeString(attribute) ?? methodRoute ?? string.Empty;
                var path = template.StartsWith('/') || template.StartsWith('~') ? template.TrimStart('~') : Combine(classRoute, template);
                path = path.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                    .Replace("[action]", method.Name, StringComparison.OrdinalIgnoreCase);
                var endpointId = AddEndpoint(context, service, verb, path, attribute, "controller");
                context.Graph.AddEdge(endpointId, context.MethodNodeId(method), EdgeKind.ImplementedBy, 1.0,
                    new Evidence(method.File, method.StartLine, "controller"));
            }
        }
    }

    private static void ExtractGrpc(AnalysisContext context, ServiceInfo service, ScopeIndex scope)
    {
        foreach (var method in scope.Methods)
        {
            foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
            {
                if (SyntaxHelpers.InvokedName(invocation) is not GenericNameSyntax { Identifier.ValueText: "MapGrpcService" } generic)
                {
                    continue;
                }

                var implementation = SyntaxHelpers.GenericArguments(generic).FirstOrDefault();
                if (implementation is null || !scope.TypesByName.TryGetValue(implementation, out var types))
                {
                    continue;
                }

                foreach (var type in types)
                {
                    var grpcBase = type.BaseTypeNames.FirstOrDefault(b => b.EndsWith("Base", StringComparison.Ordinal));
                    var grpcService = grpcBase is null ? type.Name : grpcBase[..^"Base".Length];
                    foreach (var grpcMethod in type.Methods.Where(m => SyntaxHelpers.HasModifier((MethodDeclarationSyntax)m.Body, SyntaxKind.OverrideKeyword)))
                    {
                        var id = NodeIds.GrpcEndpoint(service.Name, grpcService, grpcMethod.Name);
                        var node = context.Graph.AddNode(id, NodeKind.Endpoint, $"GRPC {grpcService}/{grpcMethod.Name}", service.Name,
                            new Evidence(grpcMethod.File, grpcMethod.StartLine, "grpc"));
                        node.Properties["verb"] = "GRPC";
                        node.Properties["path"] = $"{grpcService}/{grpcMethod.Name}";
                        node.Properties["normalizedPath"] = $"{grpcService}/{grpcMethod.Name}".ToLowerInvariant();
                        context.Graph.AddEdge(service.NodeId, id, EdgeKind.Exposes, 1.0, new Evidence(method.File, SyntaxHelpers.Line(invocation), "grpc"));
                        context.Graph.AddEdge(id, context.MethodNodeId(grpcMethod), EdgeKind.ImplementedBy, 1.0, new Evidence(grpcMethod.File, grpcMethod.StartLine, "grpc"));
                    }
                }
            }
        }
    }

    /// <summary><c>app.MapForwarder("/images/{id}", "http://catalog-api", "/api/catalog/items/{id}/pic")</c>.</summary>
    private static void ExtractForwarder(AnalysisContext context, ServiceInfo service, CodeMethod method, InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments.Select(a => SyntaxHelpers.ResolveString(a.Expression, method, method.Type)).ToList();
        if (arguments.Count < 2 || arguments[0] is null || arguments[1] is null)
        {
            return;
        }

        var endpointId = AddEndpoint(context, service, "GET", arguments[0]!, invocation, "yarp-forwarder");
        var target = HttpClientExtractor.ParseServiceUrl(arguments[1]!);
        if (target is null)
        {
            return;
        }

        var targetPath = arguments.Count > 2 && arguments[2] is not null ? arguments[2]! : arguments[0]!;
        var evidence = new Evidence(method.File, SyntaxHelpers.Line(invocation), "yarp-forwarder");
        HttpClientExtractor.LinkCall(context, endpointId, target.Value.Service, "GET", Combine(target.Value.BasePath, targetPath), evidence);
    }

    private static string AddEndpoint(AnalysisContext context, ServiceInfo service, string verb, string path, SyntaxNode at, string extractor)
    {
        var display = "/" + path.Trim('/');
        var id = NodeIds.Endpoint(service.Name, verb, display);
        var evidence = new Evidence(at.SyntaxTree.FilePath, SyntaxHelpers.Line(at), extractor, at.ToString().Split('\n')[0].Trim());
        var node = context.Graph.AddNode(id, NodeKind.Endpoint, $"{verb} {display}", service.Name, evidence);
        node.Properties["verb"] = verb;
        node.Properties["path"] = display;
        node.Properties["normalizedPath"] = NodeIds.NormalizePath(display);
        context.Graph.AddEdge(service.NodeId, id, EdgeKind.Exposes, 1.0, evidence);
        return id;
    }

    internal static string Combine(string prefix, string route)
    {
        var a = prefix.Trim().Trim('/');
        var b = route.Trim().Trim('/');
        return a.Length == 0 ? b : b.Length == 0 ? a : $"{a}/{b}";
    }
}

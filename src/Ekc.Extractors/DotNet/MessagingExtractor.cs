using Ekc.Graph;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>
/// Asynchronous and in-process messaging:
/// integration events (topics) published and subscribed across services, and in-process dispatch of
/// commands, requests, notifications and domain events to their handlers (MediatR-style).
/// </summary>
internal static class MessagingExtractor
{
    /// <summary>Handler interfaces whose first generic argument is an integration event delivered through a broker.</summary>
    private static readonly HashSet<string> IntegrationHandlerInterfaces = new(StringComparer.Ordinal)
    {
        "IIntegrationEventHandler", "IConsumer",
    };

    /// <summary>Handler interfaces for in-process dispatch.</summary>
    private static readonly HashSet<string> InProcessHandlerInterfaces = new(StringComparer.Ordinal)
    {
        "IRequestHandler", "INotificationHandler", "ICommandHandler", "IQueryHandler", "IDomainEventHandler", "IHandleMessages", "IMessageHandler",
    };

    private static readonly string[] HandlerMethodNames = ["Handle", "HandleAsync", "Consume", "ConsumeAsync"];

    public static void Extract(AnalysisContext context)
    {
        DiscoverIntegrationEvents(context);

        foreach (var service in context.Services)
        {
            var scope = context.Scope(service);
            ExtractSubscriptions(context, service, scope);
            var inProcessHandlers = InProcessHandlers(scope);

            foreach (var method in scope.Methods)
            {
                foreach (var creation in method.BodyNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var typeName = SyntaxHelpers.SimpleTypeName(creation.Type);
                    var evidence = new Evidence(method.File, SyntaxHelpers.Line(creation), "messaging", creation.ToString().Split('\n')[0].Trim());

                    if (context.IntegrationEvents.Contains(typeName))
                    {
                        var topic = AddTopic(context, typeName);
                        context.Graph.AddEdge(context.MethodNodeId(method), topic, EdgeKind.Publishes, 0.85, evidence);
                    }
                    else if (inProcessHandlers.TryGetValue(typeName, out var handlers))
                    {
                        foreach (var handler in handlers.Where(h => h != method))
                        {
                            context.Graph.AddEdge(context.MethodNodeId(method), context.MethodNodeId(handler), EdgeKind.Dispatches, 0.85, evidence,
                                new Dictionary<string, string> { ["message"] = typeName });
                        }
                    }
                }
            }
        }
    }

    private static void DiscoverIntegrationEvents(AnalysisContext context)
    {
        foreach (var type in context.Projects.SelectMany(p => p.Types))
        {
            if (type.BaseTypeNames.Any(b => b.EndsWith("IntegrationEvent", StringComparison.Ordinal)))
            {
                context.IntegrationEvents.Add(type.Name);
            }

            foreach (var baseType in type.BaseTypes)
            {
                if (IntegrationHandlerInterfaces.Contains(SyntaxHelpers.SimpleTypeName(baseType.Type))
                    && SyntaxHelpers.GenericArguments(baseType.Type).FirstOrDefault() is { } message)
                {
                    context.IntegrationEvents.Add(message);
                }
            }
        }

        foreach (var invocation in context.Projects.SelectMany(p => p.Methods).SelectMany(m => m.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()))
        {
            if (SyntaxHelpers.InvokedName(invocation) is GenericNameSyntax { Identifier.ValueText: "AddSubscription" or "Subscribe" } generic
                && SyntaxHelpers.GenericArguments(generic).FirstOrDefault() is { } message)
            {
                context.IntegrationEvents.Add(message);
            }
        }

        // The base types themselves are not topics.
        context.IntegrationEvents.Remove("IntegrationEvent");
    }

    private static void ExtractSubscriptions(AnalysisContext context, ServiceInfo service, ScopeIndex scope)
    {
        var subscriptions = new List<(string Event, string Handler, Evidence Evidence)>();

        foreach (var method in scope.Methods)
        {
            foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
            {
                if (SyntaxHelpers.InvokedName(invocation) is GenericNameSyntax { Identifier.ValueText: "AddSubscription" or "Subscribe" } generic)
                {
                    var arguments = SyntaxHelpers.GenericArguments(generic);
                    if (arguments.Count >= 2)
                    {
                        subscriptions.Add((arguments[0], arguments[1], new Evidence(method.File, SyntaxHelpers.Line(invocation), "messaging", invocation.ToString().Trim())));
                    }
                }
            }
        }

        // Handlers discovered by interface (e.g. MassTransit IConsumer<T>) subscribe even without explicit registration.
        foreach (var type in scope.Types)
        {
            foreach (var baseType in type.BaseTypes)
            {
                if (IntegrationHandlerInterfaces.Contains(SyntaxHelpers.SimpleTypeName(baseType.Type))
                    && SyntaxHelpers.GenericArguments(baseType.Type).FirstOrDefault() is { } message
                    && type.Project == service.Entry)
                {
                    subscriptions.Add((message, type.Name, new Evidence(type.File, SyntaxHelpers.Line(type.Syntax), "messaging", baseType.ToString())));
                }
            }
        }

        foreach (var (eventName, handlerName, evidence) in subscriptions)
        {
            var topic = AddTopic(context, eventName);
            context.Graph.AddEdge(service.NodeId, topic, EdgeKind.Subscribes, 1.0, evidence);
            if (!scope.TypesByName.TryGetValue(handlerName, out var handlerTypes))
            {
                continue;
            }

            foreach (var handlerType in handlerTypes)
            {
                var handle = handlerType.Methods.FirstOrDefault(m => HandlerMethodNames.Contains(m.Name));
                if (handle is not null)
                {
                    context.Graph.AddEdge(topic, context.MethodNodeId(handle), EdgeKind.HandledBy, 1.0, evidence,
                        new Dictionary<string, string> { ["service"] = service.Name });
                }
            }
        }
    }

    /// <summary>Message type → its in-process handler methods within the scope.</summary>
    private static Dictionary<string, List<CodeMethod>> InProcessHandlers(ScopeIndex scope)
    {
        var handlers = new Dictionary<string, List<CodeMethod>>(StringComparer.Ordinal);
        foreach (var type in scope.Types)
        {
            foreach (var baseType in type.BaseTypes)
            {
                if (!InProcessHandlerInterfaces.Contains(SyntaxHelpers.SimpleTypeName(baseType.Type))
                    || SyntaxHelpers.GenericArguments(baseType.Type).FirstOrDefault() is not { } message)
                {
                    continue;
                }

                var handle = type.Methods.FirstOrDefault(m => HandlerMethodNames.Contains(m.Name));
                if (handle is not null)
                {
                    if (!handlers.TryGetValue(message, out var list))
                    {
                        handlers[message] = list = [];
                    }

                    list.Add(handle);
                }
            }
        }

        return handlers;
    }

    private static string AddTopic(AnalysisContext context, string eventName)
    {
        var id = NodeIds.Topic(eventName);
        context.Graph.AddNode(id, NodeKind.Topic, eventName);
        return id;
    }
}

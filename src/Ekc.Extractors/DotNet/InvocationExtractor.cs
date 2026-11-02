using Ekc.Graph;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>
/// An approximate call graph without compiling: invocations are resolved by receiver type when the receiver is a
/// field, property or parameter with a declared type in scope (high confidence), otherwise by unique method name.
/// </summary>
internal static class InvocationExtractor
{
    /// <summary>Names too generic to resolve by name alone (BCL/LINQ/framework collisions).</summary>
    private static readonly HashSet<string> AmbiguousNames = new(StringComparer.Ordinal)
    {
        "ToString", "Equals", "GetHashCode", "Dispose", "DisposeAsync", "Add", "AddRange", "Remove", "Get", "Set", "Find", "Update",
        "Select", "Where", "Any", "All", "First", "FirstOrDefault", "Single", "SingleOrDefault", "ToList", "ToListAsync", "ToArray",
        "Count", "Contains", "Include", "Handle", "Send", "Publish", "Execute", "Run", "Invoke", "Configure", "Map", "Create", "Build",
        "Validate", "Log", "LogInformation", "LogWarning", "LogError", "SaveChanges", "SaveChangesAsync", "Main", "Clear", "Load",
    };

    public static void Extract(AnalysisContext context)
    {
        foreach (var service in context.Services)
        {
            var scope = context.Scope(service);
            foreach (var method in scope.Methods)
            {
                foreach (var invocation in method.BodyNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = SyntaxHelpers.InvokedName(invocation)?.Identifier.ValueText;
                    if (name is null || !scope.MethodsByName.TryGetValue(name, out var byName))
                    {
                        continue;
                    }

                    var (targets, confidence) = Resolve(scope, method, invocation, name, byName);
                    foreach (var target in targets.Where(t => t != method))
                    {
                        context.Graph.AddEdge(context.MethodNodeId(method), context.MethodNodeId(target), EdgeKind.Invokes, confidence,
                            new Evidence(method.File, SyntaxHelpers.Line(invocation), "call-graph", invocation.ToString().Split('\n')[0].Trim()));
                    }
                }
            }
        }
    }

    private static (List<CodeMethod> Targets, double Confidence) Resolve(
        ScopeIndex scope, CodeMethod caller, InvocationExpressionSyntax invocation, string name, List<CodeMethod> byName)
    {
        var argumentCount = invocation.ArgumentList.Arguments.Count;
        var receiver = SyntaxHelpers.Receiver(invocation);

        // this.M() / M() → same type first.
        if (receiver is null or ThisExpressionSyntax && caller.Type is not null)
        {
            var own = caller.Type.Methods.Where(m => m.Name == name).ToList();
            if (own.Count > 0)
            {
                return (Prefer(own, argumentCount), 0.9);
            }
        }

        // field/parameter of a known type: _repository.Add(order) → OrderRepository.Add
        var receiverName = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.ValueText,
            _ => null,
        };
        var receiverType = receiverName is null
            ? null
            : caller.ParameterTypes.GetValueOrDefault(receiverName) ?? caller.Type?.MemberTypes.GetValueOrDefault(receiverName);
        if (receiverType is not null)
        {
            var simple = SyntaxHelpers.SimpleTypeName(receiverType);
            var typed = scope.TypesNamedOrImplementing(simple)
                .SelectMany(t => t.Methods.Where(m => m.Name == name))
                .ToList();
            if (typed.Count > 0)
            {
                var preferred = Prefer(typed, argumentCount);
                return (preferred, preferred.Count == 1 ? 0.85 : 0.6);
            }

            // The receiver's type is known and is not ours (framework/library type): do not guess by name.
            return ([], 0);
        }

        if (AmbiguousNames.Contains(name))
        {
            return ([], 0);
        }

        var candidates = Prefer(byName, argumentCount);
        return candidates.Count switch
        {
            1 => (candidates, 0.7),
            2 or 3 => (candidates, 0.4),
            _ => ([], 0),
        };
    }

    private static List<CodeMethod> Prefer(List<CodeMethod> candidates, int argumentCount)
    {
        var exact = candidates.Where(c => c.ParameterCount == argumentCount).ToList();
        return exact.Count > 0 ? exact : candidates;
    }
}

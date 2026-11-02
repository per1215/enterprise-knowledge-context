using Ekc.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>
/// Business rules declared in code: <c>[BusinessRule("Name")]</c> on a method, or a
/// <c>// ekc:rule Name</c> comment directly above it.
/// </summary>
internal static class RuleExtractor
{
    private const string CommentMarker = "ekc:rule";

    public static void Extract(AnalysisContext context)
    {
        foreach (var method in context.Projects.Where(p => !p.IsAppHost).SelectMany(p => p.Methods).Where(m => m.Body is MethodDeclarationSyntax))
        {
            var declaration = (MethodDeclarationSyntax)method.Body;
            foreach (var attribute in SyntaxHelpers.Attributes(declaration).Where(a => SyntaxHelpers.AttributeName(a) == "BusinessRule"))
            {
                if (SyntaxHelpers.FirstAttributeString(attribute) is { } name)
                {
                    AddRule(context, name, method, new Evidence(method.File, SyntaxHelpers.Line(attribute), "rule-attribute", attribute.ToString()), 1.0);
                }
            }

            foreach (var trivia in declaration.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
            {
                var text = trivia.ToString().TrimStart('/').Trim();
                if (text.StartsWith(CommentMarker, StringComparison.OrdinalIgnoreCase))
                {
                    var name = text[CommentMarker.Length..].Trim();
                    if (name.Length > 0)
                    {
                        var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        AddRule(context, name, method, new Evidence(method.File, line, "rule-comment", text), 0.95);
                    }
                }
            }
        }
    }

    internal static void AddRule(AnalysisContext context, string name, CodeMethod method, Evidence evidence, double confidence)
    {
        var id = NodeIds.Rule(name);
        context.Graph.AddNode(id, NodeKind.BusinessRule, name, evidence: evidence);
        context.Graph.AddEdge(id, context.MethodNodeId(method), EdgeKind.EnforcedBy, confidence, evidence);
    }
}

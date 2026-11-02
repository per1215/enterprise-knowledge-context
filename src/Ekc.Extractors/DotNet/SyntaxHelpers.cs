using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

internal static class SyntaxHelpers
{
    public static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    /// <summary>"Namespace.Outer.Inner.List&lt;T&gt;" → "List".</summary>
    public static string SimpleTypeName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => SimpleTypeName(q.Right),
        AliasQualifiedNameSyntax a => SimpleTypeName(a.Name),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        NullableTypeSyntax n => SimpleTypeName(n.ElementType),
        _ => type.ToString(),
    };

    /// <summary>"Basket.BasketClient" / "BasketClient?" / "List&lt;X&gt;" → "BasketClient" / "BasketClient" / "List".</summary>
    public static string SimpleTypeName(string typeText)
    {
        var text = typeText.Trim().TrimEnd('?');
        var generic = text.IndexOf('<');
        if (generic >= 0)
        {
            text = text[..generic];
        }

        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    /// <summary>Generic arguments of a base type such as <c>IRequestHandler&lt;CreateOrder, bool&gt;</c>.</summary>
    public static IReadOnlyList<string> GenericArguments(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => GenericArguments(q.Right),
        GenericNameSyntax g => g.TypeArgumentList.Arguments.Select(SimpleTypeName).ToList(),
        _ => [],
    };

    public static IReadOnlyList<string> GenericArguments(SimpleNameSyntax name) =>
        name is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Select(SimpleTypeName).ToList() : [];

    /// <summary>The method name of an invocation: <c>a.b.Foo(...)</c>, <c>Foo(...)</c>, <c>a?.Foo(...)</c>, <c>Foo&lt;T&gt;(...)</c>.</summary>
    public static SimpleNameSyntax? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name,
        SimpleNameSyntax s => s,
        MemberBindingExpressionSyntax b => b.Name,
        _ => null,
    };

    /// <summary>The receiver of <c>receiver.Method(...)</c>, or null.</summary>
    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax m ? m.Expression : null;

    /// <summary>
    /// Flattens a fluent chain <c>root.A(..).B(..).C(..)</c> into [A, B, C] and returns the root expression.
    /// </summary>
    public static (ExpressionSyntax Root, List<InvocationExpressionSyntax> Calls) FlattenChain(ExpressionSyntax expression)
    {
        var calls = new List<InvocationExpressionSyntax>();
        var current = expression;
        while (true)
        {
            if (current is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax member)
            {
                calls.Add(invocation);
                current = member.Expression;
            }
            else if (current is MemberAccessExpressionSyntax access && access.Expression is InvocationExpressionSyntax)
            {
                current = access.Expression;
            }
            else if (current is AwaitExpressionSyntax awaitExpression)
            {
                current = awaitExpression.Expression;
            }
            else
            {
                break;
            }
        }

        calls.Reverse();
        return (current, calls);
    }

    public static string? RootIdentifier(ExpressionSyntax expression) => FlattenChain(expression).Root switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m when m.Expression is ThisExpressionSyntax => m.Name.Identifier.ValueText,
        _ => null,
    };

    public static string? LiteralString(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    /// <summary>
    /// Best-effort string value of an expression. Unknown interpolation holes and operands become <c>{}</c>
    /// (a route parameter), so the result is a route template. Returns null when nothing is known.
    /// </summary>
    public static string? ResolveString(ExpressionSyntax? expression, CodeMethod? method, CodeType? type, int depth = 0)
    {
        if (expression is null || depth > 4)
        {
            return null;
        }

        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;

            case InterpolatedStringExpressionSyntax interpolated:
            {
                var parts = new List<string>();
                var anyKnown = false;
                foreach (var content in interpolated.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text)
                    {
                        parts.Add(text.TextToken.ValueText);
                        anyKnown = true;
                    }
                    else if (content is InterpolationSyntax hole)
                    {
                        var value = ResolveString(hole.Expression, method, type, depth + 1);
                        if (value is not null && !value.Contains("{}"))
                        {
                            parts.Add(value);
                            anyKnown = true;
                        }
                        else
                        {
                            parts.Add("{}");
                        }
                    }
                }

                return anyKnown ? string.Concat(parts) : null;
            }

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
            {
                var left = ResolveString(binary.Left, method, type, depth + 1);
                var right = ResolveString(binary.Right, method, type, depth + 1);
                return left is null && right is null ? null : (left ?? "{}") + (right ?? "{}");
            }

            case ParenthesizedExpressionSyntax parenthesized:
                return ResolveString(parenthesized.Expression, method, type, depth + 1);

            case IdentifierNameSyntax identifier:
            {
                var name = identifier.Identifier.ValueText;
                if (method is not null)
                {
                    var local = method.Body.DescendantNodes()
                        .OfType<VariableDeclaratorSyntax>()
                        .FirstOrDefault(v => v.Identifier.ValueText == name && v.Initializer is not null);
                    if (local is not null)
                    {
                        return ResolveString(local.Initializer!.Value, method, type, depth + 1);
                    }
                }

                return type?.StringMembers.GetValueOrDefault(name);
            }

            case MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax or IdentifierNameSyntax:
                return type?.StringMembers.GetValueOrDefault(member.Name.Identifier.ValueText);

            default:
                return null;
        }
    }

    public static bool HasModifier(MemberDeclarationSyntax member, SyntaxKind kind) => member.Modifiers.Any(m => m.IsKind(kind));

    public static IEnumerable<AttributeSyntax> Attributes(MemberDeclarationSyntax member) =>
        member.AttributeLists.SelectMany(l => l.Attributes);

    public static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static string? FirstAttributeString(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Select(a => LiteralString(a.Expression)).FirstOrDefault(s => s is not null);
}

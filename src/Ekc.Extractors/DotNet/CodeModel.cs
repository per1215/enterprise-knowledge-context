using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>A C# project as seen from its source files.</summary>
internal sealed class ProjectInfo
{
    public required string Name { get; init; }

    public required string CsprojPath { get; init; }

    public required string Directory { get; init; }

    /// <summary>Repository-relative directory with forward slashes.</summary>
    public required string RelativeDirectory { get; init; }

    public string? Sdk { get; init; }

    public bool IsExecutable { get; set; }

    public bool IsTest { get; init; }

    public bool IsAppHost { get; set; }

    public List<string> ProjectReferences { get; } = [];

    public List<SyntaxTree> Trees { get; } = [];

    public List<CodeType> Types { get; } = [];

    public List<CodeMethod> Methods { get; } = [];

    /// <summary>Lambdas and local functions modelled as their own methods; enclosing methods skip their bodies.</summary>
    public HashSet<SyntaxNode> SeparatelyModelled { get; } = [];

    public override string ToString() => Name;
}

/// <summary>A class, record, struct or interface declaration.</summary>
internal sealed class CodeType
{
    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required ProjectInfo Project { get; init; }

    public required string File { get; init; }

    public required TypeDeclarationSyntax Syntax { get; init; }

    /// <summary>Base types and interfaces as written, e.g. <c>IRequestHandler&lt;CreateOrderCommand, bool&gt;</c>.</summary>
    public List<BaseTypeSyntax> BaseTypes { get; } = [];

    /// <summary>Fields, properties and constructor parameters: member name → declared type text.</summary>
    public Dictionary<string, string> MemberTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>String constants and readonly string fields with literal initialisers.</summary>
    public Dictionary<string, string> StringMembers { get; } = new(StringComparer.Ordinal);

    public List<CodeMethod> Methods { get; } = [];

    public IEnumerable<string> BaseTypeNames => BaseTypes.Select(b => SyntaxHelpers.SimpleTypeName(b.Type));

    public override string ToString() => FullName;
}

/// <summary>A method, top-level program, or endpoint lambda: anything with a body we analyse.</summary>
internal sealed class CodeMethod
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CodeType? Type { get; init; }

    public required ProjectInfo Project { get; init; }

    public required string File { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required int ParameterCount { get; init; }

    /// <summary>The declaration, lambda or compilation unit whose body is analysed.</summary>
    public required SyntaxNode Body { get; init; }

    /// <summary>Parameter name → type text.</summary>
    public Dictionary<string, string> ParameterTypes { get; init; } = new(StringComparer.Ordinal);

    public bool IsSynthetic { get; init; }

    /// <summary>Nodes of this method's body, excluding nested lambdas/local functions that are methods of their own.</summary>
    public IEnumerable<SyntaxNode> BodyNodes() =>
        Body.DescendantNodes(n => n == Body || !Project.SeparatelyModelled.Contains(n))
            .Where(n => n == Body || !Project.SeparatelyModelled.Contains(n));

    /// <summary>"Type.Method" — the display name used in reports.</summary>
    public string DisplayName => Type is null ? (Name == "<Main>" ? "Program.<Main>" : $"Program.{Name}") : $"{Type.Name}.{Name}";

    public override string ToString() => DisplayName;
}

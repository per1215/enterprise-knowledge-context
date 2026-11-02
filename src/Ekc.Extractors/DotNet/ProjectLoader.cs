using System.Collections.Concurrent;
using System.Xml.Linq;
using Ekc.Extractors.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>Finds .csproj files, parses their C# sources (syntax only) and builds the type/method model.</summary>
internal static class ProjectLoader
{
    private static readonly string[] TestPackages = ["xunit", "xunit.v3", "nunit", "mstest.testframework", "microsoft.net.test.sdk", "mstest"];

    public static List<ProjectInfo> Load(string root, bool includeTests)
    {
        var csprojs = RepositoryFiles.Enumerate(root, "*.csproj").ToList();
        var projectDirectories = csprojs.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).ToHashSet(StringComparer.Ordinal);
        var projects = new ConcurrentBag<ProjectInfo>();

        Parallel.ForEach(csprojs, csproj =>
        {
            var project = ReadProject(root, csproj);
            if (project.IsTest && !includeTests)
            {
                return;
            }

            foreach (var file in SourceFiles(project.Directory, projectDirectories))
            {
                var text = File.ReadAllText(file);
                var relative = RepositoryFiles.Relative(root, file);
                project.Trees.Add(CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), relative));
            }

            BuildCodeModel(project);
            project.IsAppHost |= project.Trees.Any(t => t.GetText().ToString().Contains("DistributedApplication.CreateBuilder", StringComparison.Ordinal));
            project.IsExecutable |= project.Trees.Any(t => t.GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().Any());
            projects.Add(project);
        });

        return projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static ProjectInfo ReadProject(string root, string csproj)
    {
        var document = XDocument.Load(csproj);
        var sdk = document.Root?.Attribute("Sdk")?.Value;
        var properties = document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(e => e.Name.LocalName).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        var packages = document.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (e.Attribute("Include")?.Value ?? string.Empty).ToLowerInvariant()).ToList();
        var directory = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        var name = properties.GetValueOrDefault("AssemblyName") is { Length: > 0 } assemblyName && !assemblyName.Contains('$')
            ? assemblyName
            : Path.GetFileNameWithoutExtension(csproj);

        var isTest = packages.Any(p => TestPackages.Contains(p))
            || string.Equals(properties.GetValueOrDefault("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);

        var project = new ProjectInfo
        {
            Name = name,
            CsprojPath = Path.GetFullPath(csproj),
            Directory = directory,
            RelativeDirectory = RepositoryFiles.Relative(root, directory),
            Sdk = sdk,
            IsTest = isTest,
            IsAppHost = (sdk?.Contains("Aspire.AppHost", StringComparison.OrdinalIgnoreCase) ?? false)
                || packages.Contains("aspire.hosting.apphost")
                || string.Equals(properties.GetValueOrDefault("IsAspireHost"), "true", StringComparison.OrdinalIgnoreCase),
            IsExecutable = (sdk?.Contains(".Web", StringComparison.OrdinalIgnoreCase) ?? false)
                || (sdk?.Contains(".Worker", StringComparison.OrdinalIgnoreCase) ?? false)
                || string.Equals(properties.GetValueOrDefault("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase),
        };

        foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                project.ProjectReferences.Add(Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        return project;
    }

    private static IEnumerable<string> SourceFiles(string projectDirectory, HashSet<string> projectDirectories)
    {
        foreach (var file in RepositoryFiles.Enumerate(projectDirectory, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip files that belong to a nested project, and EF migrations (generated, noisy).
            var directory = Path.GetDirectoryName(file)!;
            if (directory.Split(Path.DirectorySeparatorChar).Contains("Migrations"))
            {
                continue;
            }

            var owner = directory;
            while (owner.Length >= projectDirectory.Length && !projectDirectories.Contains(owner))
            {
                owner = Path.GetDirectoryName(owner)!;
            }

            if (owner == projectDirectory)
            {
                yield return file;
            }
        }
    }

    private static void BuildCodeModel(ProjectInfo project)
    {
        foreach (var tree in project.Trees)
        {
            var root = tree.GetCompilationUnitRoot();

            // using Alias = Some.Namespace.Type; → Alias resolves to Type.
            var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias is not null && u.NamespaceOrType is not null)
                .GroupBy(u => u.Alias!.Name.Identifier.ValueText)
                .ToDictionary(g => g.Key, g => g.First().NamespaceOrType!.ToString(), StringComparer.Ordinal);

            // Top-level statements become a synthetic Program.<Main>.
            var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count > 0)
            {
                project.Methods.Add(new CodeMethod
                {
                    Id = Graph.NodeIds.Method(project.Name, "Program", "<Main>", 0),
                    Name = "<Main>",
                    Type = null,
                    Project = project,
                    File = tree.FilePath,
                    StartLine = SyntaxHelpers.Line(globals[0]),
                    EndLine = SyntaxHelpers.EndLine(globals[^1]),
                    ParameterCount = 0,
                    Body = root,
                    IsSynthetic = true,
                });

                // Local functions in top-level programs (common minimal-API handler style) are methods of Program.
                foreach (var local in globals.Select(g => g.Statement).OfType<LocalFunctionStatementSyntax>())
                {
                    project.SeparatelyModelled.Add(local);
                    project.Methods.Add(new CodeMethod
                    {
                        Id = Graph.NodeIds.Method(project.Name, "Program", local.Identifier.ValueText, local.ParameterList.Parameters.Count),
                        Name = local.Identifier.ValueText,
                        Type = null,
                        Project = project,
                        File = tree.FilePath,
                        StartLine = SyntaxHelpers.Line(local),
                        EndLine = SyntaxHelpers.EndLine(local),
                        ParameterCount = local.ParameterList.Parameters.Count,
                        Body = local,
                        ParameterTypes = local.ParameterList.Parameters
                            .Where(p => p.Type is not null)
                            .GroupBy(p => p.Identifier.ValueText)
                            .ToDictionary(g => g.Key, g => Unalias(g.First().Type!.ToString(), aliases), StringComparer.Ordinal),
                    });
                }
            }

            foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var type = new CodeType
                {
                    Name = declaration.Identifier.ValueText,
                    FullName = FullName(declaration),
                    Project = project,
                    File = tree.FilePath,
                    Syntax = declaration,
                };

                if (declaration.BaseList is not null)
                {
                    type.BaseTypes.AddRange(declaration.BaseList.Types);
                }

                CollectMembers(declaration, type, aliases);

                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var codeMethod = new CodeMethod
                    {
                        Id = Graph.NodeIds.Method(project.Name, type.FullName, method.Identifier.ValueText, method.ParameterList.Parameters.Count),
                        Name = method.Identifier.ValueText,
                        Type = type,
                        Project = project,
                        File = tree.FilePath,
                        StartLine = SyntaxHelpers.Line(method),
                        EndLine = SyntaxHelpers.EndLine(method),
                        ParameterCount = method.ParameterList.Parameters.Count,
                        Body = method,
                        ParameterTypes = method.ParameterList.Parameters
                            .Where(p => p.Type is not null)
                            .GroupBy(p => p.Identifier.ValueText)
                            .ToDictionary(g => g.Key, g => Unalias(g.First().Type!.ToString(), aliases), StringComparer.Ordinal),
                    };
                    type.Methods.Add(codeMethod);
                    project.Methods.Add(codeMethod);
                }

                project.Types.Add(type);
            }
        }
    }

    private static string Unalias(string typeText, Dictionary<string, string> aliases) =>
        aliases.TryGetValue(typeText.TrimEnd('?'), out var target) ? target : typeText;

    private static void CollectMembers(TypeDeclarationSyntax declaration, CodeType type, Dictionary<string, string> aliases)
    {
        if (declaration.ParameterList is not null)
        {
            foreach (var parameter in declaration.ParameterList.Parameters.Where(p => p.Type is not null))
            {
                type.MemberTypes[parameter.Identifier.ValueText] = Unalias(parameter.Type!.ToString(), aliases);
            }
        }

        foreach (var member in declaration.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        type.MemberTypes[variable.Identifier.ValueText] = Unalias(field.Declaration.Type.ToString(), aliases);
                        if (SyntaxHelpers.LiteralString(variable.Initializer?.Value) is { } value)
                        {
                            type.StringMembers[variable.Identifier.ValueText] = value;
                        }
                    }

                    break;
                case PropertyDeclarationSyntax property:
                    type.MemberTypes[property.Identifier.ValueText] = Unalias(property.Type.ToString(), aliases);
                    if (SyntaxHelpers.LiteralString(property.Initializer?.Value ?? property.ExpressionBody?.Expression) is { } propertyValue)
                    {
                        type.StringMembers[property.Identifier.ValueText] = propertyValue;
                    }

                    break;
                case ConstructorDeclarationSyntax constructor:
                    foreach (var parameter in constructor.ParameterList.Parameters.Where(p => p.Type is not null))
                    {
                        type.MemberTypes.TryAdd(parameter.Identifier.ValueText, Unalias(parameter.Type!.ToString(), aliases));
                    }

                    // Fields assigned from constructor parameters keep the parameter's type: _repo = repo.
                    foreach (var assignment in constructor.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (assignment.Left is IdentifierNameSyntax or MemberAccessExpressionSyntax
                            && assignment.Right is IdentifierNameSyntax source
                            && constructor.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.ValueText == source.Identifier.ValueText)?.Type is { } parameterType)
                        {
                            var target = assignment.Left is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : ((IdentifierNameSyntax)assignment.Left).Identifier.ValueText;
                            type.MemberTypes[target] = Unalias(parameterType.ToString(), aliases);
                        }
                    }

                    break;
            }
        }
    }

    private static string FullName(TypeDeclarationSyntax declaration)
    {
        var names = new Stack<string>();
        SyntaxNode? current = declaration;
        while (current is not null)
        {
            switch (current)
            {
                case TypeDeclarationSyntax t:
                    names.Push(t.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    names.Push(ns.Name.ToString());
                    break;
            }

            current = current.Parent;
        }

        return string.Join('.', names);
    }
}

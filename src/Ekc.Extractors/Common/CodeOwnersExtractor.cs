using System.Text.RegularExpressions;
using Ekc.Extractors.DotNet;
using Ekc.Graph;

namespace Ekc.Extractors.Common;

/// <summary>Service ownership from CODEOWNERS (GitHub/GitLab/Azure DevOps syntax; last matching rule wins).</summary>
internal static class CodeOwnersExtractor
{
    private static readonly string[] Locations = ["CODEOWNERS", ".github/CODEOWNERS", "docs/CODEOWNERS", ".gitlab/CODEOWNERS"];

    public static void Extract(AnalysisContext context)
    {
        var file = Locations.Select(l => Path.Combine(context.Root, l)).FirstOrDefault(File.Exists);
        if (file is null)
        {
            return;
        }

        var relativeFile = RepositoryFiles.Relative(context.Root, file);
        var rules = new List<(Regex Pattern, string[] Owners, int Line)>();
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                rules.Add((ToRegex(parts[0]), parts[1..], i + 1));
            }
        }

        foreach (var service in context.Services)
        {
            // Match the project file path, so "src/Claims.Api/" and "*.csproj"-style rules both work.
            var path = RepositoryFiles.Relative(context.Root, service.Entry.CsprojPath);
            var match = rules.LastOrDefault(r => r.Pattern.IsMatch(path));
            if (match.Owners is null)
            {
                continue;
            }

            foreach (var owner in match.Owners)
            {
                var teamId = NodeIds.Team(owner);
                var evidence = new Evidence(relativeFile, match.Line, "codeowners", lines[match.Line - 1].Trim());
                context.Graph.AddNode(teamId, NodeKind.Team, owner.TrimStart('@'), evidence: evidence);
                context.Graph.AddEdge(service.NodeId, teamId, EdgeKind.OwnedBy, 1.0, evidence);
            }
        }
    }

    /// <summary>CODEOWNERS glob → regex over repository-relative paths.</summary>
    internal static Regex ToRegex(string pattern)
    {
        var anchored = pattern.StartsWith('/');
        var p = pattern.Trim('/');
        var directory = pattern.EndsWith('/');
        var regex = Regex.Escape(p)
            .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        var prefix = anchored || p.Contains('/') ? "^" : "(^|.*/)";
        var suffix = directory ? "/.*$" : "(/.*)?$";
        return new Regex(prefix + regex + suffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

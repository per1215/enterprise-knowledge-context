using System.Text.RegularExpressions;
using Ekc.Graph;

namespace Ekc.Analysis;

public sealed record ChangedFile(string Path, IReadOnlyList<(int Start, int End)> Ranges);

/// <summary>Maps a unified diff (<c>git diff -U0</c>) to the methods whose bodies changed.</summary>
public static partial class DiffMapper
{
    public static IReadOnlyList<ChangedFile> Parse(string unifiedDiff)
    {
        var files = new List<ChangedFile>();
        string? current = null;
        var ranges = new List<(int, int)>();

        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                Flush();
                var path = line[4..].Trim();
                current = path == "/dev/null" ? null : path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                continue;
            }

            var hunk = HunkHeader().Match(line);
            if (hunk.Success && current is not null)
            {
                var start = int.Parse(hunk.Groups["start"].Value);
                var count = hunk.Groups["count"].Success ? int.Parse(hunk.Groups["count"].Value) : 1;
                // Pure deletions (count 0) still touch the code around the start line.
                ranges.Add((start, Math.Max(start, start + count - 1)));
            }
        }

        Flush();
        return files;

        void Flush()
        {
            if (current is not null && ranges.Count > 0)
            {
                files.Add(new ChangedFile(current, ranges.ToList()));
            }

            ranges.Clear();
        }
    }

    /// <summary>Methods (and services, for changes outside any method) touched by the diff.</summary>
    public static (IReadOnlyList<Node> Methods, IReadOnlyList<string> UnmappedFiles) Map(SystemGraph graph, IReadOnlyList<ChangedFile> files)
    {
        var methods = new List<Node>();
        var unmapped = new List<string>();
        var byFile = graph.OfKind(NodeKind.Method).Where(m => m.File is not null)
            .GroupBy(m => m.File!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!byFile.TryGetValue(file.Path, out var candidates))
            {
                unmapped.Add(file.Path);
                continue;
            }

            var hit = candidates
                .Where(m => file.Ranges.Any(r => r.Start <= m.EndLine && r.End >= m.StartLine))
                // Prefer the innermost method when a lambda sits inside a method.
                .OrderBy(m => (m.EndLine - m.StartLine) ?? int.MaxValue)
                .ToList();
            if (hit.Count == 0)
            {
                unmapped.Add(file.Path);
            }

            methods.AddRange(hit.Where(h => !methods.Contains(h)));
        }

        return (methods, unmapped);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex HunkHeader();
}

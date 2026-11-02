namespace Ekc.Extractors.Common;

/// <summary>Enumerates repository files, skipping build output, dependencies and VCS folders.</summary>
internal static class RepositoryFiles
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".ekc", "packages", "TestResults", "artifacts", "dist", "wwwroot",
    };

    public static IEnumerable<string> Enumerate(string root, params string[] patterns)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirectories)
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    yield return file;
                }
            }
        }
    }

    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

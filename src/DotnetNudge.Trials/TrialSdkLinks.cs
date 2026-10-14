namespace DotnetNudge.Trials;

internal static class TrialSdkLinks
{
    public static (List<string> Directories, List<string> Files, List<(string Link, string Target)> Links)
        InspectPreparedRoot(string root)
    {
        TrialPaths.Child(root, "dotnet");
        var directories = new List<string>();
        var files = new List<string>();
        var links = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null) links.Add(entry);
                else if (Directory.Exists(entry))
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else if (info.Exists) files.Add(entry);
                else throw new TrialException($"Unsupported prepared SDK entry: {entry}");
            }
        return (directories, files, links.Select(link => (link, ResolveFile(root, link))).ToList());
    }

    public static string ResolveFile(string root, string path,
        IReadOnlyDictionary<string, string>? archiveLinks = null)
    {
        var baseRoot = Path.GetFullPath(root);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = path;
        while (true)
        {
            current = Path.GetFullPath(current);
            if (!current.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !visited.Add(current))
                throw new TrialException($"SDK link escapes root or forms a cycle: {path}");
            for (var parent = Path.GetDirectoryName(current); parent is not null && parent != baseRoot;
                 parent = Path.GetDirectoryName(parent))
                if (new DirectoryInfo(parent).LinkTarget is not null)
                    throw new TrialException($"SDK directory link is unsupported: {parent}");
            var link = archiveLinks is null
                ? new FileInfo(current).LinkTarget
                : archiveLinks.GetValueOrDefault(current);
            if (link is null)
            {
                if (Directory.Exists(current) || !File.Exists(current))
                    throw new TrialException($"SDK link points to a directory or missing file: {path}");
                return current;
            }
            if (Path.IsPathRooted(link) ||
                System.Text.RegularExpressions.Regex.IsMatch(link, @"^[A-Za-z]:[/\\]|^[/\\]"))
                throw new TrialException($"Absolute SDK link is unsupported: {path}");
            current = Path.Combine(Path.GetDirectoryName(current)!, link.Replace('\\', Path.DirectorySeparatorChar));
        }
    }
}

namespace DotnetNudge.Trials;

internal static class TrialPaths
{
    public static bool Overlaps(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return a.Equals(b, comparison) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, comparison) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }

    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new TrialException($"Path must be relative: {relative}");
        var fullRoot = Path.GetFullPath(root);
        for (DirectoryInfo? ancestor = new(fullRoot); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.LinkTarget is not null)
                throw new TrialException($"Symlink trial root/ancestor is unsupported: {ancestor.FullName}");
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new TrialException($"Path escapes trial root: {relative}");
        var cursor = fullRoot;
        foreach (var part in Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, part);
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                var info = (FileSystemInfo)(Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor));
                if (info.LinkTarget is not null)
                    throw new TrialException($"Symlink in trial path: {cursor}");
            }
        }
        return full;
    }

    public static void AssertNoLinks(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = (FileSystemInfo)(Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry));
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new TrialException($"Symlink/reparse point is unsupported: {entry}");
        }
    }
}

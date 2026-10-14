using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;

namespace DotnetNudge.Core;

public static class WorkspaceDiscovery
{
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "packages", "vendor", "artifacts", ".artifacts"
    };
    static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj"
    };
    static readonly HashSet<string> ConfigNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "NuGet.Config", "nuget.config", "dotnet-tools.json", "packages.lock.json"
    };

    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = Directory.Exists(current)
                ? new DirectoryInfo(current).ResolveLinkTarget(true)
                : File.Exists(current) ? new FileInfo(current).ResolveLinkTarget(true) : null;
            if (target is not null) current = target.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    static string Id(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path))).ToLowerInvariant();

    public static string ScopeId(string root) => Id(Canonical(root));

    public static string SdkScopeKey(string root, string scope)
    {
        var full = Canonical(Path.Combine(root, scope));
        var cursor = Canonical(root);
        while (true)
        {
            if (Directory.Exists(Path.Combine(cursor, ".git")) ||
                File.Exists(Path.Combine(cursor, ".git")))
            {
                var relative = Path.GetRelativePath(cursor, full).Replace('\\', '/');
                return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
                    ? full : relative;
            }
            var parent = Directory.GetParent(cursor)?.FullName;
            if (parent is null) return full;
            cursor = parent;
        }
    }

    public static (string Repository, string Worktree) Identities(string root)
    {
        var worktree = Canonical(root);
        var cursor = worktree;
        while (true)
        {
            var dotgit = Path.Combine(cursor, ".git");
            if (Directory.Exists(dotgit) || File.Exists(dotgit))
            {
                var gitdir = Directory.Exists(dotgit) ? dotgit : ParseGitDir(dotgit, cursor);
                var common = Path.Combine(gitdir, "commondir");
                var commonDir = File.Exists(common)
                    ? Path.GetFullPath(File.ReadAllText(common).Trim(), gitdir) : gitdir;
                return (Id(Canonical(commonDir)), Id(Canonical(cursor)));
            }
            var parent = Directory.GetParent(cursor)?.FullName;
            if (parent is null) break;
            cursor = parent;
        }
        return (Id(worktree), Id(worktree));
    }

    static string ParseGitDir(string dotgit, string root)
    {
        var line = File.ReadAllText(dotgit).Trim();
        if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid Git directory pointer: {dotgit}");
        return Path.GetFullPath(line["gitdir:".Length..].Trim(), root);
    }

    public static WorkspaceInventory Discover(string root)
    {
        root = Canonical(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var (repository, worktree) = Identities(root);
        var projects = new List<string>();
        var projectDeclarations = new List<ProjectDeclaration>();
        var solutions = new List<string>();
        var configurations = new List<string>();
        var declarations = new List<SdkDeclaration>();
        var coverage = new List<Coverage>();
        var visited = 0;
        void Walk(string directory, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Workspace directory depth exceeds 32.");
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (++visited > 50000)
                    throw new InvalidDataException("Workspace scan exceeds 50,000 files.");
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var name = Path.GetFileName(file);
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ProjectExtensions.Contains(Path.GetExtension(file)))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 2 * 1024 * 1024)
                            throw new InvalidDataException("Project declaration exceeds 2 MiB.");
                        using var reader = XmlReader.Create(file, new XmlReaderSettings
                        {
                            DtdProcessing = DtdProcessing.Prohibit,
                            XmlResolver = null,
                            MaxCharactersInDocument = 2 * 1024 * 1024
                        });
                        var xml = XDocument.Load(reader);
                        var sdk = xml.Root?.Attribute("Sdk")?.Value;
                        if (sdk is not null ||
                            xml.Root?.Elements().Any(e => e.Name.LocalName == "Sdk") == true)
                        {
                            projects.Add(relative);
                            projectDeclarations.Add(new(relative, sdk,
                                xml.Descendants().Where(e => e.Name.LocalName is
                                    "TargetFramework" or "TargetFrameworks")
                                    .Select(e => e.Value).ToArray()));
                        }
                        else coverage.Add(new("project:" + relative, Assessment.Partial,
                            "Non-SDK project: inventory-only; MSBuild evaluation not performed."));
                    }
                    catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException)
                    {
                        coverage.Add(new("project:" + relative, Assessment.Blocked, ex.Message));
                    }
                }
                else if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
                    solutions.Add(relative);
                if (ConfigNames.Contains(name) ||
                    name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("packages.config", StringComparison.OrdinalIgnoreCase))
                    configurations.Add(relative);
                if (name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
                {
                    configurations.Add(relative);
                    try { declarations.Add(ReadSdk(file, relative)); }
                    catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
                    { coverage.Add(new("sdk:" + relative, Assessment.Blocked, ex.Message)); }
                }
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!Excluded.Contains(Path.GetFileName(child)) &&
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    Walk(child, depth + 1);
        }
        Walk(root, 0);
        // Ancestor scopes can affect SDK selection but cannot be inferred from a nested declaration.
        var ancestor = Directory.GetParent(root);
        while (ancestor is not null)
        {
            var global = Path.Combine(ancestor.FullName, "global.json");
            if (File.Exists(global))
            {
                try { declarations.Add(ReadSdk(global, Canonical(global))); }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
                { coverage.Add(new("sdk:" + global, Assessment.Blocked, ex.Message)); }
                break;
            }
            ancestor = ancestor.Parent;
        }
        projects.Sort(StringComparer.Ordinal);
        projectDeclarations.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        solutions.Sort(StringComparer.Ordinal);
        configurations.Sort(StringComparer.Ordinal);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var executionScopes = projects.Concat(solutions)
            .Select(relative => Path.GetDirectoryName(Path.Combine(root, relative))!)
            .Append(root);
        var relevantDeclarations = executionScopes.Select(scope => declarations
            .Where(declaration =>
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(
                    Path.Combine(root, declaration.Scope)))!;
                return scope.Equals(directory, comparison) ||
                    scope.StartsWith(directory + Path.DirectorySeparatorChar, comparison);
            })
            .OrderByDescending(declaration => Path.GetDirectoryName(Path.GetFullPath(
                Path.Combine(root, declaration.Scope)))!.Length)
            .FirstOrDefault())
            .Where(declaration => declaration is not null)
            .DistinctBy(declaration => declaration!.Scope)
            .Select(declaration => declaration!)
            .ToArray();
        coverage.Add(new("nuget-resolved-graph", Assessment.Unknown,
            "Existing assets and evaluated package graphs are not checked by this foundation scan."));
        coverage.Add(new("effective-sdk", Assessment.Unknown,
            "Declared global.json settings are not an observed SDK selection."));
        return new(Contract.Version, repository, worktree, ScopeId(root), root, projects, projectDeclarations, solutions,
            configurations, relevantDeclarations, coverage);
    }

    static SdkDeclaration ReadSdk(string file, string scope)
    {
        if (new FileInfo(file).Length > 1024 * 1024)
            throw new InvalidDataException($"global.json exceeds 1 MiB: {file}");
        var raw = File.ReadAllText(file);
        using var json = JsonDocument.Parse(raw, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var sdk = json.RootElement.TryGetProperty("sdk", out var value) ? value : default;
        string? Text(string key) => sdk.ValueKind == JsonValueKind.Object &&
            sdk.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
        bool? prerelease = sdk.ValueKind == JsonValueKind.Object &&
            sdk.TryGetProperty("allowPrerelease", out var allow) &&
            allow.ValueKind is JsonValueKind.True or JsonValueKind.False ? allow.GetBoolean() : null;
        var paths = sdk.ValueKind == JsonValueKind.Object &&
            sdk.TryGetProperty("paths", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToArray() : [];
        return new(scope, Text("version"), Text("rollForward"), prerelease, paths,
            Text("workloadVersion"), raw);
    }
}

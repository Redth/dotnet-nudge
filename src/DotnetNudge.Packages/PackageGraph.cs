using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace DotnetNudge.Packages;

public enum EvidenceStatus { Complete, Partial, Unknown }
public enum GraphChangeKind { Added, Removed, Upgraded, Downgraded, RoleChanged }
public enum GraphObservationOrigin { Reconciliation, SessionEdit, EnvironmentTransition }

public sealed record PackageNode(
    string Project, string Framework, string? RuntimeIdentifier, string Id, NuGetVersion Version,
    bool Direct, string? RequestedRange, string? PackagePath, string? Source,
    IReadOnlyList<string> Dependencies, bool CentrallyManaged = false, string? ContentHash = null);

public sealed record PackageGraph(string AssetsPath, string Fingerprint, IReadOnlyList<PackageNode> Packages,
    EvidenceStatus Status, string? Diagnostic);

public sealed record GraphChange(long Sequence, GraphChangeKind Kind, PackageNode? Previous,
    PackageNode? Current, GraphObservationOrigin Origin);

public sealed class PackageGraphReader
{
    public PackageGraph Read(string assetsPath, IEnumerable<string>? declaredInputs = null)
    {
        if (!File.Exists(assetsPath))
            return Unknown(assetsPath, "Restored project.assets.json is missing; restore is required.");
        try
        {
            var before = new FileInfo(assetsPath);
            var length = before.Length;
            if (length > 64 * 1024 * 1024)
                return Unknown(assetsPath, "Assets exceed the passive read limit.");
            var modified = before.LastWriteTimeUtc;
            var bytes = File.ReadAllBytes(assetsPath);
            var after = new FileInfo(assetsPath);
            if (length != after.Length || modified != after.LastWriteTimeUtc ||
                !SHA256.HashData(bytes).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(assetsPath))))
                return Unknown(assetsPath, "Assets changed during reading; retry after restore settles.");
            if (declaredInputs?.Any(p => !File.Exists(p) || File.GetLastWriteTimeUtc(p) > modified) == true)
                return Unknown(assetsPath, "A declared project input is missing or newer than assets; restore needed.");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var schema) || schema.GetInt32() is not (3 or 4))
                return Unknown(assetsPath, "Unsupported assets schema (expected version 3 or 4).");
            // Let NuGet validate the lock-file shape as well as our version gate.
            var lockFile = new LockFileFormat().Read(assetsPath);
            if (lockFile is null || !root.TryGetProperty("targets", out var targets) ||
                !root.TryGetProperty("libraries", out var libraries))
                return Unknown(assetsPath, "Assets do not contain a complete restored graph.");
            var project = root.GetProperty("project");
            var restore = project.GetProperty("restore");
            var projectPath = restore.GetProperty("projectPath").GetString() ?? assetsPath;
            var folders = root.TryGetProperty("packageFolders", out var packageFolders)
                ? packageFolders.EnumerateObject().Select(p => p.Name).ToArray() : [];
            var direct = new Dictionary<string, Dictionary<string, (string? Range, bool Central)>>(
                StringComparer.OrdinalIgnoreCase);
            if (project.TryGetProperty("frameworks", out var frameworks))
                foreach (var framework in frameworks.EnumerateObject())
                {
                    var references = new Dictionary<string, (string?, bool)>(StringComparer.OrdinalIgnoreCase);
                    if (framework.Value.TryGetProperty("dependencies", out var dependencies))
                        foreach (var dependency in dependencies.EnumerateObject())
                            references[dependency.Name] = (dependency.Value.ValueKind == JsonValueKind.String
                                ? dependency.Value.GetString()
                                : dependency.Value.TryGetProperty("version", out var version) ? version.GetString() : null,
                                dependency.Value.ValueKind == JsonValueKind.Object &&
                                dependency.Value.TryGetProperty("versionCentrallyManaged", out var central) &&
                                central.ValueKind == JsonValueKind.True);
                    direct[framework.Name] = references;
                }
            var nodes = new List<PackageNode>();
            foreach (var target in targets.EnumerateObject())
            {
                var parts = target.Name.Split('/', 2);
                var tfm = parts[0];
                var rid = parts.Length == 2 ? parts[1] : null;
                foreach (var item in target.Value.EnumerateObject())
                {
                    var slash = item.Name.LastIndexOf('/');
                    if (slash <= 0 || !NuGetVersion.TryParse(item.Name[(slash + 1)..], out var version) ||
                        !item.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
                        continue;
                    var id = item.Name[..slash];
                    var library = libraries.GetProperty(item.Name);
                    var relative = library.TryGetProperty("path", out var path) ? path.GetString() : null;
                    if (relative is not null && (Path.IsPathRooted(relative) ||
                        relative.Replace('\\', '/').Split('/').Any(segment => segment == "..")))
                        return Unknown(assetsPath, $"Unsafe package path in assets for {id}.");
                    var packagePath = relative is null ? null : folders.Select(folder => Path.Combine(folder, relative))
                        .FirstOrDefault(Directory.Exists);
                    string? requested = null;
                    var centrallyManaged = false;
                    if (direct.TryGetValue(tfm, out var references))
                    {
                        references.TryGetValue(id, out var declaration);
                        requested = declaration.Range;
                        centrallyManaged = declaration.Central;
                    }
                    var isDirect = direct.GetValueOrDefault(tfm)?.ContainsKey(id) == true;
                    var dependencies = item.Value.TryGetProperty("dependencies", out var deps)
                        ? deps.EnumerateObject().Select(p => p.Name).ToArray() : [];
                    var contentHash = library.TryGetProperty("sha512", out var sha) ? sha.GetString() : null;
                    var source = library.TryGetProperty("source", out var feed) &&
                        feed.ValueKind == JsonValueKind.String ? feed.GetString() : null;
                    nodes.Add(new PackageNode(projectPath, tfm, rid, id, version, isDirect, requested,
                        packagePath, source, dependencies, centrallyManaged, contentHash));
                }
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new PackageGraph(assetsPath, hash, nodes, EvidenceStatus.Complete, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or KeyNotFoundException or ArgumentException or
                                   InvalidOperationException or FormatException or OverflowException)
        {
            return Unknown(assetsPath, $"Assets could not be read: {ex.Message}");
        }
    }

    private static PackageGraph Unknown(string path, string reason) =>
        new(path, "", [], EvidenceStatus.Unknown, reason);
}

public sealed class GraphTransitions
{
    private sealed record StoredNode(string Project, string Framework, string? Rid, string Id,
        string Version, bool Direct, string? Requested, string? Path, string? Source, string[] Dependencies,
        bool CentrallyManaged, string? ContentHash);
    private sealed record StoredGraph(long Sequence, StoredNode[] Nodes);

    private readonly string? _stateFile;
    private PackageGraph? _previous;
    private long _sequence;

    public GraphTransitions(string? stateFile = null)
    {
        _stateFile = stateFile;
        if (stateFile is null || !File.Exists(stateFile)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredGraph>(File.ReadAllText(stateFile))
                ?? throw new InvalidDataException("Empty transition state.");
            _sequence = stored.Sequence;
            _previous = new PackageGraph("", "", stored.Nodes.Select(p => new PackageNode(
                p.Project, p.Framework, p.Rid, p.Id, NuGetVersion.Parse(p.Version),
                p.Direct, p.Requested, p.Path, p.Source, p.Dependencies,
                p.CentrallyManaged, p.ContentHash)).ToArray(),
                EvidenceStatus.Complete, null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or
                                   NullReferenceException)
        {
            throw new InvalidDataException("Transition snapshot is corrupt; do not reset the baseline implicitly.", ex);
        }
    }

    public IReadOnlyList<GraphChange> Observe(PackageGraph graph,
        GraphObservationOrigin origin = GraphObservationOrigin.Reconciliation)
    {
        if (graph.Status != EvidenceStatus.Complete)
            return [];
        if (_previous is null)
        {
            _previous = graph;
            Save();
            return [];
        }
        var old = _previous.Packages.ToDictionary(Key);
        var current = graph.Packages.ToDictionary(Key);
        var changes = new List<GraphChange>();
        foreach (var key in old.Keys.Union(current.Keys).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            old.TryGetValue(key, out var previous);
            current.TryGetValue(key, out var next);
            if (previous is null || next is null)
                changes.Add(new GraphChange(++_sequence, previous is null
                    ? GraphChangeKind.Added : GraphChangeKind.Removed, previous, next, origin));
            else
            {
                if (previous.Version != next.Version)
                    changes.Add(new GraphChange(++_sequence, previous.Version < next.Version
                        ? GraphChangeKind.Upgraded : GraphChangeKind.Downgraded, previous, next, origin));
                if (previous.Direct != next.Direct)
                    changes.Add(new GraphChange(++_sequence, GraphChangeKind.RoleChanged, previous, next, origin));
            }
        }
        _previous = graph;
        Save();
        return changes;
    }

    private void Save()
    {
        if (_stateFile is null || _previous is null) return;
        var parent = Path.GetDirectoryName(Path.GetFullPath(_stateFile))!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, Path.GetFileName(_stateFile) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var stored = new StoredGraph(_sequence, _previous.Packages.Select(p =>
            new StoredNode(p.Project, p.Framework, p.RuntimeIdentifier, p.Id, p.Version.ToString(),
                p.Direct, p.RequestedRange, p.PackagePath, p.Source, p.Dependencies.ToArray(),
                p.CentrallyManaged, p.ContentHash)).ToArray());
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(stored));
            File.Move(temp, _stateFile, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Key(PackageNode node) =>
        string.Join('\0', node.Project, node.Framework, node.RuntimeIdentifier ?? "", node.Id).ToLowerInvariant();
}

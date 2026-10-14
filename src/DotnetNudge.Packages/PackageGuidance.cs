using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;
using NuGet.Versioning;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DotnetNudge.Packages;

public sealed record GuidanceDocument(string Path, string Text, string Sha256);
public sealed record PackageSkill(string Handle, string Name, string Description, string PackageId,
    string Version, string? Source, string Project, string Framework, string? RuntimeIdentifier,
    GuidanceDocument Entry, IReadOnlyList<GuidanceDocument> References);
public sealed record GuidanceInventory(IReadOnlyList<PackageSkill> Skills, GuidanceDocument? Readme,
    EvidenceStatus Status, string? Diagnostic);

public sealed class PackageGuidanceReader
{
    private const long MaxFile = 256 * 1024;
    private const long MaxTotal = 4 * 1024 * 1024;
    private static readonly Regex LocalLink = new(@"!?\[[^\]]*\]\((?<path>[^)#\s]+)(?:#[^)]*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public GuidanceInventory ReadRestored(PackageNode node)
    {
        if (node.PackagePath is null)
            return new([], null, EvidenceStatus.Unknown, "Exact restored package path is unavailable.");
        return ReadDirectory(node.PackagePath, node);
    }

    public GuidanceInventory ReadArchive(string archivePath, PackageNode node) =>
        ReadFiles(node, () => ArchiveFiles(archivePath));

    private GuidanceInventory ReadDirectory(string root, PackageNode node) =>
        ReadFiles(node, () => DirectoryFiles(root));

    private static GuidanceInventory ReadFiles(PackageNode node,
        Func<Dictionary<string, byte[]>> acquire)
    {
        try
        {
            var files = acquire();
            var skills = new List<PackageSkill>();
            var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in files.Where(p => Regex.IsMatch(p.Key,
                         @"^skills/[^/]+/SKILL\.md$", RegexOptions.IgnoreCase))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var prefix = pair.Key[..(pair.Key.LastIndexOf('/') + 1)];
                var text = Decode(pair.Value);
                var (name, description) = Frontmatter(text);
                if (!skillNames.Add(name))
                    throw new InvalidDataException($"Colliding skill names: {name}");
                var references = new List<GuidanceDocument>();
                var pending = new Queue<GuidanceDocument>();
                pending.Enqueue(Document(pair.Key, pair.Value));
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pair.Key };
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    foreach (Match link in LocalLink.Matches(current.Text))
                    {
                        var value = Uri.UnescapeDataString(link.Groups["path"].Value);
                        if (Uri.TryCreate(value, UriKind.Absolute, out _) || value.StartsWith('#')) continue;
                        var directory = current.Path[..(current.Path.LastIndexOf('/') + 1)];
                        var path = Normalize(directory + value);
                        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            !files.TryGetValue(path, out var referenced))
                            throw new InvalidDataException($"Linked reference outside skill or missing: {value}");
                        if (visited.Add(path))
                        {
                            var document = Document(path, referenced);
                            references.Add(document);
                            pending.Enqueue(document);
                        }
                    }
                }
                var entry = Document(pair.Key, pair.Value);
                var handle = $"{node.Id}@{node.Version}:{node.Source ?? "unknown"}:{node.Project}:" +
                    $"{node.Framework}:{node.RuntimeIdentifier}:{entry.Sha256}";
                skills.Add(new(handle, name, description, node.Id, node.Version.ToString(), node.Source,
                    node.Project, node.Framework, node.RuntimeIdentifier, entry, references));
            }
            GuidanceDocument? readme = null;
            var nuspec = files.FirstOrDefault(p => p.Key.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspec.Value is null)
                throw new InvalidDataException("Exact package manifest is missing.");
            using var xmlReader = XmlReader.Create(new StringReader(Decode(nuspec.Value).TrimStart('\uFEFF')),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var xml = XDocument.Load(xmlReader, LoadOptions.None);
            var id = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
            var version = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
            if (!node.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                !NuGetVersion.TryParse(version, out var parsed) || parsed != node.Version)
                throw new InvalidDataException("Package manifest identity differs from selected graph scope.");
            var declared = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "readme")?.Value;
            if (!string.IsNullOrWhiteSpace(declared))
            {
                var path = Normalize(declared.Replace('\\', '/'));
                if (!files.TryGetValue(path, out var data))
                    throw new InvalidDataException($"Declared package README is missing: {path}");
                readme = Document(path, data);
            }
            return new(skills, readme, EvidenceStatus.Complete, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Xml.XmlException or DecoderFallbackException or ArgumentException)
        {
            return new([], null, EvidenceStatus.Unknown, $"Package guidance rejected: {ex.Message}");
        }
    }

    private static Dictionary<string, byte[]> ArchiveFiles(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(path);
        long total = 0;
        if (archive.Entries.Count > 20_000)
            throw new InvalidDataException("Archive has too many entries.");
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Split('/').Contains(".."))
                throw new InvalidDataException($"Traversal in archive path: {entry.FullName}");
            var name = Normalize(entry.FullName);
            if (entry.FullName.EndsWith('/')) continue;
            if (!Relevant(name)) continue;
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new InvalidDataException($"Symlink entry rejected: {name}");
            if (entry.Length > MaxFile || (total += entry.Length) > MaxTotal)
                throw new InvalidDataException("Archive exceeds guidance size limits.");
            if (!files.TryAdd(name, ReadBounded(entry.Open())))
                throw new InvalidDataException($"Colliding archive paths: {name}");
        }
        return files;
    }

    private static Dictionary<string, byte[]> DirectoryFiles(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var canonical = Path.GetFullPath(root);
        if (new DirectoryInfo(canonical).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Package directory is a symlink.");
        long total = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((canonical, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visited > 20_000)
                    throw new InvalidDataException("Package has too many entries.");
                var relative = Normalize(Path.GetRelativePath(canonical, entry).Replace('\\', '/'));
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Symlink directory rejected: {relative}");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (depth >= 32) throw new InvalidDataException("Package directory nesting exceeds limit.");
                    pending.Push((entry, depth + 1));
                    continue;
                }
                if (!Relevant(relative)) continue;
                var info = new FileInfo(entry);
                if (info.Length > MaxFile || (total += info.Length) > MaxTotal)
                    throw new InvalidDataException("Package exceeds guidance size limits.");
                if (!files.TryAdd(relative, ReadBounded(File.OpenRead(entry))))
                    throw new InvalidDataException($"Colliding package paths: {relative}");
            }
        }
        return files;
    }

    private static byte[] ReadBounded(Stream stream)
    {
        using (stream)
        using (var result = new MemoryStream())
        {
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (result.Length + count > MaxFile)
                    throw new InvalidDataException("Package file exceeds size limit.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') ||
            path.Contains('\0') || path.Contains(':') || path.Length > 1024)
            throw new InvalidDataException($"Unsafe package path: {path}");
        var stack = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) throw new InvalidDataException($"Traversal in package path: {path}");
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(part);
        }
        return string.Join('/', stack);
    }

    private static (string Name, string Description) Frontmatter(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 4 || lines[0] != "---")
            throw new InvalidDataException("SKILL.md requires YAML frontmatter.");
        var end = Array.IndexOf(lines, "---", 1);
        if (end < 2 || end > 100)
            throw new InvalidDataException("Malformed SKILL.md frontmatter.");
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(string.Join('\n', lines[1..end])));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
                throw new InvalidDataException("Skill frontmatter must be a YAML mapping.");
            string? Field(string key) => mapping.Children.FirstOrDefault(pair =>
                pair.Key is YamlScalarNode scalar && scalar.Value == key).Value is YamlScalarNode value
                ? value.Value?.Trim() : null;
            var name = Field("name");
            var description = Field("description");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
                throw new InvalidDataException("Skill frontmatter requires scalar name and description.");
            return (name, description);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException("Malformed YAML skill frontmatter.", ex);
        }
    }

    private static string Decode(byte[] data) => new UTF8Encoding(false, true).GetString(data);
    private static bool Relevant(string path) =>
        path.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    private static GuidanceDocument Document(string path, byte[] data) =>
        new(path, Decode(data), Convert.ToHexString(SHA256.HashData(data)));
}

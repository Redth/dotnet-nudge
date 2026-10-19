using System.Net;
using System.Text;
using System.Text.Json;
using DotnetNudge.Packages;

namespace DotnetNudge.Core;

public sealed record AgentSkillInfo(string Name, string Description, string Url, bool Legacy);
public sealed record AgentToolResource(string Id, string Repository, string Name, string Description,
    string? Version, string Revision, string Url, string Marketplace,
    IReadOnlyList<string> Capabilities, IReadOnlyList<AgentSkillInfo> Skills);
public sealed record AgentToolCatalog(string Repository, Assessment Freshness, bool Complete,
    IReadOnlyList<AgentToolResource> Resources, IReadOnlyList<Coverage> Coverage)
{
    public DateTimeOffset? RetrievedAt { get; init; }
}

public sealed class AgentToolCatalogSource(NudgeState state, HttpClient client, TimeProvider? clock = null)
{
    public static readonly IReadOnlyList<string> Repositories = ["dotnet/skills", "dotnet/maui-labs"];
    readonly TimeProvider time = clock ?? TimeProvider.System;
    const int MaxPlugins = 32;
    const int MaxSkillsPerPlugin = 4;

    public async Task<AgentToolCatalog> ReadAsync(string repository, CancellationToken token)
    {
        if (!Repositories.Contains(repository, StringComparer.Ordinal))
            throw new ArgumentException("Only the two configured official helper repositories are supported.");
        var coverage = new List<Coverage>();
        var resources = new List<AgentToolResource>();
        var freshness = Assessment.Confirmed;
        var complete = true;
        async Task<string> Read(string url, bool prefix = false)
        {
            var result = await ReadTextAsync(url, prefix, token);
            if (result.Freshness == Assessment.Stale) freshness = Assessment.Stale;
            return result.Body;
        }
        try
        {
            using var commit = JsonDocument.Parse(await Read($"https://api.github.com/repos/{repository}/commits/main"));
            var revision = commit.RootElement.GetProperty("sha").GetString()!;
            if (revision is not { Length: 40 } || revision.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Catalog commit identity is invalid.");
            var treeId = commit.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString();
            if (treeId is not { Length: 40 } || treeId.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Catalog tree identity is invalid.");
            using var tree = JsonDocument.Parse(await Read(
                $"https://api.github.com/repos/{repository}/git/trees/{treeId}"));
            var roots = Entries(tree);
            var entries = new List<(string Path, string Type, string Sha)>();
            var loadedRoots = new HashSet<string>(StringComparer.Ordinal);
            var marketplacePath = repository == "dotnet/maui-labs"
                ? ".github/plugin/marketplace.json" : ".claude-plugin/marketplace.json";
            string Raw(string path) => $"https://raw.githubusercontent.com/{repository}/{revision}/" +
                string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            using var marketplace = JsonDocument.Parse(await Read(Raw(marketplacePath)));
            var marketplaceName = marketplace.RootElement.GetProperty("name").GetString()!;
            var plugins = marketplace.RootElement.GetProperty("plugins").EnumerateArray().ToArray();
            if (plugins.Length > MaxPlugins)
            {
                complete = false;
                coverage.Add(new("agent-catalog-limit:" + repository, Assessment.Partial,
                    $"Only {MaxPlugins} installable plugins were read."));
            }
            foreach (var plugin in plugins.Take(MaxPlugins))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var name = plugin.GetProperty("name").GetString()!;
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
                        name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) ||
                        resources.Any(resource => resource.Name == name))
                        throw new InvalidDataException("Catalog plugin identity is invalid or duplicated.");
                    var directory = plugin.GetProperty("source").GetString()!;
                    if (directory is null || !directory.StartsWith("./", StringComparison.Ordinal) ||
                        directory.Contains('\\') || directory[2..].Split('/').Any(part => part is "" or "." or ".."))
                        throw new InvalidDataException("Catalog source must be a contained relative plugin directory.");
                    directory = directory[2..];
                    var rootDirectory = directory.Split('/')[0];
                    if (loadedRoots.Add(rootDirectory))
                    {
                        var root = roots.SingleOrDefault(entry => entry.Path == rootDirectory && entry.Type == "tree");
                        if (root == default)
                            throw new InvalidDataException("Plugin root is absent from the pinned source tree.");
                        using var subtree = JsonDocument.Parse(await Read(
                            $"https://api.github.com/repos/{repository}/git/trees/{root.Sha}?recursive=1"));
                        entries.AddRange(Entries(subtree).Select(entry =>
                            (rootDirectory + "/" + entry.Path, entry.Type, entry.Sha)));
                    }
                    var pluginTree = entries.SingleOrDefault(entry => entry.Path == directory && entry.Type == "tree");
                    if (pluginTree == default)
                        throw new InvalidDataException("Plugin directory is absent from the pinned source tree.");
                    var manifestPath = new[] { directory + "/plugin.json", directory + "/.claude-plugin/plugin.json" }
                        .FirstOrDefault(path => entries.Any(entry => entry.Path == path && entry.Type == "blob"))
                        ?? throw new InvalidDataException("Plugin manifest is unavailable.");
                    using var manifest = JsonDocument.Parse(await Read(Raw(manifestPath)));
                    if (manifest.RootElement.GetProperty("name").GetString() != name)
                        throw new InvalidDataException("Marketplace and plugin identities differ.");
                    var description = manifest.RootElement.TryGetProperty("description", out var descriptionValue)
                        ? descriptionValue.GetString() ?? "" : plugin.GetProperty("description").GetString() ?? "";
                    var version = manifest.RootElement.TryGetProperty("version", out var versionValue)
                        ? versionValue.GetString() : null;
                    var capabilities = new HashSet<string>(StringComparer.Ordinal);
                    if (manifest.RootElement.TryGetProperty("lspServers", out _)) capabilities.Add("language-server");
                    if (manifest.RootElement.TryGetProperty("mcpServers", out _) ||
                        plugin.TryGetProperty("mcpServers", out _) ||
                        entries.Any(entry => entry.Path == directory + "/mcp.json" || entry.Path == directory + "/.mcp.json"))
                        capabilities.Add("mcp-server");
                    var skillPaths = entries.Where(entry => entry.Type == "blob" &&
                        entry.Path.StartsWith(directory + "/skills/", StringComparison.Ordinal) &&
                        entry.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)).OrderBy(entry => entry.Path).ToArray();
                    if (skillPaths.Length > 0) capabilities.Add("skills");
                    var skills = new List<AgentSkillInfo>();
                    foreach (var skill in skillPaths.Take(MaxSkillsPerPlugin))
                    {
                        try
                        {
                            var text = await Read(Raw(skill.Path), prefix: true);
                            var metadata = PackageGuidanceReader.ReadFrontmatter(text);
                            skills.Add(new(metadata.Name, metadata.Description.Length > 2000
                                ? metadata.Description[..2000] : metadata.Description,
                                $"https://github.com/{repository}/blob/{revision}/{skill.Path}",
                                metadata.Description.StartsWith("Legacy ", StringComparison.OrdinalIgnoreCase)));
                        }
                        catch (Exception ex) when (Failure(ex))
                        {
                            coverage.Add(new("agent-skill:" + skill.Path, Assessment.Partial,
                                $"Skill usage metadata could not be read: {ex.Message}"));
                        }
                    }
                    if (skillPaths.Length > MaxSkillsPerPlugin)
                        coverage.Add(new("agent-skills:" + name, Assessment.Partial,
                            $"Read {MaxSkillsPerPlugin} of {skillPaths.Length} skill descriptions; plugin availability is still known."));
                    resources.Add(new($"{repository}:{name}", repository, name,
                        description.Length > 2000 ? description[..2000] : description, version,
                        pluginTree.Sha, $"https://github.com/{repository}/tree/{revision}/{directory}",
                        marketplaceName, capabilities.Order(StringComparer.Ordinal).ToArray(), skills));
                }
                catch (Exception ex) when (Failure(ex))
                {
                    complete = false;
                    coverage.Add(new("agent-plugin:" + repository, Assessment.Partial, ex.Message));
                }
            }
        }
        catch (Exception ex) when (Failure(ex))
        {
            complete = false;
            coverage.Add(new("agent-catalog:" + repository, Assessment.Blocked, ex.Message));
        }
        coverage.Add(new("agent-catalog:" + repository, freshness == Assessment.Stale
            ? Assessment.Stale : complete ? Assessment.Confirmed : Assessment.Partial,
            $"Read {resources.Count} installable plugin records. Repository-internal contributor skills are not installation recommendations."));
        return new(repository, freshness, complete, resources, coverage)
        {
            RetrievedAt = state.Cached($"https://api.github.com/repos/{repository}/commits/main")?.Refreshed
        };
    }

    static (string Path, string Type, string Sha)[] Entries(JsonDocument tree)
    {
        if (tree.RootElement.GetProperty("truncated").GetBoolean())
            throw new InvalidDataException("Catalog tree was truncated; absent resources cannot be inferred.");
        return tree.RootElement.GetProperty("tree").EnumerateArray().Select(item =>
            (item.GetProperty("path").GetString() ?? throw new InvalidDataException("Catalog path is missing."),
                item.GetProperty("type").GetString() ?? "",
                item.GetProperty("sha").GetString() ?? "")).ToArray();
    }

    async Task<(string Body, Assessment Freshness)> ReadTextAsync(string url, bool prefix, CancellationToken token)
    {
        var cached = state.Cached(url);
        if (cached is not null && time.GetUtcNow() - cached.Value.Refreshed < TimeSpan.FromHours(24))
            return (cached.Value.Body, Assessment.Confirmed);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("dotnet-nudge/1.0");
        if (cached?.Etag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.RequestMessage?.RequestUri?.AbsoluteUri != url)
                throw new IOException("Catalog redirect changed the approved source URL.");
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                state.Cache(url, cached.Value.Body, cached.Value.Etag, revalidated: true);
                return (cached.Value.Body, Assessment.Confirmed);
            }
            response.EnsureSuccessStatusCode();
            var limit = prefix ? 16 * 1024 : 4 * 1024 * 1024;
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (buffer.Length < limit)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, limit - buffer.Length)), token);
                if (read == 0) break;
                buffer.Write(bytes, 0, read);
            }
            if (!prefix && buffer.Length == limit && await stream.ReadAsync(bytes.AsMemory(0, 1), token) > 0)
                throw new InvalidDataException("Catalog metadata exceeds 4 MiB.");
            var body = Encoding.UTF8.GetString(buffer.ToArray());
            state.Cache(url, body, response.Headers.ETag?.ToString());
            return (body, Assessment.Confirmed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            if (cached is not null) return (cached.Value.Body, Assessment.Stale);
            throw new IOException($"Public catalog metadata unavailable ({ex.GetType().Name}); no installation state was inferred.", ex);
        }
    }

    static bool Failure(Exception ex) => ex is IOException or InvalidDataException or JsonException
        or InvalidOperationException or KeyNotFoundException or ArgumentException;
}

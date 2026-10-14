using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace DotnetNudge.Core;

public interface IReleaseMetadata
{
    Task<(string Body, Assessment Freshness)> ReadAsync(string source, CancellationToken cancellationToken);
}

public sealed class ReleaseMetadata : IReleaseMetadata
{
    public const string IndexUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";
    readonly NudgeState state;
    readonly HttpClient client;
    readonly TimeProvider clock;
    readonly string? fixtureDirectory;

    public ReleaseMetadata(NudgeState state, HttpClient client, TimeProvider? clock = null,
        string? fixtureDirectory = null)
    {
        this.state = state;
        this.client = client;
        this.clock = clock ?? TimeProvider.System;
        this.fixtureDirectory = fixtureDirectory;
    }

    public DateTimeOffset? LastRetrieved(string source) => state.Cached(source)?.Fetched;

    public async Task<(string Body, Assessment Freshness)> ReadAsync(string source, CancellationToken cancellationToken)
    {
        if (fixtureDirectory is not null)
        {
            if (source != IndexUrl && !Uri.TryCreate(source, UriKind.Absolute, out _))
                throw new IOException("Release fixture URI is invalid.");
            var file = source == IndexUrl ? "releases-index.json"
                : Path.GetFileName(new Uri(source).LocalPath);
            // Channel fixtures live in channel-specific directories to avoid ambiguous releases.json names.
            if (source != IndexUrl)
            {
                var channel = new Uri(source).Segments.Reverse().Skip(1).FirstOrDefault()?.Trim('/');
                file = Path.Combine(channel ?? "", file);
            }
            var fixture = Path.Combine(fixtureDirectory, file);
            if (new FileInfo(fixture).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Release fixture exceeds 32 MiB.");
            return (await File.ReadAllTextAsync(fixture, cancellationToken),
                Assessment.Confirmed);
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("dotnetcli.blob.core.windows.net" or "builds.dotnet.microsoft.com"))
            throw new IOException("Release metadata URI is not an approved official HTTPS endpoint.");
        var cached = state.Cached(source);
        if (cached is not null && clock.GetUtcNow() - cached.Value.Refreshed < TimeSpan.FromHours(24))
            return (cached.Value.Body, Assessment.Confirmed);
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (cached?.Etag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                state.Cache(source, cached.Value.Body, cached.Value.Etag, revalidated: true);
                return (cached.Value.Body, Assessment.Confirmed);
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 32 * 1024 * 1024)
                throw new InvalidDataException("Release metadata exceeds 32 MiB.");
            await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            state.Cache(source, body, response.Headers.ETag?.ToString());
            return (body, Assessment.Confirmed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cached is not null) return (cached.Value.Body, Assessment.Stale);
            throw new IOException($"Release metadata unavailable from {source}: {ex.Message}", ex);
        }
    }
}

public static class SdkServicing
{
    public static int FeatureBand(NuGetVersion version) => version.Patch / 100 * 100;

    static JsonElement RequiredArray(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Release metadata requires an array property '{key}'.");
        return value;
    }

    static string RequiredString(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Release metadata requires a string property '{key}'.");
        return value.GetString()!;
    }

    public static async Task<(IReadOnlyList<Finding> Findings, IReadOnlyList<Coverage> Coverage)> CheckAsync(
        WorkspaceInventory workspace, IReleaseMetadata source, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
        var coverage = new List<Coverage>();
        if (workspace.SdkDeclarations.Count == 0)
        {
            coverage.Add(new("sdk-servicing", Assessment.Unknown,
                "No global.json pin found; release-metadata servicing cannot assess a declaration."));
            return (findings, coverage);
        }
        JsonDocument index;
        Assessment indexFreshness;
        try
        {
            var result = await source.ReadAsync(ReleaseMetadata.IndexUrl, cancellationToken);
            index = JsonDocument.Parse(result.Body);
            indexFreshness = result.Freshness;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            coverage.Add(new("sdk-servicing", Assessment.Blocked, ex.Message));
            return (findings, coverage);
        }
        using (index)
        {
            Dictionary<string, string> channels;
            try
            {
                channels = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in RequiredArray(index.RootElement, "releases-index").EnumerateArray())
                {
                    var channelVersion = RequiredString(entry, "channel-version");
                    if (entry.TryGetProperty("releases.json", out var releasesJson) &&
                        releasesJson.ValueKind == JsonValueKind.String)
                        channels[channelVersion] = releasesJson.GetString()!;
                    else if (workspace.SdkDeclarations.Any(declaration =>
                        declaration.Version?.StartsWith(channelVersion + ".", StringComparison.Ordinal) == true))
                        throw new JsonException($"Release channel {channelVersion} is missing releases.json.");
                }
            }
            catch (JsonException ex)
            {
                coverage.Add(new("sdk-servicing", Assessment.Blocked, ex.Message));
                return (findings, coverage);
            }
            foreach (var declaration in workspace.SdkDeclarations)
            {
                if (!NuGetVersion.TryParse(declaration.Version, out var pinned))
                {
                    coverage.Add(new("sdk:" + declaration.Scope, Assessment.Unknown,
                    "No parseable declared SDK version; review separate effective SDK probe coverage."));
                    continue;
                }
                var channel = $"{pinned.Major}.{pinned.Minor}";
                try
                {
                    if (!channels.TryGetValue(channel, out var uri))
                    {
                        coverage.Add(new("sdk:" + declaration.Scope, Assessment.Unknown,
                            $"No official release channel for {channel}."));
                        continue;
                    }
                    var result = await source.ReadAsync(uri, cancellationToken);
                    using var releases = JsonDocument.Parse(result.Body);
                    var available = new List<NuGetVersion>();
                    var malformed = false;
                    foreach (var release in RequiredArray(releases.RootElement, "releases").EnumerateArray())
                    {
                        if (release.ValueKind != JsonValueKind.Object)
                        {
                            malformed = true;
                            continue;
                        }
                        if (release.TryGetProperty("sdk", out var sdk)) Add(sdk);
                        if (release.TryGetProperty("sdks", out var sdks))
                        {
                            if (sdks.ValueKind == JsonValueKind.Array)
                                foreach (var entry in sdks.EnumerateArray()) Add(entry);
                            else malformed = true;
                        }
                    }
                    void Add(JsonElement sdk)
                    {
                        if (sdk.ValueKind == JsonValueKind.Object &&
                            sdk.TryGetProperty("version", out var version) &&
                            version.ValueKind == JsonValueKind.String &&
                            NuGetVersion.TryParse(version.GetString(), out var parsed))
                            available.Add(parsed);
                        else malformed = true;
                    }
                    if (available.Count == 0)
                    {
                        coverage.Add(new("sdk:" + declaration.Scope, Assessment.Partial,
                            "Release channel has no parseable SDK entries."));
                        continue;
                    }
                    var candidate = available.Where(v => v.Major == pinned.Major &&
                        v.Minor == pinned.Minor && FeatureBand(v) == FeatureBand(pinned) &&
                        (declaration.AllowPrerelease == true || !v.IsPrerelease) && v > pinned)
                        .OrderByDescending(v => v).FirstOrDefault();
                    if (candidate is null)
                    {
                        coverage.Add(new("sdk:" + declaration.Scope,
                            indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                                ? Assessment.Stale : Assessment.Partial,
                            "No newer same-band SDK in available metadata; review separate effective SDK probe coverage." +
                            (malformed ? " Some release entries were malformed." : "")));
                        continue;
                    }
                    var subject = $"sdk:{WorkspaceDiscovery.SdkScopeKey(workspace.Root, declaration.Scope)}:" +
                        $"{channel}:{FeatureBand(pinned)}";
                    var revision = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{subject}|{candidate.ToNormalizedString()}")))
                        .ToLowerInvariant()[..16];
                    findings.Add(new(Contract.Version, subject, subject, revision, "sdk-servicing",
                        Priority.Medium, pinned.ToNormalizedString(), candidate.ToNormalizedString(),
                        new(null, null, declaration.Scope),
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        [new("dotnet-release-metadata", uri,
                            (source as ReleaseMetadata)?.LastRetrieved(uri) ??
                            (clock ?? TimeProvider.System).GetUtcNow())],
                        "review-sdk-pin-and-effective-selection", true, "sdk"));
                    coverage.Add(new("sdk:" + declaration.Scope,
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        "A newer SDK exists in the declared feature band; review installed/effective probe coverage separately. " +
                        $"Declared policy: rollForward={declaration.RollForward ?? "(default)"}, " +
                        $"allowPrerelease={declaration.AllowPrerelease?.ToString() ?? "(host-dependent; previews excluded)"}, " +
                        $"paths={string.Join(",", declaration.Paths)}." +
                        (malformed ? " Some release entries were malformed." : "")));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
                {
                    coverage.Add(new("sdk:" + declaration.Scope, Assessment.Blocked, ex.Message));
                }
            }
        }
        return (findings, coverage);
    }
}

public sealed class NudgeScanner(IReleaseMetadata metadata, TimeProvider? clock = null)
{
    public async Task<ScanReport> ScanAsync(string workspace, CancellationToken cancellationToken = default,
        bool probeInstalled = false, NudgeState? state = null)
    {
        var inventory = WorkspaceDiscovery.Discover(workspace);
        var (findings, sdkCoverage) = await SdkServicing.CheckAsync(inventory, metadata, clock, cancellationToken);
        var observed = probeInstalled ? await DotnetProbes.ReadAsync(inventory, cancellationToken) : null;
        var packages = state is null ? null : await PackageIntegration.ScanAsync(inventory,
            state.DirectoryPath, cancellationToken);
        return new(Contract.Version, inventory, findings.Concat(packages?.Findings ?? []).ToArray(),
            inventory.Coverage.Where(c => observed is null || c.Check != "effective-sdk")
                .Where(c => packages is null || c.Check != "nuget-resolved-graph")
                .Concat(sdkCoverage).Concat(observed?.Coverage ?? [])
                .Concat(packages?.Coverage ?? []).ToArray(),
            (clock ?? TimeProvider.System).GetUtcNow());
    }
}

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
                    var securityVersions = new HashSet<string>(StringComparer.Ordinal);
                    var malformed = false;
                    foreach (var release in RequiredArray(releases.RootElement, "releases").EnumerateArray())
                    {
                        if (release.ValueKind != JsonValueKind.Object)
                        {
                            malformed = true;
                            continue;
                        }
                        var security = release.TryGetProperty("security", out var flag) &&
                            flag.ValueKind == JsonValueKind.True;
                        if (release.TryGetProperty("sdk", out var sdk)) Add(sdk, security);
                        if (release.TryGetProperty("sdks", out var sdks))
                        {
                            if (sdks.ValueKind == JsonValueKind.Array)
                                foreach (var entry in sdks.EnumerateArray()) Add(entry, security);
                            else malformed = true;
                        }
                    }
                    void Add(JsonElement sdk, bool security)
                    {
                        if (sdk.ValueKind == JsonValueKind.Object &&
                            sdk.TryGetProperty("version", out var version) &&
                            version.ValueKind == JsonValueKind.String &&
                            NuGetVersion.TryParse(version.GetString(), out var parsed))
                        {
                            available.Add(parsed);
                            if (security) securityVersions.Add(parsed.ToNormalizedString());
                        }
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
                    var securityRelease = available.Any(version =>
                        version > pinned && version <= candidate &&
                        version.Major == pinned.Major && version.Minor == pinned.Minor &&
                        FeatureBand(version) == FeatureBand(pinned) &&
                        (declaration.AllowPrerelease == true || !version.IsPrerelease) &&
                        securityVersions.Contains(version.ToNormalizedString()));
                    var revision = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{subject}|{candidate.ToNormalizedString()}|{securityRelease}")))
                        .ToLowerInvariant()[..16];
                    findings.Add(new(Contract.Version, subject, subject, revision, "sdk-servicing",
                        securityRelease ? Priority.High : Priority.Medium,
                        pinned.ToNormalizedString(), candidate.ToNormalizedString(),
                        new(null, null, declaration.Scope),
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        [new(securityRelease ? "official-security-release" : "dotnet-release-metadata", uri,
                            (source as ReleaseMetadata)?.LastRetrieved(uri) ??
                            (clock ?? TimeProvider.System).GetUtcNow())],
                        securityRelease ? "review-security-release-and-effective-sdk-selection" :
                            "review-sdk-pin-and-effective-selection", true, "sdk"));
                    coverage.Add(new("sdk:" + declaration.Scope,
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        (securityRelease
                            ? "The SDK upgrade path crosses an official security release; CVE applicability to this project is not established. "
                            : "A newer SDK exists in the declared feature band. ") +
                        "Review installed/effective probe coverage separately. " +
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
            foreach (var declaration in workspace.SdkDeclarations)
            {
                if (!NuGetVersion.TryParse(declaration.Version, out var pinned)) continue;
                foreach (var (channel, uri) in channels
                    .Select(pair => (Version: NuGetVersion.TryParse(pair.Key + ".0", out var parsed)
                            ? parsed : null, pair.Key, pair.Value))
                    .Where(item => item.Version is not null && item.Version.Major > pinned.Major)
                    .OrderByDescending(item => item.Version)
                    .Take(2)
                    .Select(item => (item.Key, item.Value)))
                {
                    try
                    {
                        var (body, freshness) = await source.ReadAsync(uri, cancellationToken);
                        using var releases = JsonDocument.Parse(body);
                        var candidates = new List<NuGetVersion>();
                        foreach (var release in RequiredArray(releases.RootElement, "releases").EnumerateArray())
                        {
                            if (release.ValueKind != JsonValueKind.Object) continue;
                            if (release.TryGetProperty("sdk", out var sdk)) Add(sdk);
                            if (release.TryGetProperty("sdks", out var sdks) &&
                                sdks.ValueKind == JsonValueKind.Array)
                                foreach (var sdkEntry in sdks.EnumerateArray()) Add(sdkEntry);
                        }
                        void Add(JsonElement sdk)
                        {
                            if (sdk.ValueKind == JsonValueKind.Object &&
                                sdk.TryGetProperty("version", out var version) &&
                                version.ValueKind == JsonValueKind.String &&
                                NuGetVersion.TryParse(version.GetString(), out var parsed) &&
                                parsed.Major > pinned.Major)
                                candidates.Add(parsed);
                        }
                        foreach (var candidate in new[]
                        {
                            candidates.Where(v => !v.IsPrerelease).OrderByDescending(v => v).FirstOrDefault(),
                            candidates.Where(v => v.IsPrerelease).OrderByDescending(v => v).FirstOrDefault()
                        }.Where(v => v is not null))
                        {
                            var kind = candidate!.IsPrerelease ? "sdk-preview-trial" : "sdk-major-trial";
                            var subject = $"sdk-trial:{WorkspaceDiscovery.SdkScopeKey(workspace.Root, declaration.Scope)}:{kind}:{channel}";
                            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                                $"{subject}|{candidate.ToNormalizedString()}"))).ToLowerInvariant()[..16];
                            findings.Add(new(Contract.Version, subject, subject, revision, kind,
                                Priority.Low, pinned.ToNormalizedString(), candidate.ToNormalizedString(),
                                new(null, null, declaration.Scope),
                                indexFreshness == Assessment.Stale || freshness == Assessment.Stale
                                    ? Assessment.Stale : Assessment.Partial,
                                [new("official-sdk-release", uri,
                                    (source as ReleaseMetadata)?.LastRetrieved(uri) ??
                                    (clock ?? TimeProvider.System).GetUtcNow())],
                                "review-isolated-sdk-only-trial-no-production-pin-or-tfm-change",
                                true, "sdk-trial"));
                        }
                        coverage.Add(new("sdk-trial:" + declaration.Scope + ":" + channel,
                            indexFreshness == Assessment.Stale || freshness == Assessment.Stale
                                ? Assessment.Stale : Assessment.Partial,
                            "Official SDK versions only; availability on this host and trial build/test compatibility are unverified. " +
                            "Preview eligibility does not change production global.json policy."));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
                    {
                        coverage.Add(new("sdk-trial:" + declaration.Scope + ":" + channel,
                            Assessment.Blocked, ex.Message));
                    }
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
            state, cancellationToken);
        var toolchains = state is null ? null : await ToolchainIntegration.ScanAsync(inventory,
            observed, metadata, cancellationToken);
        return new(Contract.Version, inventory, findings.Concat(packages?.Findings ?? [])
                .Concat(toolchains?.Findings ?? []).ToArray(),
            inventory.Coverage.Where(c => observed is null || c.Check != "effective-sdk")
                .Where(c => packages is null || c.Check != "nuget-resolved-graph")
                .Concat(sdkCoverage).Concat(observed?.Coverage ?? [])
                .Concat(packages?.Coverage ?? []).Concat(toolchains?.Coverage ?? []).ToArray(),
            (clock ?? TimeProvider.System).GetUtcNow());
    }
}

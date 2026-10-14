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

    public async Task<(string Body, Assessment Freshness)> ReadAsync(string source, CancellationToken cancellationToken)
    {
        if (fixtureDirectory is not null)
        {
            var file = source == IndexUrl ? "releases-index.json"
                : Path.GetFileName(new Uri(source).LocalPath);
            // Channel fixtures live in channel-specific directories to avoid ambiguous releases.json names.
            if (source != IndexUrl)
            {
                var channel = new Uri(source).Segments.Reverse().Skip(1).FirstOrDefault()?.Trim('/');
                file = Path.Combine(channel ?? "", file);
            }
            return (await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, file), cancellationToken),
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
                state.Cache(source, cached.Value.Body, cached.Value.Etag);
                return (cached.Value.Body, Assessment.Confirmed);
            }
            response.EnsureSuccessStatusCode();
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

    public static async Task<(IReadOnlyList<Finding> Findings, IReadOnlyList<Coverage> Coverage)> CheckAsync(
        WorkspaceInventory workspace, IReleaseMetadata source, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
        var coverage = new List<Coverage>();
        if (workspace.SdkDeclarations.Count == 0)
        {
            coverage.Add(new("sdk-servicing", Assessment.Unknown,
                "No global.json pin found; effective installed SDK has not been observed."));
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
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            coverage.Add(new("sdk-servicing", Assessment.Blocked, ex.Message));
            return (findings, coverage);
        }
        using (index)
        {
            foreach (var declaration in workspace.SdkDeclarations)
            {
                if (!NuGetVersion.TryParse(declaration.Version, out var pinned))
                {
                    coverage.Add(new("sdk:" + declaration.Scope, Assessment.Unknown,
                        "No parseable declared SDK version; effective selection not observed."));
                    continue;
                }
                var channel = $"{pinned.Major}.{pinned.Minor}";
                try
                {
                    var entries = index.RootElement.GetProperty("releases-index").EnumerateArray();
                    var selected = entries.FirstOrDefault(e =>
                        e.GetProperty("channel-version").GetString() == channel);
                    if (selected.ValueKind == JsonValueKind.Undefined)
                    {
                        coverage.Add(new("sdk:" + declaration.Scope, Assessment.Unknown,
                            $"No official release channel for {channel}."));
                        continue;
                    }
                    var uri = selected.GetProperty("releases.json").GetString()
                        ?? throw new JsonException("Missing releases.json URI.");
                    var result = await source.ReadAsync(uri, cancellationToken);
                    using var releases = JsonDocument.Parse(result.Body);
                    var available = new List<NuGetVersion>();
                    foreach (var release in releases.RootElement.GetProperty("releases").EnumerateArray())
                    {
                        if (release.TryGetProperty("sdk", out var sdk)) Add(sdk);
                        if (release.TryGetProperty("sdks", out var sdks) && sdks.ValueKind == JsonValueKind.Array)
                            foreach (var entry in sdks.EnumerateArray()) Add(entry);
                    }
                    void Add(JsonElement sdk)
                    {
                        if (sdk.ValueKind == JsonValueKind.Object &&
                            sdk.TryGetProperty("version", out var version) &&
                            NuGetVersion.TryParse(version.GetString(), out var parsed))
                            available.Add(parsed);
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
                            "No newer same-band SDK in available metadata; effective SDK selection remains unobserved."));
                        continue;
                    }
                    var subject = $"sdk:{declaration.Scope}:{channel}:{FeatureBand(pinned)}";
                    var revision = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{subject}|{candidate.ToNormalizedString()}")))
                        .ToLowerInvariant()[..16];
                    findings.Add(new(Contract.Version, subject, subject, revision, "sdk-servicing",
                        Priority.Medium, pinned.ToNormalizedString(), candidate.ToNormalizedString(),
                        new(null, null, declaration.Scope),
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        [new("dotnet-release-metadata", uri, (clock ?? TimeProvider.System).GetUtcNow())],
                        "review-sdk-pin-and-effective-selection", true, "sdk"));
                    coverage.Add(new("sdk:" + declaration.Scope,
                        indexFreshness == Assessment.Stale || result.Freshness == Assessment.Stale
                            ? Assessment.Stale : Assessment.Partial,
                        "A newer SDK exists in the declared feature band; installed/effective SDK not observed. " +
                        $"Declared policy: rollForward={declaration.RollForward ?? "(default)"}, " +
                        $"allowPrerelease={declaration.AllowPrerelease?.ToString() ?? "(host-dependent; previews excluded)"}, " +
                        $"paths={string.Join(",", declaration.Paths)}."));
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
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
    public async Task<ScanReport> ScanAsync(string workspace, CancellationToken cancellationToken = default)
    {
        var inventory = WorkspaceDiscovery.Discover(workspace);
        var (findings, sdkCoverage) = await SdkServicing.CheckAsync(inventory, metadata, clock, cancellationToken);
        return new(Contract.Version, inventory, findings,
            inventory.Coverage.Concat(sdkCoverage).ToArray(), (clock ?? TimeProvider.System).GetUtcNow());
    }
}

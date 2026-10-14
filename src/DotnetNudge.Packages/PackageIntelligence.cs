using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Model;
using NuGet.Versioning;

namespace DotnetNudge.Packages;

public sealed record PackageAdvisory(string Url, int Severity, string Range, string AuditSource);
public sealed record PackageDeprecation(string Source, IReadOnlyList<string> Reasons,
    string? Message, string? AlternatePackageId, string? AlternateVersionRange);
public sealed record UpdateCandidate(NuGetVersion Version, string Kind, string Source,
    EvidenceStatus Compatibility, string? Diagnostic);
public sealed record RemediationPath(IReadOnlyList<string> PackageIds, string DirectParent,
    NuGetVersion? ParentCandidate, string? ParentSource,
    EvidenceStatus Coverage = EvidenceStatus.Unknown, string? Diagnostic = null);
public sealed record PackageAssessment(PackageNode Package, EvidenceStatus Status,
    IReadOnlyList<PackageAdvisory> Advisories, IReadOnlyList<UpdateCandidate> Candidates,
    NuGetVersion? MinimalKnownFix, IReadOnlyList<RemediationPath> Paths, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<PackageDeprecation> Deprecations { get; init; } = [];
    public string SubjectKey => string.Join('|', Package.Project, Package.Framework,
        Package.RuntimeIdentifier ?? "", Package.Id.ToLowerInvariant());
    public string EvidenceRevision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            SubjectKey,
            Version = Package.Version.ToNormalizedString(),
            Status,
            MinimalKnownFix = MinimalKnownFix?.ToNormalizedString(),
            Deprecations = Deprecations.Select(d => new
                {
                    d.Source, d.Message, d.AlternatePackageId, d.AlternateVersionRange,
                    Reasons = d.Reasons.Order(StringComparer.Ordinal).ToArray()
                })
                .OrderBy(d => d.Source, StringComparer.Ordinal).ToArray(),
            Advisories = Advisories.Select(a => new { a.AuditSource, a.Url, a.Range, a.Severity })
                .OrderBy(a => a.AuditSource, StringComparer.Ordinal)
                .ThenBy(a => a.Url, StringComparer.Ordinal).ThenBy(a => a.Range, StringComparer.Ordinal)
                .ThenBy(a => a.Severity).ToArray(),
            Candidates = Candidates.Select(c => new
                { Version = c.Version.ToNormalizedString(), c.Kind, c.Source, c.Compatibility, c.Diagnostic })
                .OrderBy(c => c.Source, StringComparer.Ordinal)
                .ThenBy(c => c.Version, StringComparer.Ordinal)
                .ThenBy(c => c.Kind, StringComparer.Ordinal)
                .ThenBy(c => c.Compatibility)
                .ThenBy(c => c.Diagnostic, StringComparer.Ordinal).ToArray(),
            Paths = Paths.Select(p => new
                {
                    PackageIds = p.PackageIds.ToArray(), p.DirectParent,
                    ParentCandidate = p.ParentCandidate?.ToNormalizedString(), p.ParentSource,
                    p.Coverage, p.Diagnostic
                })
                .OrderBy(p => string.Join('\0', p.PackageIds), StringComparer.Ordinal)
                .ThenBy(p => p.DirectParent, StringComparer.Ordinal)
                .ThenBy(p => p.ParentCandidate, StringComparer.Ordinal)
                .ThenBy(p => p.ParentSource, StringComparer.Ordinal)
                .ThenBy(p => p.Coverage)
                .ThenBy(p => p.Diagnostic, StringComparer.Ordinal).ToArray(),
            Diagnostics = Diagnostics.OrderBy(d => d, StringComparer.Ordinal).ToArray()
        }))));
    public bool RequiresConsentForMutation => true;
    public DateTimeOffset RetrievedAt { get; init; }
}

public sealed class PackageSourcePolicy
{
    private readonly ISettings _settings;
    private readonly IReadOnlyList<PackageSource> _sources;
    private readonly PackageSourceMapping _mapping;

    public PackageSourcePolicy(string projectDirectory)
    {
        _settings = Settings.LoadDefaultSettings(projectDirectory);
        var provider = new PackageSourceProvider(_settings);
        _sources = provider.LoadPackageSources().Where(s => s.IsEnabled).ToArray();
        AuditSources = provider.LoadAuditSources().Where(s => s.IsEnabled).ToArray();
        _mapping = PackageSourceMapping.GetPackageSourceMapping(_settings);
    }

    public IReadOnlyList<PackageSource> AuditSources { get; }

    // With no mapping, explicit source names are mandatory: never broadcast IDs to every configured feed.
    public IReadOnlyList<PackageSource> Allowed(string id, IReadOnlySet<string>? approvedSourceNames = null)
    {
        var names = _mapping.IsEnabled
            ? _mapping.GetConfiguredPackageSources(id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : approvedSourceNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return _sources.Where(s => names.Contains(s.Name) &&
            (approvedSourceNames is null || approvedSourceNames.Contains(s.Name))).ToArray();
    }
}

public sealed class PackageIntelligence
{
    private readonly PackageSourcePolicy _policy;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _advisoryLifetime;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<IReadOnlyDictionary<string,
        IReadOnlyList<PackageVulnerabilityInfo>>> Partitions)> _advisoryCache = new(StringComparer.OrdinalIgnoreCase);

    public PackageIntelligence(PackageSourcePolicy policy, Func<DateTimeOffset>? now = null,
        TimeSpan? advisoryLifetime = null)
    {
        _policy = policy;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _advisoryLifetime = advisoryLifetime ?? TimeSpan.FromHours(6);
    }

    public async Task<PackageAssessment> AssessAsync(PackageNode package, PackageGraph graph,
        IReadOnlySet<string>? approvedSourceNames = null, bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        if (graph.Status != EvidenceStatus.Complete)
            return new PackageAssessment(package, EvidenceStatus.Unknown, [], [], null, [],
                [graph.Diagnostic ?? "Graph unavailable"]) { RetrievedAt = _now() };
        var sources = _policy.Allowed(package.Id, approvedSourceNames);
        if (sources.Count == 0)
            diagnostics.Add("No permitted mapped/explicit package source for this ID.");
        var audits = _policy.AuditSources.Count > 0 ? _policy.AuditSources : sources;
        if (audits.Count == 0)
            diagnostics.Add("No permitted audit source; advisory status is unknown.");
        var advisories = new List<PackageAdvisory>();
        var ranges = new List<VersionRange>();
        foreach (var audit in audits)
        {
            try
            {
                var partitions = await AdvisoryPartitions(audit, cancellationToken);
                foreach (var partition in partitions)
                    if (partition.TryGetValue(package.Id, out var entries))
                        foreach (var entry in entries)
                        {
                            if (entry.Versions.Satisfies(package.Version))
                                advisories.Add(new(entry.Url.ToString(), (int)entry.Severity,
                                    entry.Versions.ToString(), audit.Name));
                            ranges.Add(entry.Versions);
                        }
            }
            catch (Exception ex) when (FeedFailure(ex))
            {
                diagnostics.Add($"Audit source {audit.Name} unavailable ({ex.GetType().Name}); authentication or feed access may be required.");
            }
        }
        var candidates = new List<UpdateCandidate>();
        var deprecations = new List<PackageDeprecation>();
        foreach (var source in sources)
        {
            try
            {
                var repository = Repository.Factory.GetCoreV3(source);
                using var cache = new SourceCacheContext { NoCache = true };
                var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                var metadata = (await metadataResource.GetMetadataAsync(package.Id,
                    includePrerelease || package.Version.IsPrerelease, true,
                    cache, NullLogger.Instance, cancellationToken)).ToArray();
                if (metadata.Length == 0)
                {
                    diagnostics.Add($"Version metadata unavailable from {source.Name}.");
                    continue;
                }
                var current = metadata.FirstOrDefault(m => m.Identity.Version == package.Version);
                if (current is null)
                    diagnostics.Add($"Exact resolved version metadata unavailable from {source.Name}; deprecation status is unknown.");
                else if (await current.GetDeprecationMetadataAsync().WaitAsync(cancellationToken) is { } deprecation)
                {
                    var reasons = (deprecation.Reasons ?? []).Select(reason =>
                            new[] { "Legacy", "CriticalBugs", "Other" }.FirstOrDefault(known =>
                                known.Equals(reason, StringComparison.OrdinalIgnoreCase)))
                        .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    deprecations.Add(new(source.Name, reasons.Length == 0 ? ["Other"] : reasons,
                        deprecation.Message, deprecation.AlternatePackage?.PackageId,
                        deprecation.AlternatePackage?.Range?.ToString()));
                }
                foreach (var candidate in metadata.Where(m => m.IsListed && m.Identity.Version > package.Version &&
                    (includePrerelease || !m.Identity.Version.IsPrerelease)).OrderBy(m => m.Identity.Version))
                {
                    if (await candidate.GetDeprecationMetadataAsync().WaitAsync(cancellationToken) is not null)
                        continue;
                    var version = candidate.Identity.Version;
                    var compatibility = EvidenceStatus.Unknown;
                    string? reason = "Candidate dependency/framework evidence unavailable.";
                    if (candidate.DependencySets is { } dependencyGroups)
                    {
                        var framework = NuGetFramework.ParseFolder(package.Framework);
                        var nearest = new FrameworkReducer().GetNearest(framework,
                            dependencyGroups.Select(g => g.TargetFramework));
                        if (nearest is null)
                            reason = $"No candidate dependency group compatible with {package.Framework}.";
                        else
                        {
                            var group = dependencyGroups.First(g => g.TargetFramework.Equals(nearest));
                            var conflict = group.Packages.FirstOrDefault(d => graph.Packages.Any(p =>
                                p.Project == package.Project && p.Framework == package.Framework &&
                                p.RuntimeIdentifier == package.RuntimeIdentifier &&
                                p.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase) &&
                                !d.VersionRange.Satisfies(p.Version)));
                            compatibility = conflict is null ? EvidenceStatus.Partial : EvidenceStatus.Unknown;
                            reason = conflict is null
                                ? "Static dependency group matches; full restore/build compatibility unverified."
                                : $"Installed {conflict.Id} does not satisfy candidate constraint {conflict.VersionRange}.";
                        }
                    }
                    candidates.Add(new(version, Classify(package.Version, version), source.Name,
                        compatibility, reason));
                }
            }
            catch (Exception ex) when (FeedFailure(ex))
            {
                diagnostics.Add($"Package source {source.Name} unavailable ({ex.GetType().Name}); authentication or feed access may be required.");
            }
        }
        var distinct = candidates.OrderBy(c => c.Version).ThenBy(c => c.Source, StringComparer.Ordinal)
            .DistinctBy(c => (c.Version, c.Source)).ToArray();
        // Do not claim a security fix if any audit partition or source failed.
        var fix = diagnostics.Count == 0 && advisories.Count > 0
            ? distinct.FirstOrDefault(c => c.Compatibility == EvidenceStatus.Partial &&
                ranges.All(r => !r.Satisfies(c.Version)))?.Version : null;
        IReadOnlyList<RemediationPath> paths = package.Direct ? [] : FindPaths(package, graph);
        if (fix is not null && paths.Count > 0)
            paths = await ParentOptions(paths, package, fix, graph, approvedSourceNames, diagnostics,
                cancellationToken);
        return new PackageAssessment(package, diagnostics.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
            advisories, distinct, fix, paths, diagnostics)
            { RetrievedAt = _now(), Deprecations = deprecations };
    }

    public async Task<PackageAssessment> AuditOnlyAsync(PackageNode package, PackageGraph graph,
        CancellationToken cancellationToken = default)
    {
        if (graph.Status != EvidenceStatus.Complete)
            return new(package, EvidenceStatus.Unknown, [], [], null, [],
                [graph.Diagnostic ?? "Graph unavailable"]) { RetrievedAt = _now() };
        var sources = _policy.AuditSources.Count > 0
            ? _policy.AuditSources : _policy.Allowed(package.Id);
        var diagnostics = new List<string>();
        var advisories = new List<PackageAdvisory>();
        if (sources.Count == 0)
            diagnostics.Add("No permitted mapped source or configured audit source; advisory coverage is unknown.");
        foreach (var source in sources)
        {
            try
            {
                foreach (var partition in await AdvisoryPartitions(source, cancellationToken))
                    if (partition.TryGetValue(package.Id, out var entries))
                        foreach (var entry in entries)
                            if (entry.Versions.Satisfies(package.Version))
                                advisories.Add(new(entry.Url.ToString(), (int)entry.Severity,
                                    entry.Versions.ToString(), source.Name));
            }
            catch (Exception ex) when (FeedFailure(ex))
            {
                diagnostics.Add($"Audit source {source.Name} unavailable ({ex.GetType().Name}); advisory coverage is incomplete.");
            }
        }
        return new(package, diagnostics.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            advisories, [], null, [], diagnostics) { RetrievedAt = _now() };
    }

    private async Task<IReadOnlyList<RemediationPath>> ParentOptions(IReadOnlyList<RemediationPath> paths,
        PackageNode target, NuGetVersion fixedVersion, PackageGraph graph,
        IReadOnlySet<string>? approvedSources, List<string> diagnostics, CancellationToken token)
    {
        var output = new List<RemediationPath>();
        foreach (var path in paths)
        {
            var parent = graph.Packages.First(p => p.Project == target.Project && p.Framework == target.Framework &&
                p.RuntimeIdentifier == target.RuntimeIdentifier &&
                p.Id.Equals(path.DirectParent, StringComparison.OrdinalIgnoreCase));
            if (path.PackageIds.Count != 2)
            {
                diagnostics.Add($"Indirect remediation path through {path.DirectParent} needs a restore trial.");
                output.Add(path with { Coverage = EvidenceStatus.Partial,
                    Diagnostic = "Indirect dependency constraints are not evaluated." });
                continue; // An indirect path needs a restore trial before a parent version can be attributed.
            }
            var sources = _policy.Allowed(parent.Id, approvedSources);
            if (sources.Count == 0)
            {
                diagnostics.Add($"No permitted mapped/explicit source for direct parent {parent.Id}.");
                output.Add(path with { Coverage = EvidenceStatus.Unknown,
                    Diagnostic = "Direct-parent metadata source is not permitted." });
                continue;
            }
            var choices = new List<(NuGetVersion Version, string Source)>();
            var incomplete = false;
            foreach (var source in sources)
                try
                {
                    var repo = Repository.Factory.GetCoreV3(source);
                    using var cache = new SourceCacheContext { NoCache = true };
                    var resource = await repo.GetResourceAsync<PackageMetadataResource>(token);
                    var metadata = (await resource.GetMetadataAsync(parent.Id, false, false, cache,
                        NullLogger.Instance, token)).ToArray();
                    if (metadata.Length == 0)
                    {
                        diagnostics.Add($"Direct parent {parent.Id} metadata unavailable from {source.Name}.");
                        incomplete = true;
                        continue;
                    }
                    foreach (var item in metadata.Where(m => m.IsListed && m.Identity.Version > parent.Version))
                    {
                        if (await item.GetDeprecationMetadataAsync().WaitAsync(token) is not null)
                            continue;
                        if (item.DependencySets is null)
                        {
                            incomplete = true;
                            diagnostics.Add($"Direct parent {parent.Id}@{item.Identity.Version} dependency metadata is missing.");
                            continue;
                        }
                        var nearest = new FrameworkReducer().GetNearest(NuGetFramework.ParseFolder(target.Framework),
                            item.DependencySets.Select(g => g.TargetFramework));
                        var group = item.DependencySets.FirstOrDefault(g => g.TargetFramework.Equals(nearest));
                        if (group?.Packages.Any(d => d.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase) &&
                            d.VersionRange.MinVersion is { } minimum && minimum >= fixedVersion &&
                            d.VersionRange.Satisfies(fixedVersion)) == true)
                            choices.Add((item.Identity.Version, source.Name));
                    }
                }
                catch (Exception ex) when (FeedFailure(ex))
                {
                    incomplete = true;
                    diagnostics.Add($"Direct parent {parent.Id} source {source.Name} unavailable ({ex.GetType().Name}); authentication or feed access may be required.");
                }
            var best = choices.OrderBy(c => c.Version).FirstOrDefault();
            output.Add(path with { ParentCandidate = best.Version, ParentSource = best.Source,
                Coverage = incomplete ? EvidenceStatus.Partial : EvidenceStatus.Complete,
                Diagnostic = incomplete ? "Direct-parent source or dependency metadata is incomplete." : null });
        }
        return output;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<PackageVulnerabilityInfo>>>>
        AdvisoryPartitions(PackageSource source, CancellationToken token)
    {
        if (_advisoryCache.TryGetValue(source.Source, out var cached) &&
            _now() - cached.At < _advisoryLifetime)
            return cached.Partitions;
        var repository = Repository.Factory.GetCoreV3(source);
        var resource = await repository.GetResourceAsync<IVulnerabilityInfoResource>(token)
            ?? throw new InvalidDataException("Feed has no VulnerabilityInfo resource.");
        using var cache = new SourceCacheContext { NoCache = true };
        var result = await resource.GetVulnerabilityInfoAsync(cache, NullLogger.Instance, token);
        if (result.Exceptions is not null || result.KnownVulnerabilities is null)
            throw new InvalidDataException("Incomplete advisory partitions.");
        _advisoryCache[source.Source] = (_now(), result.KnownVulnerabilities);
        return result.KnownVulnerabilities;
    }

    private static string Classify(NuGetVersion current, NuGetVersion next) =>
        next.IsPrerelease ? "preview" : next.Major != current.Major ? "major"
        : next.Minor != current.Minor ? "minor" : "patch";

    private static bool FeedFailure(Exception ex) => ex is NuGetProtocolException or
        HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or InvalidOperationException ||
        ex is AggregateException aggregate && !aggregate.InnerExceptions.Any(e => e is OperationCanceledException);

    private static IReadOnlyList<RemediationPath> FindPaths(PackageNode target, PackageGraph graph)
    {
        var scope = graph.Packages.Where(p => p.Project == target.Project && p.Framework == target.Framework &&
            p.RuntimeIdentifier == target.RuntimeIdentifier).ToArray();
        var paths = new List<RemediationPath>();
        void Walk(PackageNode node, List<string> path)
        {
            if (path.Any(id => id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))) return;
            var next = new List<string>(path) { node.Id };
            if (node.Direct) { paths.Add(new(next, node.Id, null, null)); return; }
            foreach (var parent in scope.Where(p => p.Dependencies.Contains(node.Id, StringComparer.OrdinalIgnoreCase)))
                Walk(parent, next);
        }
        Walk(target, []);
        return paths.OrderBy(p => p.PackageIds.Count).ToArray();
    }
}

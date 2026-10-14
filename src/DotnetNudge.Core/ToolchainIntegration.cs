using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetNudge.Toolchains;
using ToolAssessment = DotnetNudge.Toolchains.Assessment;
using ToolEvidence = DotnetNudge.Toolchains.Evidence;

namespace DotnetNudge.Core;

public sealed record ToolchainEvidenceBundle(WorkloadAnalysisRequest? Workload,
    HostToolInventory? HostInventory,
    IReadOnlyList<PlatformTargetAvailability>? Targets,
    ProcessLocalSuggestions? Suggestions,
    IReadOnlyDictionary<string, VersionRequirement>? MacOsRequirementsByWorkload,
    RuntimeDeployment? Deployment = null);

public sealed record ToolchainScanResult(IReadOnlyList<Finding> Findings,
    IReadOnlyList<Coverage> Coverage, AnalysisResult<WorkloadAnalysis>? Workload,
    AnalysisResult<HostCompatibilityResult>? Host,
    IReadOnlyList<AnalysisResult<RuntimeServicingResult>> Runtimes);

public static class ToolchainIntegration
{
    const string BundlePath = ".dotnet-nudge/toolchains.json";
    static readonly JsonSerializerOptions BundleJson = CreateBundleJson();

    static JsonSerializerOptions CreateBundleJson()
    {
        var options = new JsonSerializerOptions(Contract.Json);
        options.Converters.Add(new StringSetConverter());
        return options;
    }

    sealed class StringSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<string[]>(ref reader, options)?.ToHashSet(StringComparer.Ordinal)
                ?? throw new JsonException("A workload or inventory set must be an array.");

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal).ToArray(), options);
    }

    public static async Task<ToolchainScanResult> ScanAsync(WorkspaceInventory inventory,
        ProbeSnapshot? observed, IReleaseMetadata metadata, CancellationToken token)
    {
        var findings = new List<Finding>();
        var coverage = new List<Coverage>();
        var runtimes = new List<AnalysisResult<RuntimeServicingResult>>();
        foreach (var sdk in inventory.SdkDeclarations)
        {
            var evidence = new ToolEvidence(sdk.Scope, DateTimeOffset.UtcNow,
                Revision(sdk.RawJson));
            var parsed = GlobalJsonReader.Parse(sdk.RawJson, evidence);
            coverage.Add(new("global-json:" + sdk.Scope, Map(parsed.Assessment),
                parsed.Value is null
                    ? string.Join("; ", parsed.Diagnostics.Select(d => d.Message))
                    : $"Declared SDK {parsed.Value.SdkVersion ?? "unversioned"} and workload set " +
                      $"{parsed.Value.WorkloadVersion ?? "unpinned"}; selection is not an installed observation."));
        }

        AnalysisResult<WorkloadAnalysis>? workload = null;
        AnalysisResult<HostCompatibilityResult>? host = null;
        ToolchainEvidenceBundle? bundle = null;
        if (File.Exists(Path.Combine(inventory.Root, BundlePath)))
        {
            try
            {
                var raw = SafeMetadataReader.ReadFile(inventory.Root, BundlePath, 4 * 1024 * 1024);
                bundle = JsonSerializer.Deserialize<ToolchainEvidenceBundle>(raw, BundleJson)
                    ?? throw new InvalidDataException("Toolchain evidence bundle cannot be null.");
                Validate(bundle);
                if (bundle.Workload is not null)
                {
                    var effective = observed?.EffectiveSdks.GetValueOrDefault(".");
                    if (effective is not null && effective != bundle.Workload.SdkVersion)
                        coverage.Add(new("workload-set", Assessment.Unknown,
                            "Supplied workload SDK differs from the observed effective workspace SDK."));
                    else
                    {
                        workload = WorkloadCompatibilityAnalyzer.Analyze(bundle.Workload);
                        coverage.Add(new("workload-set", Map(workload.Assessment),
                            Detail(workload.Diagnostics, "Supplied workload-set metadata analyzed offline.")));
                        if (workload.Value is { Selection: var selection } &&
                            bundle.Workload.InstalledInventory?.WorkloadSetVersion is { } installedSet &&
                            installedSet != selection.SelectedWorkloadSetVersion)
                        {
                            var subject = "workload-set:" + selection.SdkFeatureBand;
                            findings.Add(new(Contract.Version, subject, subject,
                                Revision(subject + raw), "workload-set-difference", Priority.Low,
                                installedSet, selection.SelectedWorkloadSetVersion,
                                new FindingScope(null, null, selection.SdkFeatureBand),
                                Assessment.Partial,
                                [new Evidence("supplied-workload-metadata",
                                    new Uri(Path.Combine(inventory.Root, BundlePath)).AbsoluteUri,
                                    DateTimeOffset.UtcNow)],
                                "review-exact-workload-set-and-host-compatibility", true, "workload"));
                        }
                        if (workload.Value is not null && bundle.HostInventory is not null)
                        {
                            host = HostCompatibilityAnalyzer.Analyze(new(workload.Value,
                                bundle.HostInventory, bundle.Targets ?? [],
                                bundle.Suggestions ?? new(null, null, new Dictionary<string, string>()),
                                bundle.Workload.Freshness, bundle.MacOsRequirementsByWorkload));
                            coverage.Add(new("host-compatibility", Map(host.Assessment),
                                Detail(host.Diagnostics, "Supplied host inventory analyzed offline; no host changes made.")));
                            if (host.Value is not null)
                                foreach (var check in host.Value.Checks.Where(item =>
                                    item.Assessment == ToolAssessment.Incompatible))
                                {
                                    var subject = $"host-tool:{check.WorkloadId}:{check.Tool}";
                                    findings.Add(new(Contract.Version, subject, subject,
                                        Revision(subject + raw), "host-tool-incompatibility", Priority.Low,
                                        check.Observed, check.Required,
                                        new FindingScope(null, null, check.WorkloadId), Assessment.Partial,
                                        [new Evidence("supplied-host-inventory",
                                            new Uri(Path.Combine(inventory.Root, BundlePath)).AbsoluteUri,
                                            DateTimeOffset.UtcNow)],
                                        "review-process-local-toolchain-requirements", true, "toolchain"));
                                }
                        }
                    }
                }
                if (bundle.Workload is null)
                    coverage.Add(new("workload-set", Assessment.Unknown,
                        "No exact workload-set candidates or dependencies were supplied."));
                if (host is null)
                    coverage.Add(new("host-compatibility", Assessment.Unknown,
                        "Host compatibility requires a compatible workload result and explicit host inventory."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or PlatformNotSupportedException or NotSupportedException)
            {
                bundle = null;
                coverage.Add(new("toolchain-evidence", Assessment.Blocked,
                    $"Scoped metadata could not be read: {Bounded(ex.Message)}"));
            }
        }
        else
        {
            coverage.Add(new("workload-set", Assessment.Unknown,
                $"No explicit {BundlePath} evidence; workloads and host tools were not inferred."));
            coverage.Add(new("host-compatibility", Assessment.Unknown,
                "Xcode, JDK, Android, and Windows SDK inventory was not supplied."));
        }

        var text = observed?.InstalledRuntimes;
        if (text is not null || bundle?.Deployment is not null)
        {
            var installed = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0] is
                    ("Microsoft.NETCore.App" or "Microsoft.AspNetCore.App" or "Microsoft.WindowsDesktop.App"))
                .Select(parts => new InstalledRuntimeFramework(parts[0], parts[1])).ToArray();
            var deployment = bundle?.Deployment ?? new RuntimeDeployment(RuntimeDeploymentKind.Unknown);
            var components = deployment.Kind switch
            {
                RuntimeDeploymentKind.Container => deployment.DeclaredContainerRuntimeVersions,
                RuntimeDeploymentKind.SelfContained => deployment.EmbeddedRuntimeVersions,
                _ => null
            };
            var channels = installed.Select(item => item.Version)
                .Concat(components?.Values ?? [])
                .Select(version => string.Join('.', version.Split('.').Take(2)))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var channel in channels.Take(4))
            {
                token.ThrowIfCancellationRequested();
                var matching = installed.Where(item =>
                    item.Version.StartsWith(channel + ".", StringComparison.Ordinal)).ToArray();
                try
                {
                    var (index, indexFreshness) = await metadata.ReadAsync(ReleaseMetadata.IndexUrl, token);
                    using var catalog = JsonDocument.Parse(index);
                    var uri = catalog.RootElement.GetProperty("releases-index").EnumerateArray()
                        .FirstOrDefault(item => item.GetProperty("channel-version").GetString() == channel);
                    if (uri.ValueKind != JsonValueKind.Object ||
                        !uri.TryGetProperty("releases.json", out var releaseUrl))
                    {
                        coverage.Add(new("runtime:" + channel, Assessment.Unknown,
                            "No official channel metadata for observed runtime."));
                        continue;
                    }
                    var source = releaseUrl.GetString()!;
                    var (body, freshness) = await metadata.ReadAsync(source, token);
                    if (freshness != Assessment.Confirmed || indexFreshness != Assessment.Confirmed)
                    {
                        coverage.Add(new("runtime:" + channel, Assessment.Stale,
                            "Official release metadata is stale; runtime recommendations are withheld."));
                        continue;
                    }
                    var retrieved = (metadata as ReleaseMetadata)?.LastRetrieved(source)
                        ?? DateTimeOffset.UtcNow;
                    var evidence = new ToolEvidence(source, retrieved, Revision(body));
                    var localEvidence = new ToolEvidence("native-dotnet --list-runtimes",
                        DateTimeOffset.UtcNow);
                    var result = RuntimeServicingAnalyzer.Analyze(new(
                        new VersionedJsonDocument(body, evidence), matching,
                        deployment,
                        new FreshnessCriteria(DateTimeOffset.UtcNow, TimeSpan.FromDays(7)),
                        localEvidence));
                    runtimes.Add(result);
                    var deploymentDetail = result.Value?.DeploymentFrameworks.Count > 0
                        ? $"Explicit {deployment.Kind} deployment evidence analyzed for " +
                          $"{result.Value.DeploymentFrameworks.Count} framework(s)."
                        : deployment.Kind == RuntimeDeploymentKind.Unknown
                            ? "Deployment runtime remains unknown."
                            : $"Explicit {deployment.Kind} deployment has no analyzable framework evidence.";
                    coverage.Add(new("runtime:" + channel, Map(result.Assessment),
                        Detail(result.Diagnostics, "Local installed frameworks analyzed.") +
                        " " + deploymentDetail));
                    if (result.Value is null) continue;
                    foreach (var local in result.Value.LocalFrameworks.Where(f =>
                        f.SecurityServicingVersion is not null &&
                        f.Assessment == ToolAssessment.Partial))
                    {
                        var subject = $"runtime:{local.Framework}:{local.ObservedVersion}";
                        findings.Add(new(Contract.Version, subject, subject,
                            Revision(subject + evidence.Revision), "local-runtime-security-servicing",
                            Priority.Medium, local.ObservedVersion, local.SecurityServicingVersion,
                            new FindingScope(null, local.Framework, null), Assessment.Partial,
                            [new Evidence("official-runtime-release", source, retrieved),
                                new Evidence("installed-runtime", "dotnet://list-runtimes", DateTimeOffset.UtcNow)],
                            "review-local-runtime-servicing-not-deployment", true, "runtime"));
                    }
                    foreach (var deployed in result.Value.DeploymentFrameworks.Where(f =>
                        f.SecurityServicingVersion is not null &&
                        f.Assessment == ToolAssessment.Partial))
                    {
                        var subject = $"deployment-runtime:{deployed.Scope}:{deployed.Framework}";
                        findings.Add(new(Contract.Version, subject, subject,
                            Revision(subject + deployed.ObservedVersion + evidence.Revision),
                            "deployment-runtime-security-servicing", Priority.Medium,
                            deployed.ObservedVersion, deployed.SecurityServicingVersion,
                            new FindingScope(null, deployed.Framework, null), Assessment.Partial,
                            [new Evidence("official-runtime-release", source, retrieved),
                                new Evidence("supplied-deployment-runtime",
                                    new Uri(Path.Combine(inventory.Root, BundlePath)).AbsoluteUri,
                                    DateTimeOffset.UtcNow)],
                            deployed.Action ?? "review-deployment-runtime-evidence", true, "runtime"));
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                    InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
                {
                    coverage.Add(new("runtime:" + channel, Assessment.Unknown, Bounded(ex.Message)));
                }
            }
            if (installed.Length == 0)
                coverage.Add(new("runtime-local", Assessment.Unknown, "No supported local shared runtime was observed."));
            if (channels.Length > 4)
                coverage.Add(new("runtime-scan-limit", Assessment.Partial,
                    "Only four installed runtime channels were evaluated."));
        }
        else
            coverage.Add(new("runtime", Assessment.Unknown,
                "Installed shared runtimes were not observed; deployment runtime is unknown."));
        return new(findings, coverage, workload, host, runtimes);
    }

    static Assessment Map(ToolAssessment assessment) => assessment switch
    {
        ToolAssessment.Compatible => Assessment.Confirmed,
        ToolAssessment.Incompatible or ToolAssessment.Partial or ToolAssessment.Unvalidated => Assessment.Partial,
        ToolAssessment.Blocked => Assessment.Blocked,
        _ => Assessment.Unknown
    };

    static string Detail(IReadOnlyList<Diagnostic> diagnostics, string fallback) =>
        diagnostics.Count == 0 ? fallback :
            string.Join("; ", diagnostics.Take(4).Select(d => Bounded(d.Message)));

    static void Validate(ToolchainEvidenceBundle bundle)
    {
        static void Require(bool valid, string path)
        {
            if (!valid) throw new InvalidDataException($"Toolchain evidence requires {path}.");
        }
        static void Evidence(ToolEvidence? value, string path) =>
            Require(value is not null && !string.IsNullOrWhiteSpace(value.Source), path);
        static void Document(VersionedJsonDocument? value, string path)
        {
            Require(value is not null && value.Json is not null, path);
            Evidence(value!.Evidence, path + ".evidence");
        }
        static void Versions(IReadOnlyDictionary<string, string>? values, string path)
        {
            Require(values is not null && values.All(pair =>
                !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)), path);
        }
        static void Requirements(IReadOnlyDictionary<string, VersionRequirement>? values, string path)
        {
            Require(values is not null && values.All(pair =>
                !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null &&
                !string.IsNullOrWhiteSpace(pair.Value.Range)), path);
            foreach (var item in values!.Values)
                if (item.Evidence is not null) Evidence(item.Evidence, path + ".evidence");
        }
        if (bundle.Workload is { } workload)
        {
            Require(!string.IsNullOrWhiteSpace(workload.SdkVersion), "workload.sdkVersion");
            Require(workload.AvailableSets is not null, "workload.availableSets");
            foreach (var set in workload.AvailableSets!)
            {
                Require(set is not null && !string.IsNullOrWhiteSpace(set.SdkFeatureBand) &&
                    !string.IsNullOrWhiteSpace(set.UserFacingVersion), "workload.availableSets[]");
                Document(set!.WorkloadSetManifest, "workload.availableSets[].workloadSetManifest");
            }
            Require(workload.SelectedWorkloads is not null &&
                workload.SelectedWorkloads.All(value => !string.IsNullOrWhiteSpace(value)),
                "workload.selectedWorkloads");
            Require(workload.DependencyDocumentsByWorkload is not null,
                "workload.dependencyDocumentsByWorkload");
            foreach (var (key, value) in workload.DependencyDocumentsByWorkload!)
            {
                Require(!string.IsNullOrWhiteSpace(key), "workload.dependencyDocumentsByWorkload key");
                Document(value, $"workload.dependencyDocumentsByWorkload[{key}]");
            }
            Require(workload.TupleContext is not null &&
                workload.TupleContext.MauiPackageVersions is not null &&
                workload.TupleContext.TargetFrameworks is not null, "workload.tupleContext");
            var tuple = workload.TupleContext!;
            Require(tuple.MauiPackageVersions?.All(value => value is not null) == true &&
                tuple.TargetFrameworks?.All(value => value is not null) == true,
                "workload.tupleContext entries");
            if (tuple.AndroidTargets is not null)
                Require(tuple.AndroidTargets.All(value => value is not null &&
                    value.TargetFramework is not null), "workload.tupleContext.androidTargets");
            if (tuple.WindowsSdkRequirementsByWorkload is not null)
                Requirements(tuple.WindowsSdkRequirementsByWorkload,
                    "workload.tupleContext.windowsSdkRequirementsByWorkload");
            if (tuple.Evidence is not null)
                foreach (var item in tuple.Evidence) Evidence(item, "workload.tupleContext.evidence[]");
            Require(workload.Freshness is not null, "workload.freshness");
            if (workload.InstalledInventory is { } installed)
            {
                Require(installed.Manifests is not null &&
                    installed.Manifests.All(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                        pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Value.Version) &&
                        !string.IsNullOrWhiteSpace(pair.Value.SdkFeatureBand)),
                    "workload.installedInventory.manifests");
                Require(installed.Workloads is not null &&
                    installed.Workloads.All(value => !string.IsNullOrWhiteSpace(value)),
                    "workload.installedInventory.workloads");
                Evidence(installed.Evidence, "workload.installedInventory.evidence");
            }
        }
        if (bundle.HostInventory is { } host)
        {
            Evidence(host.Evidence, "hostInventory.evidence");
            Versions(host.WindowsSdkVersions, "hostInventory.windowsSdkVersions");
            Versions(host.PlatformSdkVersions, "hostInventory.platformSdkVersions");
            if (host.Android is { } android)
                Require(android.InstalledPlatforms is not null &&
                    android.InstalledBuildTools is not null && android.InstalledPackages is not null,
                    "hostInventory.android");
        }
        if (bundle.Targets is not null)
            foreach (var target in bundle.Targets)
            {
                Require(target is not null && !string.IsNullOrWhiteSpace(target.Target), "targets[]");
                if (target!.Evidence is not null) Evidence(target.Evidence, "targets[].evidence");
            }
        if (bundle.Suggestions is { } suggestions)
            Versions(suggestions.BuildProperties, "suggestions.buildProperties");
        if (bundle.MacOsRequirementsByWorkload is not null)
            Requirements(bundle.MacOsRequirementsByWorkload, "macOsRequirementsByWorkload");
        if (bundle.Deployment is { } deployment)
        {
            if (deployment.Evidence is not null) Evidence(deployment.Evidence, "deployment.evidence");
            if (deployment.Kind == RuntimeDeploymentKind.Container)
                Versions(deployment.DeclaredContainerRuntimeVersions,
                    "deployment.declaredContainerRuntimeVersions");
            if (deployment.Kind == RuntimeDeploymentKind.SelfContained)
                Versions(deployment.EmbeddedRuntimeVersions, "deployment.embeddedRuntimeVersions");
            if (deployment.DeclaredContainerRuntimeVersions is not null)
                Versions(deployment.DeclaredContainerRuntimeVersions, "deployment.declaredContainerRuntimeVersions");
            if (deployment.EmbeddedRuntimeVersions is not null)
                Versions(deployment.EmbeddedRuntimeVersions, "deployment.embeddedRuntimeVersions");
        }
    }
    static string Bounded(string value) => value.Length > 300 ? value[..300] : value;
    static string Revision(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

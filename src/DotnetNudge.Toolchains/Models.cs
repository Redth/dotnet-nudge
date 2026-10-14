using System.Text.RegularExpressions;

namespace DotnetNudge.Toolchains;

public enum Assessment
{
    Compatible,
    Incompatible,
    Unknown,
    Partial,
    Blocked,
    Unvalidated
}

public enum WorkloadInstallMode
{
    WorkloadSet,
    LooseManifests,
    Unknown
}

public enum RuntimeDeploymentKind
{
    FrameworkDependent,
    SelfContained,
    Container,
    Unknown
}

public enum RuntimeObservationScope
{
    LocalSharedRuntime,
    DeclaredContainerRuntime,
    EmbeddedSelfContainedRuntime
}

public sealed record Evidence(
    string Source,
    DateTimeOffset RetrievedAt,
    string? Revision = null,
    string? Detail = null);

public sealed record FreshnessCriteria(DateTimeOffset AsOf, TimeSpan MaximumAge);

public sealed record Diagnostic(string Code, string Message, Assessment Assessment);

public sealed record AnalysisResult<T>(
    T? Value,
    Assessment Assessment,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record GlobalJsonInfo(
    string? SdkVersion,
    string? WorkloadVersion,
    bool? AllowPrerelease,
    string? RollForward);

public sealed record VersionedJsonDocument(
    string Json,
    Evidence Evidence,
    string? SdkFeatureBand = null,
    string? ManifestVersion = null);

public sealed record WorkloadSetCandidate(
    string SdkFeatureBand,
    string UserFacingVersion,
    string? NuGetPackageVersion,
    VersionedJsonDocument WorkloadSetManifest);

public sealed record InstalledManifestInfo(string Version, string SdkFeatureBand);

public sealed record InstalledWorkloadInventory(
    WorkloadInstallMode Mode,
    string? WorkloadSetVersion,
    IReadOnlyDictionary<string, InstalledManifestInfo> Manifests,
    IReadOnlySet<string> Workloads,
    Evidence Evidence);

public sealed record WorkloadTupleContext(
    IReadOnlyList<string> MauiPackageVersions,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<AndroidProjectTarget>? AndroidTargets = null,
    IReadOnlyDictionary<string, VersionRequirement>? WindowsSdkRequirementsByWorkload = null,
    IReadOnlyList<Evidence>? Evidence = null);

public sealed record AndroidProjectTarget(
    string TargetFramework,
    string? MinimumSupportedOsApiLevel,
    string? CompileApiLevel,
    string? TargetApiLevel,
    string? BuildToolsVersion);

public sealed record WorkloadAnalysisRequest(
    string SdkVersion,
    string? PinnedWorkloadSetVersion,
    bool AllowPrerelease,
    IReadOnlyList<WorkloadSetCandidate> AvailableSets,
    IReadOnlySet<string> SelectedWorkloads,
    IReadOnlyDictionary<string, VersionedJsonDocument> DependencyDocumentsByWorkload,
    WorkloadTupleContext TupleContext,
    InstalledWorkloadInventory? InstalledInventory,
    FreshnessCriteria Freshness);

public sealed record WorkloadManifestSelection(
    string WorkloadId,
    string ManifestVersion,
    string ManifestSdkBand);

public sealed record WorkloadSelection(
    string SdkVersion,
    string SdkFeatureBand,
    string SelectedWorkloadSetVersion,
    string? NewerWorkloadSetVersionInBand,
    string? NuGetPackageVersion,
    WorkloadInstallMode? InstalledMode,
    IReadOnlyList<WorkloadManifestSelection> Manifests,
    IReadOnlyList<string> InstalledWorkloads,
    WorkloadTupleContext TupleContext);

public sealed record VersionRequirement(string Range, string? RecommendedVersion, Evidence? Evidence = null);

public sealed record AndroidRequirement(
    string? CompileApiLevel,
    string? BuildToolsVersion,
    IReadOnlyList<string> RequiredPackages);

public sealed record WorkloadRequirements(
    IReadOnlyDictionary<string, VersionRequirement> Jdks,
    IReadOnlyDictionary<string, VersionRequirement> Xcodes,
    IReadOnlyDictionary<string, AndroidRequirement> Android,
    IReadOnlyDictionary<string, VersionRequirement> WindowsSdks,
    IReadOnlyDictionary<string, string> PlatformSdkVersions);

public sealed record WorkloadAnalysis(
    WorkloadSelection Selection,
    WorkloadRequirements Requirements,
    IReadOnlyList<Evidence> Evidence);

public sealed record ObservedAndroidInventory(
    IReadOnlySet<string> InstalledPlatforms,
    IReadOnlySet<string> InstalledBuildTools,
    IReadOnlySet<string> InstalledPackages);

public sealed record HostToolInventory(
    string? MacOsVersion,
    string? JdkVersion,
    string? XcodeVersion,
    ObservedAndroidInventory? Android,
    IReadOnlyDictionary<string, string> WindowsSdkVersions,
    IReadOnlyDictionary<string, string> PlatformSdkVersions,
    Evidence Evidence);

public sealed record PlatformTargetAvailability(
    string Target,
    bool Available,
    string? Reason = null,
    Evidence? Evidence = null);

public sealed record ProcessLocalSuggestions(
    string? DeveloperDirectory,
    string? AndroidSdkRoot,
    IReadOnlyDictionary<string, string> BuildProperties);

public sealed record HostCompatibilityRequest(
    WorkloadAnalysis Analysis,
    HostToolInventory? Inventory,
    IReadOnlyList<PlatformTargetAvailability> Targets,
    ProcessLocalSuggestions Suggestions,
    FreshnessCriteria Freshness,
    IReadOnlyDictionary<string, VersionRequirement>? MacOsRequirementsByWorkload = null);

public sealed record HostRequirementCheck(
    string WorkloadId,
    string Tool,
    string Required,
    string? Observed,
    Assessment Assessment,
    string? Detail = null);

public sealed record HostCompatibilityResult(
    IReadOnlyList<HostRequirementCheck> Checks,
    IReadOnlyList<PlatformTargetAvailability> Targets,
    ProcessLocalSuggestions Suggestions);

public sealed record InstalledRuntimeFramework(
    string Name,
    string Version,
    string? InstallPath = null);

public sealed record RuntimeRelease(
    string Framework,
    string Version,
    bool Security,
    string ReleaseVersion,
    string ReleaseDate);

public sealed record RuntimeDeployment(
    RuntimeDeploymentKind Kind,
    string? DeclaredImage = null,
    IReadOnlyDictionary<string, string>? DeclaredContainerRuntimeVersions = null,
    IReadOnlyDictionary<string, string>? EmbeddedRuntimeVersions = null,
    Evidence? Evidence = null);

public sealed record RuntimeServicingRequest(
    VersionedJsonDocument ReleasesMetadata,
    IReadOnlyList<InstalledRuntimeFramework> InstalledFrameworks,
    RuntimeDeployment Deployment,
    FreshnessCriteria Freshness,
    Evidence? InventoryEvidence = null);

public sealed record RuntimeFrameworkFinding(
    RuntimeObservationScope Scope,
    string Framework,
    string ObservedVersion,
    string? SecurityServicingVersion,
    bool SecurityReleaseAvailable,
    Assessment Assessment,
    string? Action,
    string? Detail);

public sealed record RuntimeServicingResult(
    IReadOnlyList<RuntimeFrameworkFinding> LocalFrameworks,
    IReadOnlyList<RuntimeFrameworkFinding> DeploymentFrameworks,
    RuntimeDeployment Deployment,
    IReadOnlyList<RuntimeRelease> SecurityReleases);

public static class SdkVersioning
{
    private static readonly Regex SupportedSdkVersion = new(
        @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z",
        RegexOptions.CultureInvariant);

    public static bool TryGetFeatureBand(string sdkVersion, out string featureBand)
    {
        featureBand = string.Empty;
        if (!NuGet.Versioning.NuGetVersion.TryParse(sdkVersion, out var parsed)
            || !SupportedSdkVersion.IsMatch(sdkVersion)
            || parsed.Version.Build < 0)
        {
            return false;
        }

        var band = parsed.Version.Build / 100 * 100;
        featureBand = $"{parsed.Version.Major}.{parsed.Version.Minor}.{band:000}";
        return true;
    }

    public static bool IsCanonicalFeatureBand(string value) =>
        TryGetFeatureBand(value, out var featureBand)
        && NuGet.Versioning.NuGetVersion.TryParse(value, out var parsed)
        && parsed.Version.Build >= 100
        && parsed.Version.Build % 100 == 0
        && string.Equals(value, featureBand, StringComparison.Ordinal);
}

public static class WorkloadVersioning
{
    private static readonly Regex SupportedUserVersion = new(
        @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z",
        RegexOptions.CultureInvariant);

    public static bool IsSupportedUserVersion(string userVersion) =>
        SupportedUserVersion.IsMatch(userVersion)
        && NuGet.Versioning.NuGetVersion.TryParse(userVersion, out _);

    public static bool TryMapStableToNuGetPackageVersion(string userVersion, out string? packageVersion)
    {
        packageVersion = null;
        if (!IsSupportedUserVersion(userVersion)
            || !NuGet.Versioning.NuGetVersion.TryParse(userVersion, out var version)
            || version.IsPrerelease
            || version.Version.Minor != 0
            || version.Version.Build < 100
            || version.HasMetadata)
        {
            return false;
        }

        packageVersion = $"{version.Version.Major}.{version.Version.Build}.0";
        return true;
    }
}

public static class ManifestVersioning
{
    private static readonly Regex SupportedManifestVersion = new(
        @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z",
        RegexOptions.CultureInvariant);

    public static bool IsSupportedManifestVersion(string version) =>
        SupportedManifestVersion.IsMatch(version)
        && NuGet.Versioning.NuGetVersion.TryParse(version, out _);
}

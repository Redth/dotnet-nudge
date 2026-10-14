using System.Text.Json;
using NuGet.Versioning;

namespace DotnetNudge.Toolchains;

public static class WorkloadCompatibilityAnalyzer
{
    public static AnalysisResult<WorkloadAnalysis> Analyze(WorkloadAnalysisRequest request)
    {
        if (!SdkVersioning.TryGetFeatureBand(request.SdkVersion, out var featureBand))
        {
            return Failure("sdk.version-unknown", $"SDK version '{request.SdkVersion}' is not a supported .NET version format.");
        }

        var bandCandidates = request.AvailableSets
            .Where(x => string.Equals(x.SdkFeatureBand, featureBand, StringComparison.Ordinal))
            .Where(x => IsSupportedWorkloadSetVersion(x.UserFacingVersion))
            .ToArray();
        if (bandCandidates.GroupBy(x => x.UserFacingVersion, StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            return Failure(
                "workload-set.duplicate-candidate",
                $"Multiple workload set metadata documents claim the same version in SDK feature band {featureBand}.");
        }

        var sameBandCandidates = bandCandidates
            .Where(x => request.AllowPrerelease || !NuGetVersion.Parse(x.UserFacingVersion).IsPrerelease)
            .ToArray();

        WorkloadSetCandidate? selected;
        if (request.PinnedWorkloadSetVersion is not null)
        {
            selected = bandCandidates.SingleOrDefault(x =>
                string.Equals(x.UserFacingVersion, request.PinnedWorkloadSetVersion, StringComparison.Ordinal));
            if (selected is null)
            {
                return Failure(
                    "workload-set.pin-unavailable",
                    $"Pinned workload set '{request.PinnedWorkloadSetVersion}' has no supplied metadata for SDK feature band {featureBand}; another band's newest set is not a substitute.");
            }
        }
        else
        {
            selected = sameBandCandidates
                .OrderBy(x => NuGetVersion.Parse(x.UserFacingVersion), VersionComparer.VersionRelease)
                .LastOrDefault();
            if (selected is null)
            {
                return Failure("workload-set.missing", $"No eligible workload set metadata was supplied for SDK feature band {featureBand}.");
            }
        }

        var diagnostics = new List<Diagnostic>();
        var evidence = new List<Evidence> { selected.WorkloadSetManifest.Evidence };
        if (request.TupleContext.Evidence is not null)
        {
            evidence.AddRange(request.TupleContext.Evidence);
        }

        if (selected.SdkFeatureBand != featureBand)
        {
            return Failure("workload-set.band-mismatch", "Selected workload set metadata does not match the effective SDK feature band.");
        }

        var packageVersion = selected.NuGetPackageVersion;
        if (packageVersion is null
            && !WorkloadVersioning.TryMapStableToNuGetPackageVersion(selected.UserFacingVersion, out packageVersion))
        {
            diagnostics.Add(new(
                "workload-set.package-version-unknown",
                $"No explicit NuGet package version was supplied for workload set '{selected.UserFacingVersion}', and its prerelease encoding is not mapped.",
                Assessment.Partial));
        }

        if (packageVersion is not null && !NuGetVersion.TryParse(packageVersion, out _))
        {
            diagnostics.Add(new(
                "workload-set.package-version-invalid",
                $"NuGet package version '{packageVersion}' is invalid.",
                Assessment.Unknown));
            packageVersion = null;
        }

        JsonDocument setDocument;
        try
        {
            setDocument = JsonDocument.Parse(selected.WorkloadSetManifest.Json);
        }
        catch (JsonException ex)
        {
            return Failure("workload-set.malformed", $"Workload set metadata is malformed: {ex.Message}");
        }

        using (setDocument)
        {
            if (setDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure("workload-set.root", "Workload set metadata must be a JSON object.");
            }

            var manifests = new List<WorkloadManifestSelection>();
            var jdks = new Dictionary<string, VersionRequirement>(StringComparer.Ordinal);
            var xcodes = new Dictionary<string, VersionRequirement>(StringComparer.Ordinal);
            var android = new Dictionary<string, AndroidRequirement>(StringComparer.Ordinal);
            var platformSdks = new Dictionary<string, string>(StringComparer.Ordinal);
            var windowsSdks = request.TupleContext.WindowsSdkRequirementsByWorkload is null
                ? new Dictionary<string, VersionRequirement>(StringComparer.Ordinal)
                : new Dictionary<string, VersionRequirement>(
                    request.TupleContext.WindowsSdkRequirementsByWorkload,
                    StringComparer.Ordinal);
            if (request.TupleContext.TargetFrameworks.Any(x =>
                    x.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                && windowsSdks.Count == 0)
            {
                diagnostics.Add(new(
                    "windows-sdk.requirements-missing",
                    "A Windows target framework was supplied without exact Windows SDK requirement metadata.",
                    Assessment.Partial));
            }

            foreach (var (workloadId, requirement) in windowsSdks)
            {
                if (requirement.Evidence is not null)
                {
                    evidence.Add(requirement.Evidence);
                }

                if (!VersionRange.TryParse(requirement.Range, out _))
                {
                    diagnostics.Add(new(
                        "windows-sdk.requirement-unknown",
                        $"Windows SDK requirement for '{workloadId}' has an unsupported version range.",
                        Assessment.Partial));
                }
            }

            foreach (var workloadId in request.SelectedWorkloads.Order(StringComparer.Ordinal))
            {
                if (!setDocument.RootElement.TryGetProperty(workloadId, out var mapping)
                    || mapping.ValueKind != JsonValueKind.String)
                {
                    diagnostics.Add(new(
                        "workload-set.manifest-missing",
                        $"The selected set does not provide an exact manifest mapping for workload '{workloadId}'.",
                        Assessment.Partial));
                    continue;
                }

                var parts = mapping.GetString()!.Split('/');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])
                    || !SdkVersioning.TryGetFeatureBand(parts[1], out _))
                {
                    diagnostics.Add(new(
                        "workload-set.manifest-mapping-unknown",
                        $"Manifest mapping '{mapping.GetString()}' for workload '{workloadId}' is not in the supported version/SDK-band format.",
                        Assessment.Partial));
                    continue;
                }

                var manifest = new WorkloadManifestSelection(workloadId, parts[0], parts[1]);
                manifests.Add(manifest);
                if (!request.DependencyDocumentsByWorkload.TryGetValue(workloadId, out var dependencyDocument))
                {
                    diagnostics.Add(new(
                        "workload-dependencies.missing",
                        $"No exact WorkloadDependencies.json document was supplied for '{workloadId}' {manifest.ManifestVersion}/{manifest.ManifestSdkBand}.",
                        Assessment.Partial));
                    continue;
                }

                evidence.Add(dependencyDocument.Evidence);
                if (!string.Equals(dependencyDocument.ManifestVersion, manifest.ManifestVersion, StringComparison.Ordinal)
                    || !string.Equals(dependencyDocument.SdkFeatureBand, manifest.ManifestSdkBand, StringComparison.Ordinal))
                {
                    diagnostics.Add(new(
                        "workload-dependencies.version-mismatch",
                        $"Dependency metadata for '{workloadId}' does not identify the exact manifest version and SDK band selected by the workload set.",
                        Assessment.Partial));
                    continue;
                }

                try
                {
                    using var dependencies = JsonDocument.Parse(dependencyDocument.Json);
                    if (dependencies.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new(
                            "workload-dependencies.root",
                            $"Dependency metadata for '{workloadId}' must be a JSON object.",
                            Assessment.Partial));
                        continue;
                    }

                    if (!dependencies.RootElement.TryGetProperty(workloadId, out var workload))
                    {
                        diagnostics.Add(new(
                            "workload-dependencies.entry-missing",
                            $"The dependency document has no exact '{workloadId}' entry.",
                            Assessment.Partial));
                        continue;
                    }

                    if (workload.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new(
                            "workload-dependencies.entry-unknown",
                            $"Dependency entry for '{workloadId}' must be a JSON object.",
                            Assessment.Partial));
                        continue;
                    }

                    ReadVersionRequirement(workload, "jdk", workloadId, jdks, diagnostics);
                    ReadVersionRequirement(workload, "xcode", workloadId, xcodes, diagnostics);
                    ReadAndroidRequirement(workload, workloadId, android, diagnostics);
                    ReadPlatformSdkVersion(workload, workloadId, platformSdks, diagnostics);
                }
                catch (JsonException ex)
                {
                    diagnostics.Add(new(
                        "workload-dependencies.malformed",
                        $"Dependency metadata for '{workloadId}' is malformed: {ex.Message}",
                        Assessment.Partial));
                }
            }

            if (request.InstalledInventory is null)
            {
                diagnostics.Add(new(
                    "inventory.missing",
                    "Installed workload mode, set, manifests, and workload IDs were not observed; installation compatibility is unknown.",
                    Assessment.Unknown));
            }
            else
            {
                evidence.Add(request.InstalledInventory.Evidence);
                CheckInstalledInventory(request, selected, manifests, diagnostics);
            }

            var newer = sameBandCandidates
                .OrderBy(x => NuGetVersion.Parse(x.UserFacingVersion), VersionComparer.VersionRelease)
                .LastOrDefault();
            var selection = new WorkloadSelection(
                request.SdkVersion,
                featureBand,
                selected.UserFacingVersion,
                newer is not null
                    && !string.Equals(newer.UserFacingVersion, selected.UserFacingVersion, StringComparison.Ordinal)
                    ? newer.UserFacingVersion
                    : null,
                packageVersion,
                request.InstalledInventory?.Mode,
                manifests,
                request.InstalledInventory?.Workloads.Order(StringComparer.Ordinal).ToArray() ?? [],
                request.TupleContext);
            var value = new WorkloadAnalysis(
                selection,
                new WorkloadRequirements(jdks, xcodes, android, windowsSdks, platformSdks),
                evidence.Distinct().ToArray());
            var assessment = diagnostics.Any(x => x.Assessment == Assessment.Incompatible)
                ? Assessment.Incompatible
                : diagnostics.Any(x => x.Assessment == Assessment.Unknown)
                    ? Assessment.Unknown
                    : diagnostics.Count > 0 ? Assessment.Partial : Assessment.Compatible;
            return new(value, assessment, evidence.Distinct().ToArray(), diagnostics);
        }

        AnalysisResult<WorkloadAnalysis> Failure(string code, string message) =>
            new(
                null,
                Assessment.Unknown,
                request.AvailableSets.Select(x => x.WorkloadSetManifest.Evidence).Distinct().ToArray(),
                [new(code, message, Assessment.Unknown)]);
    }

    private static bool IsSupportedWorkloadSetVersion(string version) =>
        WorkloadVersioning.IsSupportedUserVersion(version);

    private static void ReadVersionRequirement(
        JsonElement workload,
        string propertyName,
        string workloadId,
        IDictionary<string, VersionRequirement> destination,
        ICollection<Diagnostic> diagnostics)
    {
        if (!workload.TryGetProperty(propertyName, out var requirement))
        {
            return;
        }

        if (requirement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(
                $"workload-dependencies.{propertyName}-unknown",
                $"The {propertyName} requirement for '{workloadId}' must be a JSON object.",
                Assessment.Partial));
            return;
        }

        var range = GetString(requirement, "version");
        if (range is null || !VersionRange.TryParse(range, out _))
        {
            diagnostics.Add(new(
                $"workload-dependencies.{propertyName}-range-unknown",
                $"The {propertyName} version requirement for '{workloadId}' is missing or has an unsupported range format.",
                Assessment.Partial));
            return;
        }

        var recommendedVersion = GetString(requirement, "recommendedVersion");
        if (requirement.TryGetProperty("recommendedVersion", out var recommended)
            && recommended.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new(
                $"workload-dependencies.{propertyName}-recommended-version-unknown",
                $"The recommended {propertyName} version for '{workloadId}' has an unsupported type.",
                Assessment.Partial));
        }

        destination[workloadId] = new(range, recommendedVersion);
    }

    private static void ReadAndroidRequirement(
        JsonElement workload,
        string workloadId,
        IDictionary<string, AndroidRequirement> destination,
        ICollection<Diagnostic> diagnostics)
    {
        if (!workload.TryGetProperty("androidsdk", out var requirement))
        {
            return;
        }

        if (requirement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(
                "workload-dependencies.androidsdk-unknown",
                $"Android SDK requirements for '{workloadId}' must be a JSON object.",
                Assessment.Partial));
            return;
        }

        var malformed = false;
        foreach (var property in new[] { "apiLevel", "buildToolsVersion" })
        {
            if (requirement.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.String)
            {
                malformed = true;
                diagnostics.Add(new(
                    $"workload-dependencies.android-{property}-unknown",
                    $"Android {property} for '{workloadId}' must be a string.",
                    Assessment.Partial));
            }
        }

        var apiLevel = GetString(requirement, "apiLevel");
        var buildTools = GetString(requirement, "buildToolsVersion");
        var packages = new List<string>();
        if (requirement.TryGetProperty("packages", out var packageArray))
        {
            if (packageArray.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new(
                    "workload-dependencies.android-packages-unknown",
                    $"Android packages for '{workloadId}' are not an array.",
                    Assessment.Partial));
                return;
            }

            foreach (var package in packageArray.EnumerateArray())
            {
                if (package.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(package.GetString()))
                {
                    malformed = true;
                    diagnostics.Add(new(
                        "workload-dependencies.android-package-unknown",
                        $"An Android package requirement for '{workloadId}' is not a non-empty string.",
                        Assessment.Partial));
                    continue;
                }

                packages.Add(package.GetString()!);
            }
        }

        if (apiLevel is null && buildTools is null && packages.Count == 0)
        {
            diagnostics.Add(new(
                "workload-dependencies.android-requirements-empty",
                $"Android requirements for '{workloadId}' contain no supported API, build-tools, or package information.",
                Assessment.Partial));
            return;
        }

        if (malformed)
        {
            return;
        }

        destination[workloadId] = new(apiLevel, buildTools, packages);
    }

    private static void ReadPlatformSdkVersion(
        JsonElement workload,
        string workloadId,
        IDictionary<string, string> destination,
        ICollection<Diagnostic> diagnostics)
    {
        if (!workload.TryGetProperty("sdk", out var sdk))
        {
            return;
        }

        var version = GetString(sdk, "version");
        if (sdk.ValueKind != JsonValueKind.Object
            || version is null
            || !NuGetVersion.TryParse(version, out _))
        {
            diagnostics.Add(new(
                "workload-dependencies.platform-sdk-unknown",
                $"The platform SDK version for '{workloadId}' has an unsupported shape or version.",
                Assessment.Partial));
            return;
        }

        destination[workloadId] = version;
    }

    private static void CheckInstalledInventory(
        WorkloadAnalysisRequest request,
        WorkloadSetCandidate selected,
        IReadOnlyCollection<WorkloadManifestSelection> manifests,
        ICollection<Diagnostic> diagnostics)
    {
        var inventory = request.InstalledInventory!;
        if (inventory.Mode == WorkloadInstallMode.Unknown)
        {
            diagnostics.Add(new(
                "inventory.mode-unknown",
                "Installed workload update mode is unknown.",
                Assessment.Unknown));
        }

        if (inventory.Mode == WorkloadInstallMode.WorkloadSet
            && !string.Equals(inventory.WorkloadSetVersion, selected.UserFacingVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "inventory.workload-set-differs",
                $"Installed workload set '{inventory.WorkloadSetVersion ?? "unknown"}' differs from the analyzed set '{selected.UserFacingVersion}'.",
                Assessment.Incompatible));
        }

        foreach (var workloadId in request.SelectedWorkloads)
        {
            if (!inventory.Workloads.Contains(workloadId))
            {
                diagnostics.Add(new(
                    "inventory.workload-not-installed",
                    $"Selected workload '{workloadId}' is not present in the observed installed workload set.",
                    Assessment.Incompatible));
            }
        }

        foreach (var manifest in manifests)
        {
            if (!inventory.ManifestVersions.TryGetValue(manifest.WorkloadId, out var installedVersion))
            {
                diagnostics.Add(new(
                    "inventory.manifest-missing",
                    $"Installed manifest version for '{manifest.WorkloadId}' was not observed.",
                    Assessment.Unknown));
            }
            else if (!string.Equals(installedVersion, manifest.ManifestVersion, StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "inventory.manifest-differs",
                    $"Installed manifest '{manifest.WorkloadId}' is {installedVersion}; analyzed set selects {manifest.ManifestVersion}.",
                    Assessment.Incompatible));
            }
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class HostCompatibilityAnalyzer
{
    public static AnalysisResult<HostCompatibilityResult> Analyze(HostCompatibilityRequest request)
    {
        var checks = new List<HostRequirementCheck>();
        var diagnostics = new List<Diagnostic>();
        var evidence = request.Analysis.Evidence.ToList();
        evidence.AddRange(request.Targets.Where(x => x.Evidence is not null).Select(x => x.Evidence!));
        if (request.Inventory is not null)
        {
            evidence.Add(request.Inventory.Evidence);
        }

        foreach (var target in request.Targets.Where(x => !x.Available))
        {
            diagnostics.Add(new(
                "platform.target-unavailable",
                $"{target.Target} is unavailable: {target.Reason ?? "the required host or device was not observed"}.",
                Assessment.Blocked));
        }

        foreach (var (workloadId, requirement) in request.Analysis.Requirements.Jdks)
        {
            var observed = request.Inventory?.JdkVersion;
            checks.Add(CheckVersion(workloadId, "JDK", requirement.Range, observed));
        }

        foreach (var (workloadId, requirement) in request.Analysis.Requirements.Xcodes)
        {
            var observed = request.Inventory?.XcodeVersion;
            checks.Add(CheckVersion(workloadId, "Xcode", requirement.Range, observed));
            var macOsVersion = request.Inventory?.MacOsVersion;
            if (request.MacOsRequirementsByWorkload?.TryGetValue(workloadId, out var macOsRequirement) == true)
            {
                checks.Add(CheckVersion(workloadId, "macOS", macOsRequirement.Range, macOsVersion));
            }
            else
            {
                checks.Add(new(
                    workloadId,
                    "macOS",
                    "Version-specific host requirement metadata",
                    macOsVersion,
                    macOsVersion is null ? Assessment.Unknown : Assessment.Unvalidated,
                    macOsVersion is null
                        ? "macOS version is not observed."
                        : "No exact macOS requirement was supplied; Xcode compatibility does not establish macOS compatibility."));
            }
        }

        foreach (var (workloadId, requirement) in request.Analysis.Requirements.WindowsSdks)
        {
            var observed = request.Inventory?.WindowsSdkVersions.GetValueOrDefault(workloadId);
            checks.Add(CheckVersion(workloadId, "Windows SDK", requirement.Range, observed));
        }

        foreach (var (workloadId, requiredVersion) in request.Analysis.Requirements.PlatformSdkVersions)
        {
            var observed = request.Inventory?.PlatformSdkVersions.GetValueOrDefault(workloadId);
            checks.Add(CheckExactVersion(workloadId, "Platform SDK", requiredVersion, observed));
        }

        foreach (var (workloadId, requirement) in request.Analysis.Requirements.Android)
        {
            var android = request.Inventory?.Android;
            if (requirement.CompileApiLevel is { } apiLevel)
            {
                var observed = android is null ? null : string.Join(",", android.InstalledPlatforms.Order(StringComparer.Ordinal));
                var assessment = android is null
                    ? Assessment.Unknown
                    : android.InstalledPlatforms.Contains(apiLevel) ? Assessment.Compatible : Assessment.Incompatible;
                checks.Add(new(
                    workloadId,
                    "Android compile API",
                    apiLevel,
                    observed,
                    assessment,
                    assessment == Assessment.Incompatible
                        ? "The compile API platform is missing; the application's minimum supported OS is unchanged."
                        : null));
            }

            if (requirement.BuildToolsVersion is { } buildToolsVersion)
            {
                var observed = android is null ? null : string.Join(",", android.InstalledBuildTools.Order(StringComparer.Ordinal));
                var assessment = android is null
                    ? Assessment.Unknown
                    : android.InstalledBuildTools.Contains(buildToolsVersion) ? Assessment.Compatible : Assessment.Incompatible;
                checks.Add(new(workloadId, "Android build tools", buildToolsVersion, observed, assessment));
            }

            foreach (var package in requirement.RequiredPackages)
            {
                var assessment = android is null
                    ? Assessment.Unknown
                    : android.InstalledPackages.Contains(package) ? Assessment.Compatible : Assessment.Incompatible;
                var observed = android is null
                    ? null
                    : android.InstalledPackages.Contains(package) ? package : "not installed";
                checks.Add(new(
                    workloadId,
                    "Android package",
                    package,
                    observed,
                    assessment));
            }
        }

        foreach (var target in request.Targets.Where(x => x.Available))
        {
            diagnostics.Add(new(
                "platform.target-unvalidated",
                $"{target.Target} is reported available, but this analysis does not execute or validate a build for it.",
                Assessment.Unvalidated));
        }

        var result = new HostCompatibilityResult(checks, request.Targets, request.Suggestions);
        var overallAssessment = diagnostics.Any(x => x.Assessment == Assessment.Blocked)
            || checks.Any(x => x.Assessment == Assessment.Incompatible)
                ? Assessment.Blocked
                : request.Inventory is null || checks.Any(x => x.Assessment == Assessment.Unknown)
                    ? Assessment.Unknown
                    : Assessment.Unvalidated;
        return new(result, overallAssessment, evidence, diagnostics);
    }

    private static HostRequirementCheck CheckVersion(string workloadId, string tool, string rangeText, string? observed)
    {
        if (observed is null)
        {
            return new(workloadId, tool, rangeText, null, Assessment.Unknown, "Tool inventory is missing; compatibility is unknown.");
        }

        if (!VersionRange.TryParse(rangeText, out var range)
            || !NuGetVersion.TryParse(observed, out var version))
        {
            return new(workloadId, tool, rangeText, observed, Assessment.Unknown, "The requirement or observed version is not in a supported NuGet version format.");
        }

        return new(workloadId, tool, rangeText, observed,
            range.Satisfies(version) ? Assessment.Compatible : Assessment.Incompatible);
    }

    private static HostRequirementCheck CheckExactVersion(string workloadId, string tool, string required, string? observed)
    {
        if (observed is null)
        {
            return new(workloadId, tool, required, null, Assessment.Unknown, "Platform SDK inventory is missing.");
        }

        if (!NuGetVersion.TryParse(required, out var requiredVersion)
            || !NuGetVersion.TryParse(observed, out var observedVersion))
        {
            return new(workloadId, tool, required, observed, Assessment.Unknown, "A platform SDK version is not in a supported version format.");
        }

        return new(
            workloadId,
            tool,
            required,
            observed,
            VersionComparer.VersionRelease.Compare(requiredVersion, observedVersion) == 0
                ? Assessment.Compatible
                : Assessment.Incompatible);
    }
}

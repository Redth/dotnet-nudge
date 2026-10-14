using System.Text.Json;
using NuGet.Versioning;

namespace DotnetNudge.Toolchains;

public static class RuntimeServicingAnalyzer
{
    private static readonly string[] SupportedFrameworks =
    [
        "Microsoft.NETCore.App",
        "Microsoft.AspNetCore.App",
        "Microsoft.WindowsDesktop.App"
    ];

    public static AnalysisResult<RuntimeServicingResult> Analyze(RuntimeServicingRequest request)
    {
        var result = AnalyzeCore(request);
        return FreshnessEvaluator.Apply(result, request.Freshness);
    }

    private static AnalysisResult<RuntimeServicingResult> AnalyzeCore(RuntimeServicingRequest request)
    {
        IReadOnlyList<RuntimeRelease> releases;
        try
        {
            releases = ParseReleases(request.ReleasesMetadata.Json);
        }
        catch (JsonException ex)
        {
            return new(
                null,
                Assessment.Unknown,
                RequestEvidence(request),
                [new("runtime-releases.malformed", $"Official release metadata is malformed: {ex.Message}", Assessment.Unknown)]);
        }

        var diagnostics = new List<Diagnostic>();
        var localFindings = new List<RuntimeFrameworkFinding>();
        var deploymentFindings = new List<RuntimeFrameworkFinding>();
        var securityReleases = releases.Where(x => x.Security).ToArray();
        foreach (var installed in request.InstalledFrameworks)
        {
            if (!SupportedFrameworks.Contains(installed.Name, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(
                    "runtime.framework-unsupported",
                    $"Framework '{installed.Name}' is not in the supported shared-framework inventory; it was not guessed.",
                    Assessment.Partial));
                continue;
            }

            if (!NuGetVersion.TryParse(installed.Version, out var installedVersion))
            {
                localFindings.Add(new(
                    RuntimeObservationScope.LocalSharedRuntime,
                    installed.Name,
                    installed.Version,
                    null,
                    false,
                    Assessment.Unknown,
                    null,
                    "Installed framework version is not in a supported NuGet version format."));
                continue;
            }

            localFindings.Add(AssessObservedVersion(
                RuntimeObservationScope.LocalSharedRuntime,
                installed.Name,
                installed.Version,
                securityReleases,
                "update-local-framework-dependent-runtime"));
        }

        if (request.InstalledFrameworks.Count == 0)
        {
            diagnostics.Add(new(
                "runtime.inventory-missing",
                "No shared-framework inventory was supplied; local runtime servicing is unknown.",
                Assessment.Unknown));
        }

        var declaredComponents = request.Deployment.Kind switch
        {
            RuntimeDeploymentKind.Container => request.Deployment.DeclaredContainerRuntimeVersions,
            RuntimeDeploymentKind.SelfContained => request.Deployment.EmbeddedRuntimeVersions,
            _ => null
        };

        if (request.Deployment.Kind is RuntimeDeploymentKind.Container or RuntimeDeploymentKind.SelfContained)
        {
            if (declaredComponents is null || declaredComponents.Count == 0)
            {
                diagnostics.Add(new(
                    request.Deployment.Kind == RuntimeDeploymentKind.Container
                        ? "runtime.container-components-missing"
                        : "runtime.embedded-runtime-evidence-missing",
                    request.Deployment.Kind == RuntimeDeploymentKind.Container
                        ? "No per-framework runtime versions were supplied for the declared container image; image tags and workstation runtimes do not prove its contents."
                        : "Self-contained runtime contents are unknown without explicit embedded-runtime version evidence.",
                    Assessment.Unknown));
            }
            else
            {
                foreach (var (framework, version) in declaredComponents)
                {
                    if (!SupportedFrameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new(
                            "runtime.framework-unsupported",
                            $"Deployment component '{framework}' is not a supported shared framework and was not guessed.",
                            Assessment.Partial));
                        continue;
                    }

                    deploymentFindings.Add(AssessObservedVersion(
                        request.Deployment.Kind == RuntimeDeploymentKind.Container
                            ? RuntimeObservationScope.DeclaredContainerRuntime
                            : RuntimeObservationScope.EmbeddedSelfContainedRuntime,
                        framework,
                        version,
                        securityReleases,
                        request.Deployment.Kind == RuntimeDeploymentKind.Container
                            ? "update-container-runtime-image-and-redeploy"
                            : "republish-self-contained-application"));
                }
            }
        }
        else if (request.Deployment.Kind == RuntimeDeploymentKind.FrameworkDependent)
        {
            diagnostics.Add(new(
                "runtime.deployment-runtime-unknown",
                "A framework-dependent deployment's runtime inventory was not supplied; workstation shared-framework evidence is reported separately and does not establish a production runtime.",
                Assessment.Unknown));
        }
        else
        {
            diagnostics.Add(new(
                "runtime.deployment-unknown",
                "Deployment kind is unknown. Workstation runtime evidence cannot establish the runtime used in production.",
                Assessment.Unknown));
        }

        var allFindings = localFindings.Concat(deploymentFindings).ToArray();
        var assessment = allFindings.Any(x => x.Assessment == Assessment.Blocked)
            ? Assessment.Blocked
            : allFindings.Any(x => x.Assessment == Assessment.Unknown)
                || diagnostics.Any(x => x.Assessment == Assessment.Unknown)
                    ? Assessment.Unknown
                    : allFindings.Any(x => x.Assessment == Assessment.Partial)
                        || diagnostics.Count > 0
                            ? Assessment.Partial
                            : Assessment.Compatible;
        var result = new RuntimeServicingResult(localFindings, deploymentFindings, request.Deployment, securityReleases);
        return new(result, assessment, RequestEvidence(request), diagnostics);
    }

    public static IReadOnlyList<RuntimeRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("releases", out var releaseArray)
            || releaseArray.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The official channel metadata must contain a releases array.");
        }

        var releases = new List<RuntimeRelease>();
        foreach (var release in releaseArray.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each official release entry must be an object.");
            }

            var releaseVersion = GetString(release, "release-version");
            var releaseDate = GetString(release, "release-date");
            if (releaseVersion is null || releaseDate is null
                || !release.TryGetProperty("security", out var securityElement)
                || securityElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException("Each official release entry must contain release-version, release-date, and boolean security fields.");
            }

            var security = securityElement.GetBoolean();
            if (release.TryGetProperty("runtime", out var runtime))
            {
                if (runtime.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("A release runtime section must be an object.");
                }

                AddComponent(releases, runtime, "microsoft.netcore.app", "Microsoft.NETCore.App", releaseVersion, releaseDate, security);
                AddComponent(releases, runtime, "microsoft.aspnetcore.app", "Microsoft.AspNetCore.App", releaseVersion, releaseDate, security);
            }

            if (release.TryGetProperty("windowsdesktop", out var windowsDesktop))
            {
                if (windowsDesktop.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("A release windowsdesktop section must be an object.");
                }

                AddVersion(releases, "Microsoft.WindowsDesktop.App", GetString(windowsDesktop, "version"),
                    releaseVersion, releaseDate, security);
            }
        }

        return releases;
    }

    private static RuntimeFrameworkFinding AssessObservedVersion(
        RuntimeObservationScope scope,
        string framework,
        string observedText,
        IReadOnlyCollection<RuntimeRelease> securityReleases,
        string updateAction)
    {
        if (!NuGetVersion.TryParse(observedText, out var observedVersion))
        {
            return new(scope, framework, observedText, null, false, Assessment.Unknown, null,
                "Observed framework version is not in a supported NuGet version format.");
        }

        var latest = securityReleases
            .Where(x => string.Equals(x.Framework, framework, StringComparison.OrdinalIgnoreCase)
                && NuGetVersion.TryParse(x.Version, out var releaseVersion)
                && releaseVersion.Version.Major == observedVersion.Version.Major
                && releaseVersion.Version.Minor == observedVersion.Version.Minor)
            .Select(x => (Release: x, Version: NuGetVersion.Parse(x.Version)))
            .OrderBy(x => x.Version, VersionComparer.VersionRelease)
            .LastOrDefault();
        if (latest.Release is null)
        {
            return new(scope, framework, observedText, null, false, Assessment.Unknown, null,
                "No security servicing release for this observed major/minor was found in supplied metadata; support status and compatibility are unknown.");
        }

        var needsServicing = VersionComparer.VersionRelease.Compare(latest.Version, observedVersion) > 0;
        return new(
            scope,
            framework,
            observedText,
            needsServicing ? latest.Release.Version : null,
            needsServicing,
            needsServicing ? Assessment.Partial : Assessment.Compatible,
            needsServicing ? updateAction : null,
            needsServicing
                ? $"A security-servicing release {latest.Release.Version} is newer than the observed {scope} version; this is servicing evidence, not an application-specific vulnerability diagnosis."
                : $"Observed {scope} version is at or beyond the newest security-servicing release in the supplied metadata.");
    }

    private static void AddComponent(
        ICollection<RuntimeRelease> destination,
        JsonElement runtime,
        string property,
        string framework,
        string releaseVersion,
        string releaseDate,
        bool security)
    {
        if (runtime.TryGetProperty(property, out var component))
        {
            if (component.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"Release runtime component '{property}' must be an object.");
            }

            AddVersion(destination, framework, GetString(component, "version"), releaseVersion, releaseDate, security);
        }
    }

    private static void AddVersion(
        ICollection<RuntimeRelease> destination,
        string framework,
        string? version,
        string releaseVersion,
        string releaseDate,
        bool security)
    {
        if (version is not null && NuGetVersion.TryParse(version, out _))
        {
            destination.Add(new(framework, version, security, releaseVersion, releaseDate));
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<Evidence> RequestEvidence(RuntimeServicingRequest request) =>
        new[] { request.ReleasesMetadata.Evidence, request.InventoryEvidence, request.Deployment.Evidence }
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .ToArray();
}

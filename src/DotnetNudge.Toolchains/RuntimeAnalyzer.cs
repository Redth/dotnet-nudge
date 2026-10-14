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
        var findings = new List<RuntimeFrameworkFinding>();
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
                findings.Add(new(
                    installed.Name,
                    installed.Version,
                    null,
                    false,
                    Assessment.Unknown,
                    null,
                    "Installed framework version is not in a supported NuGet version format."));
                continue;
            }

            var frameworkReleases = securityReleases
                .Where(x => string.Equals(x.Framework, installed.Name, StringComparison.OrdinalIgnoreCase)
                    && NuGetVersion.TryParse(x.Version, out var releaseVersion)
                    && releaseVersion.Version.Major == installedVersion.Version.Major
                    && releaseVersion.Version.Minor == installedVersion.Version.Minor)
                .Select(x => (Release: x, Version: NuGetVersion.Parse(x.Version)))
                .OrderBy(x => x.Version, VersionComparer.VersionRelease)
                .ToArray();

            if (frameworkReleases.Length == 0)
            {
                findings.Add(new(
                    installed.Name,
                    installed.Version,
                    null,
                    false,
                    Assessment.Unknown,
                    null,
                    "No security servicing release for this installed major/minor was found in the supplied metadata; support status and compatibility are unknown."));
                continue;
            }

            var latestSecurityRelease = frameworkReleases[^1];
            var isNewer = VersionComparer.VersionRelease.Compare(latestSecurityRelease.Version, installedVersion) > 0;
            var deploymentAction = GetDeploymentAction(request.Deployment, latestSecurityRelease.Release.Version, out var deploymentAssessment, out var detail);
            findings.Add(new(
                installed.Name,
                installed.Version,
                isNewer ? latestSecurityRelease.Release.Version : null,
                isNewer,
                isNewer ? deploymentAssessment : Assessment.Compatible,
                isNewer ? deploymentAction : null,
                isNewer ? detail : "Installed framework is at or beyond the newest security-servicing release in the supplied metadata."));
        }

        if (request.InstalledFrameworks.Count == 0)
        {
            diagnostics.Add(new(
                "runtime.inventory-missing",
                "No shared-framework inventory was supplied; local runtime servicing is unknown.",
                Assessment.Unknown));
        }

        if (request.Deployment.Kind == RuntimeDeploymentKind.Unknown)
        {
            diagnostics.Add(new(
                "runtime.deployment-unknown",
                "Deployment kind is unknown. Workstation runtime evidence cannot establish the runtime used in production.",
                Assessment.Unknown));
        }

        var assessment = findings.Any(x => x.Assessment == Assessment.Blocked)
            ? Assessment.Blocked
            : findings.Any(x => x.Assessment == Assessment.Unknown)
                || diagnostics.Any(x => x.Assessment == Assessment.Unknown)
                    ? Assessment.Unknown
                    : findings.Any(x => x.Assessment == Assessment.Partial)
                        || diagnostics.Count > 0
                            ? Assessment.Partial
                            : Assessment.Compatible;
        var result = new RuntimeServicingResult(findings, request.Deployment, securityReleases);
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

    private static string? GetDeploymentAction(
        RuntimeDeployment deployment,
        string securityVersion,
        out Assessment assessment,
        out string detail)
    {
        detail = string.Empty;
        switch (deployment.Kind)
        {
            case RuntimeDeploymentKind.FrameworkDependent:
                assessment = Assessment.Partial;
                detail = "Security servicing is available for a local framework-dependent runtime; deployment remediation is not inferred.";
                return "update-local-framework-dependent-runtime";
            case RuntimeDeploymentKind.SelfContained:
                assessment = Assessment.Partial;
                detail = "Self-contained applications must be republished with patched runtime assets.";
                return "republish-self-contained-application";
            case RuntimeDeploymentKind.Container:
                if (!NuGetVersion.TryParse(deployment.DeclaredRuntimeImageVersion, out var imageVersion))
                {
                    assessment = Assessment.Unknown;
                    detail = "The declared container/runtime image version is missing or unsupported; host runtimes do not establish image contents.";
                    return null;
                }

                if (!NuGetVersion.TryParse(securityVersion, out var requiredVersion))
                {
                    assessment = Assessment.Unknown;
                    detail = "The supplied security release version is unsupported.";
                    return null;
                }

                if (VersionComparer.VersionRelease.Compare(imageVersion, requiredVersion) < 0)
                {
                    assessment = Assessment.Partial;
                    detail = $"Declared runtime image {deployment.DeclaredImage ?? "(unspecified)"} is older than security-servicing runtime {securityVersion}; update the image and redeploy.";
                    return "update-container-runtime-image-and-redeploy";
                }

                assessment = Assessment.Compatible;
                detail = "Declared runtime image version is at or beyond the supplied security-servicing version; the image was not executed or inspected.";
                return null;
            default:
                assessment = Assessment.Unknown;
                detail = "Deployment kind is unknown; workstation metadata cannot prove production remediation.";
                return null;
        }
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

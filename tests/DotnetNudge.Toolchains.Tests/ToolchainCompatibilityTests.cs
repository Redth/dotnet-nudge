using System.IO.Compression;
using System.Security.Cryptography;
using DotnetNudge.Toolchains;
using Xunit;

namespace DotnetNudge.Toolchains.Tests;

public sealed class ToolchainCompatibilityTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_global_json_pins_and_preserves_source_text()
    {
        var json = Fixture("global.json");
        var result = GlobalJsonReader.Parse(json, EvidenceFor("global.json"));

        Assert.Equal(Assessment.Compatible, result.Assessment);
        Assert.Equal("10.0.101", result.Value!.SdkVersion);
        Assert.Equal("10.0.102", result.Value.WorkloadVersion);
        Assert.False(result.Value.AllowPrerelease);
        Assert.Equal("latestFeature", result.Value.RollForward);
        Assert.Contains("\"custom\": \"preserve-me\"", json);
        Assert.Equal("fixture:global.json", result.Evidence.Single().Source);
    }

    [Theory]
    [InlineData("10.0.101", "10.0.100")]
    [InlineData("10.0.113", "10.0.100")]
    [InlineData("10.0.402", "10.0.400")]
    [InlineData("10.0.100-rc.1.25451.107", "10.0.100")]
    public void Computes_stable_and_prerelease_feature_bands(string sdk, string expected)
    {
        Assert.True(SdkVersioning.TryGetFeatureBand(sdk, out var band));
        Assert.Equal(expected, band);
    }

    [Fact]
    public void Rejects_unknown_sdk_version_format()
    {
        Assert.False(SdkVersioning.TryGetFeatureBand("not-a-sdk", out _));
        Assert.False(SdkVersioning.TryGetFeatureBand("10.0.101.0", out _));
    }

    [Fact]
    public void Keeps_pinned_workload_set_and_reports_newest_only_within_sdk_band()
    {
        var request = Request(
            pinned: "10.0.102",
            candidates:
            [
                Candidate("10.0.100", "10.0.102", "10.102.0"),
                Candidate("10.0.100", "10.0.105", "10.105.0"),
                Candidate("10.0.500", "10.0.501", "10.501.0")
            ]);

        var result = WorkloadCompatibilityAnalyzer.Analyze(request);

        Assert.Equal(Assessment.Compatible, result.Assessment);
        Assert.Equal("10.0.102", result.Value!.Selection.SelectedWorkloadSetVersion);
        Assert.Equal("10.0.105", result.Value.Selection.NewerWorkloadSetVersionInBand);
        Assert.Equal("10.102.0", result.Value.Selection.NuGetPackageVersion);
        Assert.Equal("35.0.50", result.Value.Selection.Manifests.Single().ManifestVersion);
        Assert.Equal("9.0.100", result.Value.Selection.Manifests.Single().ManifestSdkBand);
    }

    [Fact]
    public void Selects_newest_eligible_set_only_from_effective_sdk_band()
    {
        var request = Request(
            pinned: null,
            candidates:
            [
                Candidate("10.0.100", "10.0.102", "10.102.0"),
                Candidate("10.0.100", "10.0.105", "10.105.0"),
                Candidate("10.0.500", "10.0.501", "10.501.0")
            ]);

        var result = WorkloadCompatibilityAnalyzer.Analyze(request);

        Assert.Equal("10.0.105", result.Value!.Selection.SelectedWorkloadSetVersion);
        Assert.Null(result.Value.Selection.NewerWorkloadSetVersionInBand);
    }

    [Fact]
    public void Requires_explicit_prerelease_nuget_encoding()
    {
        var candidate = Candidate("10.0.100", "10.0.103-rc.1", null);
        var request = Request("10.0.103-rc.1", [candidate]);

        var result = WorkloadCompatibilityAnalyzer.Analyze(request);

        Assert.Equal(Assessment.Incompatible, result.Assessment);
        Assert.Null(result.Value!.Selection.NuGetPackageVersion);
        Assert.Contains(result.Diagnostics, x => x.Code == "workload-set.package-version-unknown");
        Assert.Equal(Assessment.Partial, result.Diagnostics.Single(x => x.Code == "workload-set.package-version-unknown").Assessment);
    }

    [Fact]
    public void Accepts_explicit_prerelease_package_mapping_even_when_sdk_preview_discovery_is_disabled()
    {
        var candidate = Candidate("10.0.100", "10.0.103-rc.1", "10.103.0-rc.1");
        var request = Request("10.0.103-rc.1", [candidate], allowPrerelease: false);

        var result = WorkloadCompatibilityAnalyzer.Analyze(request);

        Assert.Equal("10.103.0-rc.1", result.Value!.Selection.NuGetPackageVersion);
    }

    [Fact]
    public void Does_not_call_stable_set_newer_than_selected_prerelease()
    {
        var request = Request(
            "10.0.103-rc.1",
            [
                Candidate("10.0.100", "10.0.102", "10.102.0"),
                Candidate("10.0.100", "10.0.103-rc.1", "10.103.0-rc.1")
            ]);

        var result = WorkloadCompatibilityAnalyzer.Analyze(request);

        Assert.Equal("10.0.103-rc.1", result.Value!.Selection.SelectedWorkloadSetVersion);
        Assert.Null(result.Value.Selection.NewerWorkloadSetVersionInBand);
    }

    [Fact]
    public void Rejects_noncanonical_bands_and_invalid_manifest_identities()
    {
        var candidate = Candidate("10.0.101", "10.0.102", "10.102.0");
        var invalidBand = WorkloadCompatibilityAnalyzer.Analyze(Request("10.0.102", [candidate]));
        var invalidInventory = InstalledInventoryReader.Parse(
            Fixture("installed-workloads.json").Replace("\"sdkBand\": \"9.0.100\"", "\"sdkBand\": \"9.0.101\"", StringComparison.Ordinal),
            EvidenceFor("invalid-manifest-band.json"));
        var invalidMappingCandidate = Candidate("10.0.100", "10.0.102", "10.102.0") with
        {
            WorkloadSetManifest = Candidate("10.0.100", "10.0.102", "10.102.0").WorkloadSetManifest with
            {
                Json = Fixture("workloadset.json").Replace(
                    "35.0.50/9.0.100",
                    "bogus/9.0.100",
                    StringComparison.Ordinal)
            }
        };
        var invalidMapping = WorkloadCompatibilityAnalyzer.Analyze(Request("10.0.102", [invalidMappingCandidate]));
        var invalidDependencyIdentity = WorkloadCompatibilityAnalyzer.Analyze(Request(
            "10.0.102",
            [Candidate("10.0.100", "10.0.102", "10.102.0")]) with
        {
            DependencyDocumentsByWorkload = new Dictionary<string, VersionedJsonDocument>
            {
                ["microsoft.net.sdk.android"] = new(
                    Fixture("android-dependencies.json"),
                    EvidenceFor("invalid-dependency-band.json"),
                    "9.0.101",
                    "35.0.50")
            }
        });

        Assert.Contains(invalidBand.Diagnostics, x => x.Code == "workload-set.band-invalid");
        Assert.Null(invalidInventory.Value);
        Assert.Contains(invalidMapping.Diagnostics, x => x.Code == "workload-set.manifest-mapping-unknown");
        Assert.Contains(invalidDependencyIdentity.Diagnostics, x => x.Code == "workload-dependencies.version-mismatch");
    }

    [Fact]
    public void Stale_evidence_downgrades_identical_analysis_to_unknown()
    {
        var currentRequest = Request("10.0.102", [Candidate("10.0.100", "10.0.102", "10.102.0")]);
        var staleRequest = currentRequest with
        {
            Freshness = new(RetrievedAt.AddDays(2), TimeSpan.FromDays(1))
        };

        var current = WorkloadCompatibilityAnalyzer.Analyze(currentRequest);
        var stale = WorkloadCompatibilityAnalyzer.Analyze(staleRequest);

        Assert.Equal(Assessment.Compatible, current.Assessment);
        Assert.NotNull(current.Value);
        Assert.Equal(Assessment.Unknown, stale.Assessment);
        Assert.Null(stale.Value);
        Assert.Contains(stale.Diagnostics, x => x.Code == "evidence.expired");
    }

    [Fact]
    public void Reports_missing_or_malformed_metadata_without_success_shaped_defaults()
    {
        var malformedGlobal = GlobalJsonReader.Parse("{", EvidenceFor("bad-global.json"));
        var invalidGlobalProperty = GlobalJsonReader.Parse(
            "{\"sdk\":{\"workloadVersion\":42}}",
            EvidenceFor("invalid-global-property.json"));
        var malformedInventory = InstalledInventoryReader.Parse("{\"mode\":\"automatic\"}", EvidenceFor("bad-inventory.json"));
        var missingPin = WorkloadCompatibilityAnalyzer.Analyze(Request(
            "10.0.999",
            [Candidate("10.0.100", "10.0.102", "10.102.0")]));

        Assert.Equal(Assessment.Unknown, malformedGlobal.Assessment);
        Assert.Null(malformedGlobal.Value);
        Assert.Equal(Assessment.Unknown, invalidGlobalProperty.Assessment);
        Assert.Null(invalidGlobalProperty.Value);
        Assert.Equal(Assessment.Unknown, malformedInventory.Assessment);
        Assert.Null(malformedInventory.Value);
        Assert.Equal(Assessment.Unknown, missingPin.Assessment);
        Assert.Null(missingPin.Value);
    }

    [Fact]
    public void Reports_malformed_workload_set_root_as_unknown()
    {
        var candidate = Candidate("10.0.100", "10.0.102", "10.102.0");
        candidate = candidate with
        {
            WorkloadSetManifest = candidate.WorkloadSetManifest with { Json = "[]" }
        };

        var result = WorkloadCompatibilityAnalyzer.Analyze(Request("10.0.102", [candidate]));

        Assert.Equal(Assessment.Unknown, result.Assessment);
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, x => x.Code == "workload-set.root");
    }

    [Fact]
    public void Reports_missing_dependencies_as_partial_and_version_mismatch_explicitly()
    {
        var request = Request("10.0.102", [Candidate("10.0.100", "10.0.102", "10.102.0")])
            with { DependencyDocumentsByWorkload = new Dictionary<string, VersionedJsonDocument>() };
        var missing = WorkloadCompatibilityAnalyzer.Analyze(request);

        var mismatchedRequest = Request("10.0.102", [Candidate("10.0.100", "10.0.102", "10.102.0")])
            with
            {
                DependencyDocumentsByWorkload = new Dictionary<string, VersionedJsonDocument>
                {
                    ["microsoft.net.sdk.android"] = new(
                        Fixture("android-dependencies.json"),
                        EvidenceFor("android-dependencies.json"),
                        "9.0.100",
                        "35.0.51")
                }
            };
        var mismatched = WorkloadCompatibilityAnalyzer.Analyze(mismatchedRequest);

        Assert.Equal(Assessment.Partial, missing.Assessment);
        Assert.Contains(missing.Diagnostics, x => x.Code == "workload-dependencies.missing");
        Assert.Contains(mismatched.Diagnostics, x => x.Code == "workload-dependencies.version-mismatch");
    }

    [Fact]
    public void Compares_xcode_and_jdk_ranges_with_inclusive_and_exclusive_bounds()
    {
        var analysis = AnalyzeIosAndAndroid();
        var compatibleInventory = Inventory(jdk: "21.0.8", xcode: "26.2", macOs: "26.7");
        var compatible = HostCompatibilityAnalyzer.Analyze(new(
            analysis,
            compatibleInventory,
            [],
            new("/Applications/Xcode_26.2.app/Contents/Developer", "/private/android", new Dictionary<string, string>()),
            Freshness));

        var incompatibleInventory = Inventory(jdk: "22.0", xcode: "26.1", macOs: "26.7");
        var incompatible = HostCompatibilityAnalyzer.Analyze(new(
            analysis,
            incompatibleInventory,
            [],
            new(null, null, new Dictionary<string, string>()),
            Freshness));

        Assert.Contains(compatible.Value!.Checks, x => x.Tool == "JDK" && x.Assessment == Assessment.Compatible);
        Assert.Contains(compatible.Value.Checks, x => x.Tool == "Xcode" && x.Assessment == Assessment.Compatible);
        Assert.Contains(compatible.Value.Checks, x => x.Tool == "Platform SDK" && x.Required == "26.2" && x.Assessment == Assessment.Compatible);
        Assert.Contains(incompatible.Value!.Checks, x => x.Tool == "JDK" && x.Assessment == Assessment.Incompatible);
        Assert.Contains(incompatible.Value.Checks, x => x.Tool == "Xcode" && x.Assessment == Assessment.Incompatible);
        Assert.Contains(incompatible.Value.Checks, x => x.Tool == "macOS" && x.Assessment == Assessment.Unvalidated);

        var incompatibleMacOs = HostCompatibilityAnalyzer.Analyze(new(
            analysis,
            Inventory(),
            [],
            new(null, null, new Dictionary<string, string>()),
            Freshness,
            new Dictionary<string, VersionRequirement>
            {
                ["microsoft.net.sdk.ios"] = new("[15.0,16.0)", null)
            }));
        Assert.Contains(incompatibleMacOs.Value!.Checks, x => x.Tool == "macOS" && x.Assessment == Assessment.Incompatible);
        Assert.Equal(Assessment.Blocked, incompatibleMacOs.Assessment);
    }

    [Fact]
    public void Keeps_android_minimum_os_compile_api_target_api_build_tools_and_installed_platforms_distinct()
    {
        var tuple = new WorkloadTupleContext(
            ["10.0.10"],
            ["net10.0-android"],
            [new("net10.0-android", "23", "35", "35", "35.0.0")]);
        var request = Request("10.0.102", [Candidate("10.0.100", "10.0.102", "10.102.0")])
            with { TupleContext = tuple };
        var analysis = WorkloadCompatibilityAnalyzer.Analyze(request).Value!;
        var host = Inventory(android: new(
            new HashSet<string>(["35"], StringComparer.Ordinal),
            new HashSet<string>(["35.0.0"], StringComparer.Ordinal),
            new HashSet<string>(["platform-tools", "platforms;android-35", "build-tools;35.0.0"], StringComparer.Ordinal)));
        var result = HostCompatibilityAnalyzer.Analyze(new(analysis, host, [], new(null, "/private/android", new Dictionary<string, string>()), Freshness));
        var checks = result.Value!.Checks;

        Assert.Equal("23", analysis.Selection.TupleContext.AndroidTargets![0].MinimumSupportedOsApiLevel);
        Assert.Contains(checks, x => x.Tool == "Android compile API" && x.Required == "35" && x.Assessment == Assessment.Compatible);
        Assert.Contains(checks, x => x.Tool == "Android build tools" && x.Required == "35.0.0" && x.Assessment == Assessment.Compatible);
        Assert.DoesNotContain(checks, x => x.Required == "23");
    }

    [Fact]
    public void Checks_project_android_compile_and_build_tools_separately_from_workload_requirements()
    {
        var tuple = new WorkloadTupleContext(
            ["10.0.10"],
            ["net10.0-android"],
            [new("net10.0-android", "23", "36", "35", "36.0.0")]);
        var analysis = WorkloadCompatibilityAnalyzer.Analyze(Request(
            "10.0.102",
            [Candidate("10.0.100", "10.0.102", "10.102.0")]) with { TupleContext = tuple }).Value!;
        var inventory = Inventory(android: new(
            new HashSet<string>(["35"], StringComparer.Ordinal),
            new HashSet<string>(["35.0.0"], StringComparer.Ordinal),
            new HashSet<string>(["platforms;android-35"], StringComparer.Ordinal)));

        var result = HostCompatibilityAnalyzer.Analyze(new(analysis, inventory, [], new(null, null, new Dictionary<string, string>()), Freshness));

        Assert.Contains(result.Value!.Checks, x => x.Tool == "Android compile API" && x.Required == "35" && x.Assessment == Assessment.Compatible);
        Assert.Contains(result.Value.Checks, x => x.Tool == "Android project compile API" && x.Required == "36" && x.Assessment == Assessment.Incompatible);
        Assert.Contains(result.Value.Checks, x => x.Tool == "Android project build tools" && x.Required == "36.0.0" && x.Assessment == Assessment.Incompatible);
        Assert.Equal("23", analysis.Selection.TupleContext.AndroidTargets![0].MinimumSupportedOsApiLevel);
    }

    [Fact]
    public void Missing_android_inventory_is_unknown_and_unavailable_os_targets_are_blocked()
    {
        var analysis = AnalyzeIosAndAndroid();
        var result = HostCompatibilityAnalyzer.Analyze(new(
            analysis,
            null,
            [new("ios", false, "No macOS/Xcode host is available.")],
            new(null, null, new Dictionary<string, string>()),
            Freshness));

        Assert.Contains(result.Value!.Checks, x => x.Tool == "Android compile API" && x.Assessment == Assessment.Unknown);
        Assert.Contains(result.Diagnostics, x => x.Code == "platform.target-unavailable" && x.Assessment == Assessment.Blocked);
        Assert.Equal(Assessment.Blocked, result.Assessment);
    }

    [Fact]
    public void Checks_exact_windows_sdk_requirements_and_reports_missing_windows_metadata()
    {
        var windowsRequirement = new VersionRequirement(
            "[10.0.20348,)",
            "10.0.20348.0",
            EvidenceFor("windows-sdk-requirement.json"));
        var windowsRequest = Request("10.0.102", [Candidate("10.0.100", "10.0.102", "10.102.0")]) with
        {
            TupleContext = new(
                ["10.0.10"],
                ["net10.0-windows"],
                WindowsSdkRequirementsByWorkload: new Dictionary<string, VersionRequirement>
                {
                    ["windows-target"] = windowsRequirement
                })
        };
        var analysis = WorkloadCompatibilityAnalyzer.Analyze(windowsRequest).Value!;
        var host = Inventory() with
        {
            WindowsSdkVersions = new Dictionary<string, string> { ["windows-target"] = "10.0.20348" }
        };
        var compatible = HostCompatibilityAnalyzer.Analyze(new(analysis, host, [], new(null, null, new Dictionary<string, string>()), Freshness));
        var missingMetadata = WorkloadCompatibilityAnalyzer.Analyze(Request(
            "10.0.102",
            [Candidate("10.0.100", "10.0.102", "10.102.0")]) with
        {
            TupleContext = new(["10.0.10"], ["net10.0-windows"])
        });

        Assert.Contains(compatible.Value!.Checks, x => x.Tool == "Windows SDK" && x.Assessment == Assessment.Compatible);
        Assert.Contains(compatible.Evidence, x => x.Source == "fixture:windows-sdk-requirement.json");
        Assert.Contains(missingMetadata.Diagnostics, x => x.Code == "windows-sdk.requirements-missing");
        Assert.Equal(Assessment.Partial, missingMetadata.Assessment);
    }

    [Fact]
    public void Keeps_process_local_suggestions_as_data_and_never_executes_them()
    {
        var analysis = AnalyzeIosAndAndroid();
        var suggestions = new ProcessLocalSuggestions(
            "/Applications/Selected Xcode.app/Contents/Developer",
            "/private/android-sdk",
            new Dictionary<string, string> { ["AndroidSdkDirectory"] = "/private/android-sdk" });
        var result = HostCompatibilityAnalyzer.Analyze(new(analysis, Inventory(), [], suggestions, Freshness));

        Assert.Same(suggestions, result.Value!.Suggestions);
        Assert.Equal("/Applications/Selected Xcode.app/Contents/Developer", result.Value.Suggestions.DeveloperDirectory);
    }

    [Fact]
    public void Stale_mac_os_requirement_evidence_downgrades_host_analysis()
    {
        var oldRequirementEvidence = EvidenceFor("macos-requirement.json") with
        {
            RetrievedAt = RetrievedAt.AddDays(-2)
        };
        var request = new HostCompatibilityRequest(
            AnalyzeIosAndAndroid(),
            Inventory(),
            [],
            new(null, null, new Dictionary<string, string>()),
            Freshness,
            new Dictionary<string, VersionRequirement>
            {
                ["microsoft.net.sdk.ios"] = new("[15.0,16.0)", null, oldRequirementEvidence)
            });

        var result = HostCompatibilityAnalyzer.Analyze(request);

        Assert.Equal(Assessment.Unknown, result.Assessment);
        Assert.Null(result.Value);
        Assert.Contains(result.Evidence, x => x.Source == "fixture:macos-requirement.json");
        Assert.Contains(result.Diagnostics, x => x.Code == "evidence.expired");
    }

    [Fact]
    public void Identifies_component_security_servicing_and_deployment_specific_actions()
    {
        var request = RuntimeRequest(RuntimeDeploymentKind.FrameworkDependent);
        var result = RuntimeServicingAnalyzer.Analyze(request);

        Assert.Equal(3, result.Value!.LocalFrameworks.Count);
        Assert.Empty(result.Value.DeploymentFrameworks);
        Assert.All(result.Value.LocalFrameworks, finding =>
        {
            Assert.True(finding.SecurityReleaseAvailable);
            Assert.Equal("10.0.3", finding.SecurityServicingVersion);
            Assert.Equal("update-local-framework-dependent-runtime", finding.Action);
        });
        Assert.Equal(3, result.Value.SecurityReleases.Count);
        Assert.Contains(result.Value.SecurityReleases, x => x.Framework == "Microsoft.AspNetCore.App");
        Assert.Contains(result.Value.SecurityReleases, x => x.Framework == "Microsoft.WindowsDesktop.App");
        Assert.Contains(result.Evidence, x => x.Source == "fixture:releases.json");
        Assert.Contains(result.Evidence, x => x.Source == "fixture:runtime-inventory.json");
        Assert.Contains(result.Evidence, x => x.Source == "fixture:deployment.json");
    }

    [Fact]
    public void Does_not_claim_old_runtime_is_current_when_metadata_has_no_same_band_servicing()
    {
        var request = RuntimeRequest(RuntimeDeploymentKind.FrameworkDependent) with
        {
            InstalledFrameworks = [new("Microsoft.NETCore.App", "8.0.1")]
        };

        var result = RuntimeServicingAnalyzer.Analyze(request);

        Assert.Equal(Assessment.Unknown, result.Assessment);
        Assert.Equal(Assessment.Unknown, result.Value!.LocalFrameworks.Single().Assessment);
        Assert.Contains("support status", result.Value.LocalFrameworks.Single().Detail);
    }

    [Fact]
    public void Distinguishes_self_contained_republish_container_drift_and_unknown_deployment()
    {
        var selfContained = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(RuntimeDeploymentKind.SelfContained));
        var container = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(
            RuntimeDeploymentKind.Container,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.NETCore.App"] = "10.0.1",
                ["Microsoft.AspNetCore.App"] = "10.0.1"
            },
            image: "mcr.microsoft.com/dotnet/aspnet:10.0"));
        var currentContainer = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(
            RuntimeDeploymentKind.Container,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.NETCore.App"] = "10.0.3",
                ["Microsoft.AspNetCore.App"] = "10.0.3"
            },
            image: "mcr.microsoft.com/dotnet/aspnet:10.0"));
        var unknown = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(RuntimeDeploymentKind.Unknown));
        var selfContainedWithEvidence = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(
            RuntimeDeploymentKind.SelfContained,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.NETCore.App"] = "10.0.1"
            }));

        Assert.Empty(selfContained.Value!.DeploymentFrameworks);
        Assert.Contains(selfContained.Diagnostics, x => x.Code == "runtime.embedded-runtime-evidence-missing");
        Assert.All(container.Value!.DeploymentFrameworks, x => Assert.Equal("update-container-runtime-image-and-redeploy", x.Action));
        Assert.All(currentContainer.Value!.DeploymentFrameworks, x => Assert.Null(x.Action));
        Assert.Equal(Assessment.Partial, currentContainer.Assessment);
        Assert.All(currentContainer.Value.LocalFrameworks, x => Assert.Equal(Assessment.Partial, x.Assessment));
        Assert.All(currentContainer.Value.DeploymentFrameworks, x => Assert.Equal(Assessment.Compatible, x.Assessment));
        Assert.Single(selfContainedWithEvidence.Value!.DeploymentFrameworks);
        Assert.Equal("republish-self-contained-application", selfContainedWithEvidence.Value.DeploymentFrameworks.Single().Action);
        Assert.All(unknown.Value!.LocalFrameworks, x => Assert.Equal(Assessment.Partial, x.Assessment));
        Assert.Contains(unknown.Diagnostics, x => x.Code == "runtime.deployment-unknown");
    }

    [Fact]
    public void Does_not_infer_cve_impact_or_deployment_remediation_from_security_metadata()
    {
        var result = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(RuntimeDeploymentKind.FrameworkDependent));

        Assert.All(result.Value!.LocalFrameworks, x => Assert.Contains("security", x.Detail!, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Value.LocalFrameworks, x => x.Detail!.Contains("CVE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Container_components_are_not_inferred_from_local_framework_inventory()
    {
        var request = RuntimeRequest(
            RuntimeDeploymentKind.Container,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.AspNetCore.App"] = "10.0.1"
            }) with
        {
            InstalledFrameworks = [new("Microsoft.NETCore.App", "10.0.3")]
        };

        var result = RuntimeServicingAnalyzer.Analyze(request);

        Assert.Single(result.Value!.DeploymentFrameworks);
        Assert.Equal("Microsoft.AspNetCore.App", result.Value.DeploymentFrameworks.Single().Framework);
        Assert.Equal(Assessment.Partial, result.Value.DeploymentFrameworks.Single().Assessment);
        Assert.DoesNotContain(result.Value.DeploymentFrameworks, x => x.Framework == "Microsoft.WindowsDesktop.App");
    }

    [Fact]
    public void Older_container_component_is_not_hidden_by_current_local_runtime()
    {
        var request = RuntimeRequest(
            RuntimeDeploymentKind.Container,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.AspNetCore.App"] = "10.0.1"
            }) with
        {
            InstalledFrameworks =
            [
                new("Microsoft.NETCore.App", "10.0.3"),
                new("Microsoft.AspNetCore.App", "10.0.3")
            ]
        };

        var result = RuntimeServicingAnalyzer.Analyze(request);

        Assert.Equal(Assessment.Partial, result.Assessment);
        Assert.All(result.Value!.LocalFrameworks, x => Assert.Equal(Assessment.Compatible, x.Assessment));
        Assert.Single(result.Value.DeploymentFrameworks);
        Assert.Equal(Assessment.Partial, result.Value.DeploymentFrameworks.Single().Assessment);
    }

    [Fact]
    public void Stale_runtime_evidence_does_not_return_compatible_findings()
    {
        var result = RuntimeServicingAnalyzer.Analyze(RuntimeRequest(RuntimeDeploymentKind.Container,
            componentVersions: new Dictionary<string, string>
            {
                ["Microsoft.NETCore.App"] = "10.0.3"
            }) with
        {
            Freshness = new(RetrievedAt.AddDays(2), TimeSpan.FromDays(1))
        });

        Assert.Equal(Assessment.Unknown, result.Assessment);
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, x => x.Code == "evidence.expired");
    }

    [Fact]
    public void Safely_reads_exact_archive_entries_without_extracting_or_changing_input()
    {
        using var stream = CreateNupkg("data/microsoft.net.workloads.workloadset.json", Fixture("workloadset.json"));
        var before = SHA256.HashData(stream.ToArray());
        stream.Position = 0;

        var json = SafeMetadataReader.ReadArchiveEntry(stream, "data/microsoft.net.workloads.workloadset.json");

        Assert.Contains("microsoft.net.sdk.android", json);
        Assert.Equal(before, SHA256.HashData(stream.ToArray()));
        Assert.Throws<ArgumentException>(() => SafeMetadataReader.ReadArchiveEntry(
            stream,
            "../data/WorkloadDependencies.json"));
        Assert.Throws<InvalidDataException>(() => SafeMetadataReader.ReadArchiveEntry(
            stream,
            "data/microsoft.net.workloads.workloadset.json",
            maximumBytes: 4));
    }

    [Fact]
    public void Reads_metadata_file_without_modifying_it()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        const string relativePath = "global.json";
        var path = Path.Combine(root, relativePath);
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var json = SafeMetadataReader.ReadFile(root, relativePath);

        Assert.Contains("\"workloadVersion\": \"10.0.102\"", json);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Throws<InvalidDataException>(() => SafeMetadataReader.ReadFile(root, relativePath, maximumBytes: 4));
    }

    [Fact]
    public void Restricts_file_reads_to_approved_root_and_rejects_symlink_escapes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"toolchains-{Guid.NewGuid():N}");
        var root = Path.Combine(directory, "approved");
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "safe.json"), "{}");
        File.WriteAllText(Path.Combine(outside, "secret.json"), "{\"secret\":true}");
        var leafLink = Path.Combine(root, "linked.json");
        var ancestorLink = Path.Combine(root, "linked-dir");
        File.CreateSymbolicLink(leafLink, Path.Combine(outside, "secret.json"));
        Directory.CreateSymbolicLink(ancestorLink, outside);
        try
        {
            Assert.Equal("{}", SafeMetadataReader.ReadFile(root, "safe.json"));
            Assert.Throws<UnauthorizedAccessException>(() => SafeMetadataReader.ReadFile(root, "../outside/secret.json"));
            Assert.Throws<ArgumentException>(() => SafeMetadataReader.ReadFile(root, Path.Combine(outside, "secret.json")));
            Assert.Throws<UnauthorizedAccessException>(() => SafeMetadataReader.ReadFile(root, "linked.json"));
            Assert.Throws<UnauthorizedAccessException>(() => SafeMetadataReader.ReadFile(root, "linked-dir/secret.json"));

            var swapTarget = Path.Combine(root, "swap.json");
            var savedPath = Path.Combine(root, "swap-original.json");
            File.WriteAllText(swapTarget, "{}");
            Assert.Throws<UnauthorizedAccessException>(() => SafeMetadataReader.ReadFileCore(
                root,
                "swap.json",
                SafeMetadataReader.DefaultMaximumBytes,
                () =>
                {
                    File.Move(swapTarget, savedPath);
                    File.CreateSymbolicLink(swapTarget, Path.Combine(outside, "secret.json"));
                }));

            var swapDirectory = Path.Combine(root, "swap-dir");
            var savedDirectory = Path.Combine(root, "swap-dir-original");
            Directory.CreateDirectory(swapDirectory);
            File.WriteAllText(Path.Combine(swapDirectory, "data.json"), "{}");
            Assert.Throws<UnauthorizedAccessException>(() => SafeMetadataReader.ReadFileCore(
                root,
                "swap-dir/data.json",
                SafeMetadataReader.DefaultMaximumBytes,
                () =>
                {
                    Directory.Move(swapDirectory, savedDirectory);
                    Directory.CreateSymbolicLink(swapDirectory, outside);
                }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WorkloadAnalysis AnalyzeIosAndAndroid() =>
        WorkloadCompatibilityAnalyzer.Analyze(Request(
            "10.0.102",
            [Candidate("10.0.100", "10.0.102", "10.102.0")],
            workloads: new HashSet<string>(
                ["microsoft.net.sdk.android", "microsoft.net.sdk.ios"],
                StringComparer.Ordinal))).Value!;

    private static WorkloadAnalysisRequest Request(
        string? pinned,
        IReadOnlyList<WorkloadSetCandidate> candidates,
        bool allowPrerelease = true,
        IReadOnlySet<string>? workloads = null)
    {
        var dependencies = new Dictionary<string, VersionedJsonDocument>(StringComparer.Ordinal)
        {
            ["microsoft.net.sdk.android"] = new(
                Fixture("android-dependencies.json"),
                EvidenceFor("android-dependencies.json"),
                "9.0.100",
                "35.0.50"),
            ["microsoft.net.sdk.ios"] = new(
                Fixture("ios-dependencies.json"),
                EvidenceFor("ios-dependencies.json"),
                "10.0.100",
                "26.2.10191")
        };
        return new(
            "10.0.101",
            pinned,
            allowPrerelease,
            candidates,
            workloads ?? new HashSet<string>(["microsoft.net.sdk.android"], StringComparer.Ordinal),
            dependencies,
            new(["10.0.10"], ["net10.0-android", "net10.0-ios"]),
            InstalledInventoryReader.Parse(Fixture("installed-workloads.json"), EvidenceFor("installed-workloads.json")).Value,
            Freshness);
    }

    private static WorkloadSetCandidate Candidate(string band, string version, string? packageVersion) =>
        new(band, version, packageVersion, new(
            Fixture("workloadset.json"),
            EvidenceFor($"workloadset-{band}-{version}.json")));

    private static HostToolInventory Inventory(
        string jdk = "21.0.8",
        string xcode = "26.2",
        string macOs = "26.7",
        ObservedAndroidInventory? android = null) =>
        new(
            macOs,
            jdk,
            xcode,
            android ?? new(
                new HashSet<string>(["35"], StringComparer.Ordinal),
                new HashSet<string>(["35.0.0"], StringComparer.Ordinal),
                new HashSet<string>(["build-tools;35.0.0", "platform-tools", "platforms;android-35"], StringComparer.Ordinal)),
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["microsoft.net.sdk.ios"] = "26.2" },
            EvidenceFor("host-inventory.json"));

    private static RuntimeServicingRequest RuntimeRequest(
        RuntimeDeploymentKind deploymentKind,
        IReadOnlyDictionary<string, string>? componentVersions = null,
        string? image = null) =>
        new(
            new(Fixture("releases.json"), EvidenceFor("releases.json", "10.0")),
            [
                new("Microsoft.NETCore.App", "10.0.1", "/dotnet/shared/Microsoft.NETCore.App/10.0.1"),
                new("Microsoft.AspNetCore.App", "10.0.2", "/dotnet/shared/Microsoft.AspNetCore.App/10.0.2"),
                new("Microsoft.WindowsDesktop.App", "10.0.1", "/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.1")
            ],
            new(
                deploymentKind,
                image,
                DeclaredContainerRuntimeVersions: componentVersions,
                EmbeddedRuntimeVersions: componentVersions,
                Evidence: EvidenceFor("deployment.json")),
            Freshness,
            EvidenceFor("runtime-inventory.json"));

    private static FreshnessCriteria Freshness => new(RetrievedAt, TimeSpan.FromDays(1));

    private static MemoryStream CreateNupkg(string entryName, string contents)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open()))
        {
            writer.Write(contents);
        }

        stream.Position = 0;
        return stream;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Evidence EvidenceFor(string source, string? revision = null) =>
        new($"fixture:{source}", RetrievedAt, revision);
}

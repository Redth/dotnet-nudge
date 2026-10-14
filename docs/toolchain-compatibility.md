# Toolchain compatibility analysis

`DotnetNudge.Toolchains` is a passive, offline analysis library. It consumes
versioned metadata and observed inventories supplied by its caller; it does not
fetch metadata, invoke `dotnet`, run a build, or mutate SDK, workload, Xcode,
JDK, Android, Windows, or project state. The API is independent of the unfinished
Core project so both projects can be built and tested directly.

## Public API

- `GlobalJsonReader.Parse` reads `sdk.version`, `sdk.workloadVersion`,
  `sdk.allowPrerelease`, and `sdk.rollForward` from supplied JSON. It returns
  parse evidence and diagnostics; it does not rewrite the file.
- `SdkVersioning.TryGetFeatureBand` parses stable and prerelease SDK versions
  and computes the exact `x.y.zzz` feature band.
- `InstalledInventoryReader.Parse` accepts an explicit read-only inventory
  document containing `mode`, optional `workloadSetVersion`, `manifests`, and
  `workloads`. Manifest identities include a validated manifest version and
  canonical SDK feature band. It does not execute workload commands or inspect
  an inferred machine-wide location.
- `WorkloadCompatibilityAnalyzer.Analyze` accepts exact workload-set candidates,
  selected workload IDs, the selected set's `WorkloadDependencies.json`
  documents, project tuple context, and optional installed inventory. A pin is
  resolved only to metadata for the effective SDK band. Without a pin, the
  newest eligible candidate is selected only from that band; the newest
  candidate in a different band is never a substitute.
  SDK feature-band identities must be canonical (for example, `9.0.100`, not
  `9.0.101`), and manifest versions are parsed before they are accepted from set
  mappings, dependency document identities, or installed inventory.
- `HostCompatibilityAnalyzer.Analyze` compares Xcode/JDK ranges and exact
  Android workload and project compile API/build-tools requirements against a
  supplied inventory. Project target API and minimum supported OS remain
  separate project facts. Its `ProcessLocalSuggestions` are data only; the
  library never applies them.
- `RuntimeServicingAnalyzer.Analyze` parses the official channel `releases`
  collection and compares the three shared-framework families independently.
  Local shared-runtime findings are distinct from deployment-component
  findings. Container runtime versions and self-contained embedded runtime
  versions must be supplied per framework; an image tag or workstation
  inventory is not evidence of image contents. Framework-dependent production
  runtime inventory remains unknown unless the caller supplies it.
- Every analysis request supplies `FreshnessCriteria(AsOf, MaximumAge)`.
  Evidence older than that caller-selected limit, future-dated evidence, or an
  invalid negative age makes the overall result unknown and suppresses its
  value. The library imposes no global freshness duration.
- `SafeMetadataReader.ReadFile(approvedRoot, relativePath)` reads a bounded file
  only when the normalized path stays within the approved root. Unix reads use
  no-follow, handle-relative opens for every root/path component; Windows reads
  verify the opened handle's resolved path before reading. Unsupported
  platforms fail explicitly. `ReadArchiveEntry` reads one exact, normalized
  archive entry in read mode; archive paths are not extracted.

All results preserve source timestamps/revisions through `Evidence` and use
`Assessment` plus diagnostics to distinguish compatible, incompatible,
unknown, partial, blocked, and unvalidated evidence. Callers should propagate
those states rather than convert missing metadata or inventory into success.

## Version and platform semantics

SDK versions, user-facing workload-set versions, NuGet package versions, and
manifest versions are separate values. Stable workload set values in the
supported `major.0.patch` form map to NuGet `major.patch.0` only after parsing
and validating that shape. Prerelease/RC workload-set package encodings must be
provided explicitly; otherwise the result has a coverage diagnostic and no
package-version placeholder. A pinned set remains selected even when a newer
same-band or globally newer set is supplied; a newer same-band version is
reported separately.

The accepted workload-set shape is the exact NuGet payload object mapping
workload IDs to `manifestVersion/sdkBand`. Each dependency document must be
provided with its exact manifest version and SDK band and contain the selected
workload's entry. Supported requirement entries are `jdk.version` and
`recommendedVersion`, `xcode.version` and `recommendedVersion`, and
`androidsdk.apiLevel`, `buildToolsVersion`, and `packages`; a sibling `sdk.version`
is retained as an exact platform SDK requirement. Version ranges use NuGet's
`VersionRange` parser, including inclusive and exclusive bounds.
Absent, malformed, mismatched, or unrecognized metadata is reported as partial
or unknown; unsupported shapes are not guessed.
The metadata shape follows the discovery approach in the [official MAUI doctor
workload dependency reference](https://github.com/dotnet/skills/blob/main/plugins/dotnet-maui/skills/dotnet-maui-doctor/references/workload-dependencies-discovery.md);
this library does not inherit that skill's network or installation behavior.

`WorkloadTupleContext` carries caller-observed MAUI package versions, target
frameworks, optional Android project settings, and exact Windows SDK
requirements supplied with their evidence. `WindowsSdkVersions` in the host
inventory is keyed by the same requirement key. Since no Windows dependency
shape is currently supported from `WorkloadDependencies.json`, a Windows TFM
without explicit Windows SDK requirement metadata gets a coverage diagnostic.
Android minimum supported OS, project compile API, project target API,
build-tools version, workload compile API, and installed Android platforms
remain separate facts. The
analyzer never raises a project's minimum supported OS. It does not infer an
Xcode/macOS minimum from the Xcode version alone; callers may supply exact
versioned macOS requirements, and otherwise the macOS dimension remains
unknown or unvalidated. Unavailable targets are blocked; available targets are
still unvalidated because this library does not build or execute applications.

Runtime security release flags mean only that the release metadata marks that
release as security servicing. They do not establish that a particular CVE
affects a particular application. SDK servicing detection remains Core's
responsibility. Local framework findings may suggest servicing that local
shared runtime, but do not prove deployment remediation. Self-contained
deployment comparisons require explicit embedded-runtime versions and lead to
republish suggestions. Container comparisons use only per-framework declared
runtime component versions, not local shared frameworks or the image tag.
Framework-dependent production runtime versions and unobserved self-contained
contents are explicitly unknown.

## Not implemented here

This PR implements analysis only. Metadata acquisition/caching, SDK servicing
findings, project discovery, trial creation/build/test validation, package
installation, license acceptance, host tool installation, Xcode selection,
global environment changes, and user-facing Core/CLI integration belong to
other modules. No workload update/advertising command, network request, system
installer, or hidden metadata-side effect is part of this library.

Fixtures use official-format global.json, workload-set, workload-dependency,
installed-inventory, and release metadata. The independent
`.github/workflows/toolchains-ci.yml` workflow builds and tests these two
projects without a solution file or dependency on Core.

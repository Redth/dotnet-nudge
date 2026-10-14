# Package intelligence library

`DotnetNudge.Packages` is an independent .NET 10 library. It does not reference the
Core/CLI layer and does not perform restore, build, package installation, or
model calls. Build/test directly:

```sh
dotnet test tests/DotnetNudge.Packages.Tests/DotnetNudge.Packages.Tests.csproj
```

## Integration surface

- `PackageGraphReader.Read(assetsPath, declaredInputs?)` returns scoped
  `PackageGraph`/`PackageNode` records from an existing, settled assets file.
  Optional trusted declaration paths make newer-than-assets inputs explicitly
  unknown. Only v3/v4 lock-file schemas are accepted; package folders come
  from assets, not a global-cache assumption. Nodes carry requested range,
  centrally managed status and package content hash where the lock file
  provides them. `Source` is null when assets have no per-package provenance.
- `GraphTransitions(stateFile?).Observe(graph)` creates a baseline, then
  sequenced add/remove/upgrade/downgrade/role events. Pass a private, per-worktree
  state path outside the repository to retain comparisons across restarts.
  Unknown graphs do not replace a baseline. Pass `GraphObservationOrigin` to
  distinguish a branch/worktree switch from an in-session edit. An I/O failure
  during atomic snapshot replacement is surfaced without advancing the in-memory
  baseline or transition sequence; callers can retry the observation.
- `PackageSourcePolicy(projectDirectory).Allowed(packageId, approvedNames?)`
  applies NuGet configuration hierarchy, disabled sources, and official package
  source mapping. Without mapping, the caller must supply explicit approved
  source names; no implicit broadcast to all configured feeds occurs.
- `PackageIntelligence.AssessAsync(node, graph, approvedSourceNames?, includePrerelease?, token?)`
  returns advisories, update candidates and transitive dependency paths. It
  reads the configured `auditSources` (or permitted package sources if none are
  configured) through NuGet's partitioned VulnerabilityInfo resource. An
  expired cache is **replaced**, not appended; incomplete partitions are never
  cached as clean. Candidate lists and dependency groups use metadata-only
  registration reads from permitted sources. `MinimalKnownFix` is only produced
  with complete feed evidence. `Compatibility=Partial` means static metadata
  fits; it is **not** a verified restore/build/runtime result. Missing or
  incompatible candidate framework evidence is `Unknown` and cannot count as
  a known fix. Parent upgrade
  options require evidence that their new dependency minimum reaches the
  fixed version. `RemediationPath.Coverage` distinguishes checked parent
  metadata from missing/unpermitted sources or indirect paths; such gaps make
  the overall assessment partial. No transitive
  direct reference/pin is added. Assessments expose a project/TFM/package
  `SubjectKey`, canonical revision covering advisory severity, fix, candidates
  and parent remediation, plus retrieval time for Core mapping;
  mutation always requires separate consent.
- `PackageGuidanceReader.ReadRestored(node)` and
  `.ReadArchive(explicitNupkgPath, node)` validate exact manifest identity and
  return versioned skill handles, hashes, bounded supporting documents, and
  declared README bytes. Content is inert third-party text; no hooks, scripts,
  permissions or host-skill activation follow from reading it. Unknown/unsafe
  archives return a diagnostic instead of guidance.
- `InspectorAdapter(executable, expectedVersion).InventoryAsync(projectPath,
  assetsPath, reader)` and `.ReadProjectSkillAsync(...)` require a pinned
  installed executable, the settled assets graph and `--offline`. They parse
  JSONL rows with their originating `InspectorSkillRow.AssetsFingerprint`,
  select the **returned** row, use separate raw output, and reject omitted-text
  or changed-assets results before and after the selected read. The explicit
  `.ListPackageSkillsAsync(...)`/`.ReadPackageFileAsync(...)` network routes
  require caller-supplied acquisition consent and a mapped/approved source.
  Absence or version mismatch is an unknown result; use the local reader instead.

**Coverage boundaries:** The installed `dotnet-inspect` tool is not required
and is not acquired by the library; the fixture-executable tests verify its
argument/diagnostic contract, not a live inspector binary. NuGet credentials
are limited to what the current noninteractive NuGet client already provides;
authentication failures are partial assessments. Unsupported/unevaluated
project conditions and custom central-package imports require a restore before
resolved versions can be trusted. Candidate framework metadata cannot prove
asset/runtime compatibility; a permitted restore/build trial belongs to the
separate host orchestration layer. Feed and package text must not be promoted
to agent instructions without the host's trust and consent checks.

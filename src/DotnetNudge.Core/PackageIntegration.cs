using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using DotnetNudge.Packages;

namespace DotnetNudge.Core;

public sealed record PackageScanResult(IReadOnlyList<Finding> Findings, IReadOnlyList<Coverage> Coverage);
public sealed record PackageSkillDescriptor(string Handle, string Name, string Description,
    string Project, string Framework, string? RuntimeIdentifier, string PackageId, string Version,
    string? Source, string? ContentHash, string AssetsFingerprint);
public sealed record PackageContextReport(int SchemaVersion, string WorkspaceId, Assessment Assessment,
    IReadOnlyList<PackageSkillDescriptor> Skills, PackageSkillDescriptor? Selected,
    GuidanceDocument? Entry, IReadOnlyList<GuidanceDocument> References, string? Diagnostic);
public sealed record PackageAssessmentReport(int SchemaVersion, string WorkspaceId,
    string AssetsFingerprint, PackageAssessment Assessment);

public static class PackageIntegration
{
    const int MaxGraphs = 8;
    const int MaxAssessments = 4;
    const int MaxAuditScopes = 512;
    const int MaxGuidancePackages = 16;

    public static async Task<PackageScanResult> ScanAsync(WorkspaceInventory workspace,
        NudgeState state, CancellationToken token)
    {
        var findings = new List<Finding>();
        var coverage = new List<Coverage>();
        var reader = new PackageGraphReader();
        var assessed = 0;
        var audited = 0;
        var auditOmitted = 0;
        var omitted = 0;
        if (workspace.Projects.Count == 0)
            coverage.Add(new("package-graph", Assessment.Unknown,
                "No SDK-style projects were discovered in the admitted workspace."));
        foreach (var project in workspace.Projects.Take(MaxGraphs))
        {
            token.ThrowIfCancellationRequested();
            var (graph, projectPath) = ReadGraph(workspace, project, reader);
            var check = "package-graph:" + project;
            if (graph.Status != EvidenceStatus.Complete)
            {
                coverage.Add(new(check, Assessment.Unknown, graph.Diagnostic ?? "Restored graph unavailable."));
                continue;
            }
            var filename = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)));
            var snapshot = state.GraphSnapshot(workspace.WorkspaceId, project);
            if (snapshot is null)
            {
                var legacy = Path.Combine(state.DirectoryPath, "package-graphs",
                    workspace.WorkspaceId, filename + ".json");
                if (File.Exists(legacy))
                {
                    if (new FileInfo(legacy).Length > 32 * 1024 * 1024)
                        throw new InvalidDataException("Legacy package graph exceeds 32 MiB; preserve state.");
                    snapshot = File.ReadAllText(legacy);
                }
            }
            var history = GraphTransitions.FromSnapshot(snapshot);
            var changes = history.Observe(graph);
            state.StageGraph(workspace.WorkspaceId, project, history.Snapshot());
            coverage.Add(new(check, Assessment.Confirmed,
                $"Observed {graph.Packages.Count} resolved project/TFM/RID package entries; " +
                (changes.Count == 0 ? "no graph delta from baseline." : $"{changes.Count} graph deltas.")));
            foreach (var change in changes)
            {
                var node = change.Current ?? change.Previous!;
                var subject = Subject(workspace, node);
                var revision = Revision(subject, change.Sequence.ToString(), graph.Fingerprint);
                findings.Add(new(Contract.Version, "graph:" + subject, subject, revision,
                    "restored-graph-change", Priority.Low,
                    change.Previous?.Version.ToNormalizedString(),
                    change.Current?.Version.ToNormalizedString(), Scope(workspace, node),
                    Assessment.Confirmed,
                    [new Evidence("settled-assets", new Uri(graph.AssetsPath).AbsoluteUri, DateTimeOffset.UtcNow)],
                    "review-external-graph-change", false, "package"));
            }
            PackageSourcePolicy policy;
            try { policy = new PackageSourcePolicy(Path.GetDirectoryName(projectPath)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                coverage.Add(new("package-sources:" + project, Assessment.Blocked, ex.Message));
                continue;
            }
            var intelligence = new PackageIntelligence(policy);
            var direct = graph.Packages.Where(n => n.Direct)
                .DistinctBy(n => (n.Framework, n.RuntimeIdentifier, n.Id))
                .ToArray();
            var assessedScopes = new HashSet<(string Framework, string? Rid, string Id)>();
            var skipped = 0;
            foreach (var node in direct)
            {
                token.ThrowIfCancellationRequested();
                var feedCheck = $"package-feed:{project}:{node.Framework}:{node.RuntimeIdentifier ?? "any"}:{node.Id}";
                if (policy.Allowed(node.Id).Count == 0)
                {
                    coverage.Add(new(feedCheck, Assessment.Unknown,
                        "No mapped source; candidate metadata requires an explicitly approved source."));
                    continue;
                }
                if (assessed >= MaxAssessments)
                {
                    skipped++;
                    continue;
                }
                assessed++;
                assessedScopes.Add((node.Framework, node.RuntimeIdentifier, node.Id));
                var assessment = await intelligence.AssessAsync(node, graph, cancellationToken: token);
                coverage.Add(new(feedCheck, Map(assessment.Status),
                    assessment.Diagnostics.Count == 0
                        ? $"Checked permitted audit, deprecation and candidate metadata for {node.Framework}."
                        : string.Join("; ", assessment.Diagnostics)));
                if (assessment.Deprecations.Count > 0)
                {
                    var deprecatedSubject = Subject(workspace, node);
                    findings.Add(new(Contract.Version, "deprecation:" + deprecatedSubject,
                        deprecatedSubject + "|deprecation",
                        Revision(deprecatedSubject, assessment.EvidenceRevision, graph.Fingerprint),
                        "package-deprecation",
                        assessment.Deprecations.Any(d => d.Reasons.Contains("CriticalBugs"))
                            ? Priority.Medium : Priority.Low,
                        node.Version.ToNormalizedString(), null, Scope(workspace, node),
                        Map(assessment.Status),
                        [new Evidence("settled-assets", new Uri(graph.AssetsPath).AbsoluteUri,
                            assessment.RetrievedAt),
                            .. policy.Allowed(node.Id)
                                .Where(s => assessment.Deprecations.Any(d => d.Source == s.Name))
                                .Select(s => new Evidence("package-deprecation-source", s.Source,
                                    assessment.RetrievedAt))],
                        "review-package-deprecation-and-replacement-trial", true, "package"));
                }
                var candidate = assessment.Advisories.Count > 0
                    ? assessment.MinimalKnownFix is { } fix
                        ? assessment.Candidates.FirstOrDefault(c => c.Version == fix) : null
                    : assessment.Candidates.FirstOrDefault(c => c.Compatibility == EvidenceStatus.Partial &&
                        c.Kind is not ("major" or "preview"));
                if (candidate is null && assessment.Advisories.Count == 0) continue;
                var subject = Subject(workspace, node);
                var revision = Revision(subject, assessment.EvidenceRevision, graph.Fingerprint);
                var evidence = new List<Evidence>
                {
                    new("settled-assets", new Uri(graph.AssetsPath).AbsoluteUri, assessment.RetrievedAt)
                };
                evidence.AddRange(assessment.Advisories.Select(a =>
                    new Evidence("nuget-advisory", a.Url, assessment.RetrievedAt)));
                if (candidate is not null)
                {
                    var source = policy.Allowed(node.Id).FirstOrDefault(s => s.Name == candidate.Source);
                    if (source is not null)
                        evidence.Add(new("mapped-package-source", source.Source, assessment.RetrievedAt));
                }
                findings.Add(new(Contract.Version, "package:" + subject, subject, revision,
                    assessment.Advisories.Count == 0 ? "package-update" : "package-advisory",
                    assessment.Advisories.Count == 0 ? Priority.Low :
                    assessment.Advisories.Max(a => a.Severity) switch
                    {
                        >= 3 => Priority.Critical, 2 => Priority.High, 1 => Priority.Medium,
                        _ => Priority.Low
                    },
                    node.Version.ToNormalizedString(), candidate?.Version.ToNormalizedString(),
                    Scope(workspace, node), Map(assessment.Status), evidence,
                    candidate?.Kind == "major" ? "review-major-package-trial" :
                    "review-package-metadata-and-trial", true, "package"));
            }
            var auditNodes = graph.Packages.DistinctBy(node =>
                    (node.Framework, node.RuntimeIdentifier, node.Id))
                .Where(node => !assessedScopes.Contains((node.Framework, node.RuntimeIdentifier, node.Id)))
                .ToArray();
            var auditCapacity = Math.Max(0, MaxAuditScopes - audited);
            foreach (var node in auditNodes.Take(auditCapacity))
            {
                token.ThrowIfCancellationRequested();
                audited++;
                var audit = await intelligence.AuditOnlyAsync(node, graph, token);
                coverage.Add(new($"package-audit:{project}:{node.Framework}:{node.RuntimeIdentifier ?? "any"}:{node.Id}",
                    Map(audit.Status), audit.Diagnostics.Count == 0
                        ? "Resolved package checked against complete permitted advisory partitions; candidate versions not checked."
                        : string.Join("; ", audit.Diagnostics)));
                if (audit.Advisories.Count == 0) continue;
                var subject = Subject(workspace, node);
                findings.Add(new(Contract.Version, "package:" + subject, subject,
                    Revision(subject, audit.EvidenceRevision, graph.Fingerprint),
                    "package-advisory",
                    audit.Advisories.Max(a => a.Severity) switch
                    {
                        >= 3 => Priority.Critical, 2 => Priority.High, 1 => Priority.Medium,
                        _ => Priority.Low
                    },
                    node.Version.ToNormalizedString(), null, Scope(workspace, node),
                    Map(audit.Status),
                    [new Evidence("settled-assets", new Uri(graph.AssetsPath).AbsoluteUri, audit.RetrievedAt),
                        .. audit.Advisories.Select(a =>
                            new Evidence("nuget-advisory", a.Url, audit.RetrievedAt))],
                    "review-advisory-and-direct-parent-trial-no-known-fix", true, "package"));
            }
            auditOmitted += Math.Max(0, auditNodes.Length - auditCapacity);
            if (skipped > 0)
            {
                omitted += skipped;
                coverage.Add(new("package-check-limit:" + project, Assessment.Partial,
                    $"{skipped} mapped direct package scopes were not assessed after the {MaxAssessments}-scope limit."));
            }
        }
        if (workspace.Projects.Count > MaxGraphs || omitted > 0 || auditOmitted > 0)
            coverage.Add(new("package-scan-limit", Assessment.Partial,
                $"Read at most {MaxGraphs} project graphs; {omitted} mapped direct candidate scopes and " +
                $"{auditOmitted} resolved advisory scopes were not assessed."));
        return new(findings, coverage);
    }

    public static async Task<PackageContextReport> ContextAsync(WorkspaceInventory workspace,
        string? handle = null, CancellationToken token = default)
    {
        var reader = new PackageGraphReader();
        var skills = new List<PackageSkillDescriptor>();
        PackageSkillDescriptor? selected = null;
        GuidanceDocument? entry = null;
        IReadOnlyList<GuidanceDocument> references = [];
        string? selectedAssets = null;
        string? selectedProject = null;
        var diagnostics = new List<string>();
        var examined = 0;
        if (workspace.Projects.Count == 0)
            diagnostics.Add("No SDK-style projects were discovered in the admitted workspace.");
        var projects = workspace.Projects.Take(MaxGraphs).ToArray();
        for (var index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            token.ThrowIfCancellationRequested();
            var (graph, _) = ReadGraph(workspace, project, reader);
            if (graph.Status != EvidenceStatus.Complete)
            {
                diagnostics.Add($"{project}: {graph.Diagnostic}");
                continue;
            }
            var resolved = graph.Packages.ToArray();
            foreach (var missing in resolved.Where(n => n.PackagePath is null).Take(8))
                diagnostics.Add($"{project}/{missing.Id}@{missing.Version}: exact restored package path unavailable.");
            var restored = resolved.Where(n => n.PackagePath is not null).ToArray();
            var remaining = MaxGuidancePackages - examined;
            foreach (var node in restored.Take(remaining))
            {
                token.ThrowIfCancellationRequested();
                examined++;
                var guidance = new PackageGuidanceReader().ReadRestored(node);
                if (guidance.Status != EvidenceStatus.Complete)
                {
                    diagnostics.Add($"{project}/{node.Id}: {guidance.Diagnostic}");
                    continue;
                }
                foreach (var skill in guidance.Skills)
                {
                    var identity = $"{skill.Handle}\0{graph.Fingerprint}\0{skill.Entry.Path}";
                    var exactHandle = "skill:" + Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    var descriptor = new PackageSkillDescriptor(exactHandle, skill.Name, skill.Description,
                        project, node.Framework, node.RuntimeIdentifier, node.Id,
                        node.Version.ToNormalizedString(), node.Source, node.ContentHash, graph.Fingerprint);
                    skills.Add(descriptor);
                    if (handle != exactHandle) continue;
                    if (selected is not null)
                        throw new InvalidDataException("Multiple restored skills match this handle.");
                    selected = descriptor;
                    entry = skill.Entry;
                    references = skill.References;
                    selectedAssets = graph.AssetsPath;
                    selectedProject = Path.Combine(workspace.Root, project);
                }
            }
            if (examined >= MaxGuidancePackages)
            {
                if (restored.Length > remaining || index + 1 < projects.Length ||
                    workspace.Projects.Count > MaxGraphs)
                    diagnostics.Add($"Guidance inspection limited to {MaxGuidancePackages} resolved package entries.");
                break;
            }
        }
        if (handle is not null && selected is null)
            throw new InvalidOperationException("Skill handle is not present in the current exact restored graph.");
        if (workspace.Projects.Count > MaxGraphs)
            diagnostics.Add($"Guidance limited to {MaxGraphs} project graphs.");
        void VerifySelectedAssets()
        {
            if (selected is null) return;
            var (current, _) = ReadGraph(workspace, selected.Project, reader);
            if (current.Status != EvidenceStatus.Complete ||
                current.Fingerprint != selected.AssetsFingerprint)
                throw new InvalidOperationException(
                    "Restored assets changed while reading the selected skill; request a new handle.");
        }
        VerifySelectedAssets();
        if (entry is not null && Environment.GetEnvironmentVariable("NUDGE_INSPECTOR_PATH")
            is { Length: > 0 } executable)
        {
            bool native;
            try { native = DotnetProbes.IsNativeExecutable(executable); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                diagnostics.Add($"Configured inspector could not be inspected ({ex.Message}); local reader was used.");
                native = false;
            }
            if (!native)
            {
                if (!diagnostics.Any(d => d.StartsWith("Configured inspector", StringComparison.Ordinal)))
                    diagnostics.Add("Configured inspector is not an installed native executable; local reader was used.");
            }
            else
            {
                var inspector = new InspectorAdapter(WorkspaceDiscovery.Canonical(executable));
                var (rows, inventory) = await inspector.InventoryAsync(
                    selectedProject!, selectedAssets!, reader, token);
                var matches = rows.Where(r =>
                    r.PackageId.Equals(selected!.PackageId, StringComparison.OrdinalIgnoreCase) &&
                    r.Version == selected.Version && r.Path == entry.Path &&
                    r.AssetsFingerprint == selected.AssetsFingerprint).ToArray();
                if (inventory.Status == EvidenceStatus.Complete && matches.Length == 1)
                {
                    var result = await inspector.ReadProjectSkillAsync(
                        selectedProject!, selectedAssets!, matches[0], reader, token);
                    if (result.Status == EvidenceStatus.Complete && result.Content is { } text &&
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) == entry.Sha256)
                        entry = entry with { Text = text };
                    else diagnostics.Add(result.Diagnostic ??
                        "Pinned inspector content differed from exact restored package bytes; local reader was used.");
                }
                else diagnostics.Add(inventory.Diagnostic ??
                    "Pinned inspector row was missing or ambiguous for the selected package scope; local reader was used.");
            }
        }
        VerifySelectedAssets();
        return new(Contract.Version, workspace.WorkspaceId,
            diagnostics.Count == 0 ? Assessment.Confirmed :
                workspace.Projects.Count == 0 ? Assessment.Unknown : Assessment.Partial, skills,
            selected, entry, references,
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics.Take(8)));
    }

    public static async Task<PackageAssessmentReport> AssessAsync(WorkspaceInventory workspace,
        string project, string framework, string? runtimeIdentifier, string packageId,
        IReadOnlySet<string> approvedSourceNames, bool acquisitionApproved,
        CancellationToken token = default)
    {
        if (!acquisitionApproved || approvedSourceNames.Count == 0 ||
            approvedSourceNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Explicit metadata acquisition approval and exact source names are required.");
        approvedSourceNames = new HashSet<string>(approvedSourceNames, StringComparer.OrdinalIgnoreCase);
        if (!workspace.Projects.Contains(project, StringComparer.Ordinal))
            throw new ArgumentException("Project must be an exact relative path in the admitted workspace.");
        var reader = new PackageGraphReader();
        var (graph, path) = ReadGraph(workspace, project, reader);
        if (graph.Status != EvidenceStatus.Complete)
            throw new InvalidOperationException(graph.Diagnostic ?? "Exact restored graph is unavailable.");
        var node = graph.Packages.SingleOrDefault(p =>
            p.Framework == framework && p.RuntimeIdentifier == runtimeIdentifier &&
            p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("No exact package/TFM/RID identity in the restored graph.");
        var policy = new PackageSourcePolicy(Path.GetDirectoryName(path)!);
        if (policy.Allowed(node.Id, approvedSourceNames).Count == 0)
            throw new ArgumentException("No configured and permitted package source matches the approved names.");
        if (policy.AuditSources.Any(s => !approvedSourceNames.Contains(s.Name)))
            throw new ArgumentException("All configured audit sources must be named in the approved source set.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var assessment = await new PackageIntelligence(policy).AssessAsync(node, graph,
                approvedSourceNames, cancellationToken: deadline.Token);
            return new(Contract.Version, workspace.WorkspaceId, graph.Fingerprint, assessment);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Package metadata assessment exceeded 45 seconds.");
        }
    }

    static (PackageGraph Graph, string Project) ReadGraph(WorkspaceInventory workspace,
        string relativeProject, PackageGraphReader reader)
    {
        var project = Path.Combine(workspace.Root, relativeProject);
        var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        var actual = WorkspaceDiscovery.Canonical(assets);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!actual.StartsWith(workspace.Root + Path.DirectorySeparatorChar, comparison))
            return (new(assets, "", [], EvidenceStatus.Unknown,
                "Assets path escapes the admitted workspace."), project);
        var inputs = workspace.ConfigurationFiles
            .Where(path => path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("NuGet.Config", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(workspace.Root, path)).Append(project);
        var graph = reader.Read(assets, inputs);
        if (graph.Status == EvidenceStatus.Complete && graph.Packages.Any(n =>
            !WorkspaceDiscovery.Canonical(n.Project).Equals(WorkspaceDiscovery.Canonical(project), comparison)))
            return (new(assets, "", [], EvidenceStatus.Unknown,
                "Assets project identity differs from the admitted project."), project);
        return (graph, project);
    }

    static FindingScope Scope(WorkspaceInventory workspace, PackageNode node) =>
        new(Path.GetRelativePath(workspace.Root, WorkspaceDiscovery.Canonical(node.Project)).Replace('\\', '/'),
            node.Framework, null, node.RuntimeIdentifier, node.Source);

    static string Subject(WorkspaceInventory workspace, PackageNode node) =>
        $"package:{WorkspaceDiscovery.SdkScopeKey(workspace.Root, Scope(workspace, node).Project!)}" +
        $"|{node.Framework}|{node.RuntimeIdentifier ?? ""}|{node.Id.ToLowerInvariant()}";

    static Assessment Map(EvidenceStatus status) => status switch
    {
        EvidenceStatus.Complete => Assessment.Confirmed,
        EvidenceStatus.Partial => Assessment.Partial,
        _ => Assessment.Unknown
    };

    static string Revision(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant()[..16];
}

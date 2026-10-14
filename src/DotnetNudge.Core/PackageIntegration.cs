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
    const int MaxGuidancePackages = 16;

    public static async Task<PackageScanResult> ScanAsync(WorkspaceInventory workspace,
        string stateDirectory, CancellationToken token)
    {
        var findings = new List<Finding>();
        var coverage = new List<Coverage>();
        var reader = new PackageGraphReader();
        var assessed = 0;
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
            var history = new GraphTransitions(Path.Combine(stateDirectory, "package-graphs",
                workspace.WorkspaceId, filename + ".json"));
            var changes = history.Observe(graph);
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
            if (assessed >= MaxAssessments) continue;
            PackageSourcePolicy policy;
            try { policy = new PackageSourcePolicy(Path.GetDirectoryName(projectPath)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                coverage.Add(new("package-sources:" + project, Assessment.Blocked, ex.Message));
                continue;
            }
            var intelligence = new PackageIntelligence(policy);
            foreach (var node in graph.Packages.Where(n => n.Direct)
                .DistinctBy(n => (n.Framework, n.RuntimeIdentifier, n.Id))
                .Take(MaxAssessments - assessed))
            {
                token.ThrowIfCancellationRequested();
                if (policy.Allowed(node.Id).Count == 0)
                {
                    coverage.Add(new("package-feed:" + project + ":" + node.Id, Assessment.Unknown,
                        "No mapped source; candidate metadata requires an explicitly approved source."));
                    continue;
                }
                assessed++;
                var assessment = await intelligence.AssessAsync(node, graph, cancellationToken: token);
                coverage.Add(new("package-feed:" + project + ":" + node.Id, Map(assessment.Status),
                    assessment.Diagnostics.Count == 0
                        ? $"Checked permitted audit and candidate metadata for {node.Framework}."
                        : string.Join("; ", assessment.Diagnostics)));
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
        }
        if (workspace.Projects.Count > MaxGraphs || assessed == MaxAssessments)
            coverage.Add(new("package-scan-limit", Assessment.Partial,
                $"Read at most {MaxGraphs} project graphs and assessed at most {MaxAssessments} mapped direct packages."));
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
                    var descriptor = new PackageSkillDescriptor(skill.Handle, skill.Name, skill.Description,
                        project, node.Framework, node.RuntimeIdentifier, node.Id,
                        node.Version.ToNormalizedString(), node.Source, node.ContentHash, graph.Fingerprint);
                    skills.Add(descriptor);
                    if (handle != skill.Handle) continue;
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
        if (entry is not null && Environment.GetEnvironmentVariable("NUDGE_INSPECTOR_PATH")
            is { Length: > 0 } executable)
        {
            bool native;
            try { native = DotnetProbes.IsNativeExecutable(executable); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

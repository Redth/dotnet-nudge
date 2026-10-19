using System.Security.Cryptography;
using System.Text;
using DotnetNudge.Packages;

namespace DotnetNudge.Core;

public sealed record PackageReportCandidate(string Version, string Kind, string Source,
    EvidenceStatus Compatibility, string? Diagnostic, string? ProjectUrl, string? Description);
public sealed record PackageReportPath(IReadOnlyList<string> PackageIds, string DirectParent,
    string? ParentCandidate, string? ParentSource, EvidenceStatus Coverage, string? Diagnostic);
public sealed record PackageReportEntry(string Id, string Revision, string Project, string Framework,
    string? RuntimeIdentifier, string PackageId, string Current, bool Direct, bool CentrallyManaged,
    EvidenceStatus Status, IReadOnlyList<PackageReportCandidate> Candidates,
    IReadOnlyList<PackageAdvisory> Advisories, string? MinimalKnownFix,
    IReadOnlyList<PackageDeprecation> Deprecations, IReadOnlyList<PackageReportPath> Paths,
    IReadOnlyList<string> Diagnostics);
public sealed record PackageReport(int SchemaVersion, string WorkspaceId, string Workspace,
    DateTimeOffset GeneratedAt, bool Complete, IReadOnlyList<string> Solutions,
    IReadOnlyList<PackageReportEntry> Packages, IReadOnlyList<Coverage> Coverage);

public static partial class PackageIntegration
{
    const int MaxReportProjects = 64;
    const int MaxReportScopes = 512;
    const int MaxReportRows = 10000;

    // Explicit reporting has its own budget; passive reminder limits must not masquerade as a full report.
    public static async Task<PackageReport> ReportAsync(WorkspaceInventory workspace,
        CancellationToken token = default)
    {
        var entries = new List<PackageReportEntry>();
        var coverage = new List<Coverage>();
        var reader = new PackageGraphReader();
        var examined = 0;
        var complete = workspace.Projects.Count > 0 && workspace.Projects.Count <= MaxReportProjects;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        foreach (var project in workspace.Projects.Take(MaxReportProjects))
        {
            token.ThrowIfCancellationRequested();
            var (graph, path) = ReadGraph(workspace, project, reader);
            if (graph.Status != EvidenceStatus.Complete)
            {
                complete = false;
                coverage.Add(new("package-graph:" + project, Assessment.Unknown,
                    graph.Diagnostic ?? "Restored graph unavailable."));
                continue;
            }
            var policy = new PackageSourcePolicy(Path.GetDirectoryName(path)!);
            var intelligence = new PackageIntelligence(policy);
            var capacity = MaxReportRows - entries.Count;
            if (graph.Packages.Count > capacity)
            {
                complete = false;
                coverage.Add(new("package-report-rows:" + project, Assessment.Partial,
                    $"{graph.Packages.Count - capacity} restored scopes omitted after the {MaxReportRows}-row limit."));
            }
            foreach (var node in graph.Packages.OrderByDescending(n => n.Direct)
                .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase).Take(capacity))
            {
                token.ThrowIfCancellationRequested();
                PackageAssessment assessment;
                if (examined >= MaxReportScopes || deadline.IsCancellationRequested)
                {
                    assessment = new(node, EvidenceStatus.Unknown, [], [], null, [],
                        ["Report metadata budget reached; this scope has not been assessed."]);
                }
                else
                {
                    examined++;
                    try
                    {
                        assessment = await intelligence.AssessAsync(node, graph,
                            cancellationToken: deadline.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        assessment = new(node, EvidenceStatus.Unknown, [], [], null, [],
                            ["Report metadata deadline reached; this scope has not been assessed."]);
                    }
                }
                complete &= assessment.Status == EvidenceStatus.Complete;
                var identity = $"{workspace.WorkspaceId}\0{project}\0{node.Framework}\0{node.RuntimeIdentifier}\0{node.Id.ToLowerInvariant()}";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                var revision = Revision(graph.Fingerprint, assessment.EvidenceRevision);
                // Retain the newest target per kind/source, plus every source for a minimal known fix.
                var candidates = assessment.Candidates.GroupBy(c => (c.Source, c.Kind)).Select(g => g.Last())
                    .Concat(assessment.Candidates.Where(c => c.Version == assessment.MinimalKnownFix))
                    .DistinctBy(c => (c.Version, c.Source)).OrderBy(c => c.Version)
                    .Select(c => new PackageReportCandidate(c.Version.ToNormalizedString(), c.Kind,
                        c.Source, c.Compatibility, c.Diagnostic, c.ProjectUrl,
                        c.Description is { Length: > 2000 } text ? text[..2000] + "..." : c.Description)).ToArray();
                entries.Add(new(id, revision, project, node.Framework, node.RuntimeIdentifier,
                    node.Id, node.Version.ToNormalizedString(), node.Direct, node.CentrallyManaged,
                    assessment.Status, candidates, assessment.Advisories,
                    assessment.MinimalKnownFix?.ToNormalizedString(), assessment.Deprecations,
                    assessment.Paths.Select(p => new PackageReportPath(p.PackageIds, p.DirectParent,
                        p.ParentCandidate?.ToNormalizedString(), p.ParentSource, p.Coverage, p.Diagnostic)).ToArray(),
                    assessment.Diagnostics));
            }
            coverage.Add(new("package-graph:" + project, Assessment.Confirmed,
                $"Read {graph.Packages.Count} restored project/TFM/RID scopes."));
        }
        if (!complete)
            coverage.Add(new("package-report", Assessment.Partial,
                $"Metadata is bounded to {MaxReportProjects} projects, {MaxReportScopes} package scopes and 90 seconds. " +
                "Missing graphs, unmapped sources and unassessed scopes are not up-to-date results. " +
                "Unmapped metadata requires separate exact-scope/source approval."));
        return new(Contract.Version, workspace.WorkspaceId, workspace.Root, DateTimeOffset.UtcNow,
            complete, workspace.Solutions, entries, coverage);
    }
}

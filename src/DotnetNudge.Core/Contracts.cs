using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetNudge.Core;

public static class Contract
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };
}

public enum Assessment { Confirmed, Unknown, Partial, Stale, Blocked }
public enum Priority { Critical, High, Medium, Low }
public enum DecisionAction { Snooze, SkipVersion, DisableCategory, EnableCategory }

public sealed record Evidence(string Type, string Uri, DateTimeOffset RetrievedAt);
public sealed record FindingScope(string? Project, string? Framework, string? SdkScope,
    string? RuntimeIdentifier = null, string? Source = null);
public sealed record Finding(
    int SchemaVersion, string Id, string SubjectKey, string Revision, string Kind,
    Priority Priority, string? Current, string? Candidate, FindingScope Scope,
    Assessment Assessment, IReadOnlyList<Evidence> Evidence, string Action, bool RequiresConsent,
    string Category);
public sealed record Coverage(string Check, Assessment Assessment, string Detail);
public sealed record SdkDeclaration(string Scope, string? Version, string? RollForward,
    bool? AllowPrerelease, IReadOnlyList<string> Paths, string? WorkloadVersion, string RawJson);
public sealed record ProjectDeclaration(string Path, string? Sdk,
    IReadOnlyList<string> TargetFrameworkExpressions);
public sealed record WorkspaceInventory(
    int SchemaVersion, string RepositoryId, string WorktreeId, string WorkspaceId, string Root,
    IReadOnlyList<string> Projects, IReadOnlyList<ProjectDeclaration> ProjectDeclarations,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> ConfigurationFiles, IReadOnlyList<SdkDeclaration> SdkDeclarations,
    IReadOnlyList<Coverage> Coverage);
public sealed record ScanReport(int SchemaVersion, WorkspaceInventory Workspace,
    IReadOnlyList<Finding> Findings, IReadOnlyList<Coverage> Coverage, DateTimeOffset ScannedAt);
public sealed record FindingStatus(Finding Finding, bool Eligible, string? Suppression);
public sealed record StatusReport(int SchemaVersion, string RepositoryId, string WorktreeId, string WorkspaceId,
    DateTimeOffset? ScannedAt, IReadOnlyList<FindingStatus> Findings, IReadOnlyList<Coverage> Coverage);
public sealed record DecisionResult(int SchemaVersion, string FindingId, string Revision,
    DecisionAction Action, DateTimeOffset RecordedAt, DateTimeOffset? Until);
public sealed record DoctorReport(int SchemaVersion, string StateDirectory, string? Workspace,
    string? RepositoryId, string? WorktreeId, string? WorkspaceId, string ReleaseMetadataSource,
    string Note);

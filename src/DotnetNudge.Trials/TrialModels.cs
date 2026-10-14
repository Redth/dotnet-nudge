using System.Text.Json.Serialization;

namespace DotnetNudge.Trials;

[JsonConverter(typeof(JsonStringEnumConverter<TrialPhase>))]
public enum TrialPhase
{
    Proposed, Approved, Preparing, Baseline, Upgrading, Validating,
    ReadyForReview, Blocked, Failed, Cancelled, Adopted, Archived
}

public sealed record TrialCommand(string Verb, string[] Arguments, string WorkingDirectory = ".");
public sealed record PackageEdit(string ProjectPath, string PackageId, string FromVersion, string ToVersion);
public sealed record SdkSelection(string Version, string Rid, string? PreparedRoot = null, bool AllowDownload = false);
public sealed record TrialBudget(long MaxDiskBytes, TimeSpan MaxDuration, TimeSpan MaxProcessDuration);
public sealed record TrialArtifactPaths(string LogsDirectory, string DiffFile);
public sealed record OriginalFingerprint(string Head, string Branch, string IndexSha256,
    string TrackedAndUntrackedSha256, string PorcelainStatus);
public sealed record TrialPlan(
    string Id, int Revision, string Repository, string SourceCommit, string TrialRoot,
    string[] ProjectScopes, SdkSelection BaselineSdk, SdkSelection CandidateSdk,
    PackageEdit[] PackageEdits, TrialCommand[] Commands, TrialArtifactPaths Artifacts, TrialBudget Budget,
    bool SdkOnly, bool AllowNetwork = false);

public sealed record CommandEvidence(
    string Stage, string Executable, string[] Arguments, string WorkingDirectory,
    Dictionary<string, string> Environment, int? ExitCode, bool TimedOut,
    string StdoutPath, string StderrPath, long DurationMilliseconds, int? TestCount = null,
    string[]? BuildArtifacts = null, string? ArtifactSha256 = null, bool ArtifactVerified = false);

[JsonConverter(typeof(JsonStringEnumConverter<TrialDimension>))]
public enum TrialDimension { NotExercised, Passed, Failed }

public sealed record StageSummary(
    TrialDimension Restore, TrialDimension Compilation, TrialDimension AutomatedTests,
    int TestCount, TrialDimension RuntimeSmoke, TrialDimension PlatformMatrix);

public sealed record TrialReport(
    string SourceCommit, bool OriginalDirtyExcluded, string Branch, string Worktree,
    string BaselineSdk, string CandidateSdk, string DiffPath,
    string Limitations, List<CommandEvidence> Commands,
    StageSummary? Baseline = null, StageSummary? Candidate = null,
    SdkPreparation? BaselineSdkProvenance = null, SdkPreparation? CandidateSdkProvenance = null,
    string? OriginalGitStatusBefore = null, string? OriginalGitStatusAfter = null,
    string[]? BaselineWarnings = null, string[]? CandidateWarnings = null,
    string[]? AddedWarnings = null, string? CandidateBranch = null,
    string? CandidateWorktree = null, OriginalFingerprint? OriginalBefore = null,
    OriginalFingerprint? OriginalAfter = null);

public sealed class TrialRecord
{
    public required TrialPlan Plan { get; set; }
    public TrialPhase Phase { get; set; } = TrialPhase.Proposed;
    public int? ApprovedRevision { get; set; }
    public string? ApprovedPlanDigest { get; set; }
    public string? OwnerToken { get; set; }
    public string? BlockReason { get; set; }
    public TrialReport? Report { get; set; }
    public List<string> Checkpoints { get; init; } = [];
}

public sealed class TrialException(string message) : Exception(message);

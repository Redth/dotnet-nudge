using DotnetNudge.Trials;

namespace DotnetNudge.Core;

public sealed record TrialStatusReport(int SchemaVersion, string Id, int Revision, string Phase,
    string? BlockReason, TrialPlan Plan, TrialReport? Report, IReadOnlyList<string> Checkpoints);

public sealed class TrialIntegration(NudgeState state, TrialRunner? runner = null)
{
    readonly TrialRunner trials = runner ?? new TrialRunner(
        new TrialStore(Path.Combine(state.DirectoryPath, "trials")));
    public string Root => Path.Combine(state.DirectoryPath, "trials");

    static TrialStatusReport Status(TrialRecord record) =>
        new(Contract.Version, record.Plan.Id, record.Plan.Revision,
            System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(record.Phase.ToString()),
            record.BlockReason, record.Plan, record.Report, record.Checkpoints);

    void VerifyWorkspace(string workspace, TrialPlan plan)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
            throw new ArgumentException("Trial workspace must be an existing absolute path.");
        if (!Path.IsPathFullyQualified(plan.Repository) || !Path.IsPathFullyQualified(plan.TrialRoot))
            throw new ArgumentException("Trial repository and trial root must be absolute paths.");
        if (WorkspaceDiscovery.Canonical(plan.Repository) != WorkspaceDiscovery.Canonical(workspace))
            throw new ArgumentException("Trial repository must equal the admitted workspace root.");
        if (WorkspaceDiscovery.Canonical(plan.TrialRoot) != WorkspaceDiscovery.Canonical(Root))
            throw new ArgumentException("Trial root must be the engine-owned state trial directory.");
        var projects = WorkspaceDiscovery.Discover(workspace).Projects.ToHashSet(StringComparer.Ordinal);
        if (plan.ProjectScopes.Length == 0 ||
            plan.ProjectScopes.Any(project => !projects.Contains(project)))
            throw new ArgumentException("Every trial project must be an exact discovered workspace project.");
    }

    TrialRecord Existing(string workspace, string id)
    {
        var record = trials.Status(id);
        VerifyWorkspace(workspace, record.Plan);
        return record;
    }

    public TrialStatusReport Propose(string workspace, TrialPlan plan)
    {
        VerifyWorkspace(workspace, plan);
        return Status(trials.Propose(plan));
    }

    public TrialStatusReport Approve(string workspace, string id, int revision)
    {
        Existing(workspace, id);
        return Status(trials.Approve(id, revision));
    }

    public async Task<TrialStatusReport> ExecuteAsync(string workspace, string id, int revision,
        CancellationToken token = default)
    {
        Existing(workspace, id);
        return Status(await trials.ExecuteAsync(id, revision, token));
    }

    public TrialStatusReport Read(string workspace, string id) => Status(Existing(workspace, id));

    public TrialStatusReport Cancel(string workspace, string id)
    {
        Existing(workspace, id);
        return Status(trials.Cancel(id));
    }

    public async Task<TrialStatusReport> CleanupAsync(string workspace, string id)
    {
        Existing(workspace, id);
        return Status(await trials.CleanupAsync(id));
    }
}

using System.Text.Json;
using DotnetNudge.Trials;

namespace DotnetNudge.Core;

public sealed class NudgeService(NudgeState state, string? owner = null, bool closeOnDispose = true,
    TrialRunner? runner = null) : IDisposable
{
    readonly string sessionOwner = owner ?? "cli:" + Guid.NewGuid().ToString("N");
    readonly HashSet<string> registered = new(StringComparer.Ordinal);
    readonly TrialIntegration trials = new(state, runner);
    static string AdmittedPath(string root) =>
        Path.IsPathFullyQualified(root) ? WorkspaceDiscovery.Canonical(root)
            : throw new ArgumentException("Workspace path must be absolute.", nameof(root));
    public StatusReport Status(string root)
    {
        root = AdmittedPath(root);
        var (repository, worktree) = WorkspaceDiscovery.Identities(root);
        return state.Status(repository, worktree, WorkspaceDiscovery.ScopeId(root));
    }

    public async Task<ScanReport> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        root = AdmittedPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        state.Register(root, TimeSpan.FromMinutes(15), sessionOwner);
        registered.Add(root);
        var workspace = WorkspaceDiscovery.ScopeId(root);
        state.RequestScan(workspace);
        var job = state.LeaseScan(sessionOwner, workspace)
            ?? throw new InvalidOperationException("A scan is already running or is awaiting a bounded retry.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var source = new ReleaseMetadata(state, client,
            fixtureDirectory: Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR"));
        try
        {
            var inputFingerprint = NudgeState.WorkspaceFingerprint(root);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await new NudgeScanner(source).ScanAsync(root, deadline.Token,
                probeInstalled: true, state: state, checkAgentTools: true);
            state.FinishScan(job, sessionOwner, result, null, inputFingerprint);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            state.FinishScan(job, sessionOwner, null, ex.Message);
            throw;
        }
    }

    public DecisionResult Decide(string root, string id, string revision, DecisionAction action,
        DateTimeOffset? until = null)
    {
        root = AdmittedPath(root);
        var (repository, _) = WorkspaceDiscovery.Identities(root);
        return state.Decide(repository, WorkspaceDiscovery.ScopeId(root), id, revision, action, until);
    }

    public PreferencesReport Preferences(string root)
    {
        root = AdmittedPath(root);
        var (repository, _) = WorkspaceDiscovery.Identities(root);
        return new(Contract.Version, repository, WorkspaceDiscovery.ScopeId(root),
            state.DisabledCategories(repository),
            Status(root).Findings.Where(f => f.Suppression is not null).ToArray());
    }

    public CategoryPreferenceResult EnableCategory(string root, string category)
    {
        var (repository, _) = WorkspaceDiscovery.Identities(AdmittedPath(root));
        return state.EnableCategory(repository, category);
    }

    public Task<PackageContextReport> PackageContextAsync(string root, string? handle = null,
        CancellationToken token = default) =>
        PackageIntegration.ContextAsync(WorkspaceDiscovery.Discover(AdmittedPath(root)), handle, token);

    public Task<PackageReport> PackageReportAsync(string root, CancellationToken token = default) =>
        PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(AdmittedPath(root)), token);

    public async Task<AgentToolsReport> AgentToolsAsync(string root, string? inventoryJson = null,
        CancellationToken token = default) =>
        (await AgentTools.AssessAsync(WorkspaceDiscovery.Discover(AdmittedPath(root)),
            state, inventoryJson, token)).Report;

    public Task<PackageAssessmentReport> AssessPackageAsync(string root, string project,
        string framework, string? runtimeIdentifier, string packageId,
        IReadOnlySet<string> approvedSourceNames, bool acquisitionApproved,
        CancellationToken token = default) =>
        PackageIntegration.AssessAsync(WorkspaceDiscovery.Discover(AdmittedPath(root)),
            project, framework, runtimeIdentifier, packageId, approvedSourceNames,
            acquisitionApproved, token);

    public async Task<ToolchainScanResult> ToolchainsAsync(string root,
        CancellationToken token = default)
    {
        var inventory = WorkspaceDiscovery.Discover(AdmittedPath(root));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var metadata = new ReleaseMetadata(state, client,
            fixtureDirectory: Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var observed = await DotnetProbes.ReadAsync(inventory, deadline.Token);
            return await ToolchainIntegration.ScanAsync(inventory, observed, metadata, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Read-only toolchain assessment exceeded 45 seconds.");
        }
    }

    public string TrialRoot => trials.Root;
    public TrialStatusReport ProposeTrial(string root, TrialPlan plan) =>
        trials.Propose(AdmittedPath(root), plan);
    public TrialStatusReport ApproveTrial(string root, string id, int revision,
        string expectedPlanDigest) =>
        trials.Approve(AdmittedPath(root), id, revision, expectedPlanDigest);
    public Task<TrialStatusReport> ExecuteTrialAsync(string root, string id, int revision,
        CancellationToken token = default) =>
        trials.ExecuteAsync(AdmittedPath(root), id, revision, token);
    public TrialStatusReport TrialStatus(string root, string id) =>
        trials.Read(AdmittedPath(root), id);
    public TrialStatusReport CancelTrial(string root, string id) =>
        trials.Cancel(AdmittedPath(root), id);
    public Task<TrialStatusReport> CleanupTrialAsync(string root, string id) =>
        trials.CleanupAsync(AdmittedPath(root), id);

    public StatusReport Register(string root)
    {
        root = AdmittedPath(root);
        state.Register(root, TimeSpan.FromMinutes(15), sessionOwner);
        registered.Add(root);
        state.RequestScan(WorkspaceDiscovery.ScopeId(root));
        return Status(root);
    }

    public void Dispose()
    {
        if (closeOnDispose)
            foreach (var root in registered) state.Close(root, sessionOwner);
    }
}

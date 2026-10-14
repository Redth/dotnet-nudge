using System.Text.Json;

namespace DotnetNudge.Core;

public sealed class NudgeService(NudgeState state, string? owner = null, bool closeOnDispose = true) : IDisposable
{
    readonly string sessionOwner = owner ?? "cli:" + Guid.NewGuid().ToString("N");
    readonly HashSet<string> registered = new(StringComparer.Ordinal);
    public StatusReport Status(string root)
    {
        root = WorkspaceDiscovery.Canonical(root);
        var (repository, worktree) = WorkspaceDiscovery.Identities(root);
        return state.Status(repository, worktree, WorkspaceDiscovery.ScopeId(root));
    }

    public async Task<ScanReport> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        root = WorkspaceDiscovery.Canonical(root);
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
            var result = await new NudgeScanner(source).ScanAsync(root, cancellationToken);
            state.FinishScan(job, sessionOwner, result, null);
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
        root = WorkspaceDiscovery.Canonical(root);
        var (repository, _) = WorkspaceDiscovery.Identities(root);
        return state.Decide(repository, WorkspaceDiscovery.ScopeId(root), id, revision, action, until);
    }

    public StatusReport Register(string root)
    {
        root = WorkspaceDiscovery.Canonical(root);
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

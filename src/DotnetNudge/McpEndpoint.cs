using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using DotnetNudge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetNudge;

public static class McpEndpoint
{
    public static async Task RunAsync(string stateDir, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        var location = new McpStateDirectory(stateDir);
        builder.Services.AddSingleton(location);
        builder.Services.AddHostedService<ScanWorker>();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<NudgeTools>();
        try { await builder.Build().RunAsync(cancellationToken); }
        finally
        {
            using var state = new NudgeState(stateDir);
            foreach (var root in location.Registered.Keys) state.Close(root, location.Owner);
        }
    }
}

public sealed class McpStateDirectory(string path)
{
    public string Path { get; } = path;
    public string Owner { get; } = "mcp:" + Guid.NewGuid().ToString("N");
    public ConcurrentDictionary<string, byte> Registered { get; } = new(StringComparer.Ordinal);
}

[McpServerToolType]
public sealed class NudgeTools(McpStateDirectory location)
{
    async Task<string> WithServiceAsync(Func<NudgeService, Task<string>> operation)
    {
        using var state = new NudgeState(location.Path);
        using var service = new NudgeService(state, location.Owner, closeOnDispose: false);
        return await operation(service);
    }

    async Task<string> RootAsync(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace))
            throw new ArgumentException("An absolute workspace path is required.");
        var root = WorkspaceDiscovery.Canonical(workspace);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if (server.ClientCapabilities?.Roots is not null)
        {
            var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken);
            var roots = result.Roots
                .Select(item => Uri.TryCreate(item.Uri, UriKind.Absolute, out var uri) &&
                    uri.IsFile && Directory.Exists(uri.LocalPath)
                    ? WorkspaceDiscovery.Canonical(uri.LocalPath) : null)
                .Where(path => path is not null).ToArray();
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!roots.Any(path => root.Equals(path, comparison) ||
                root.StartsWith(path + Path.DirectorySeparatorChar, comparison)))
                throw new McpException("Workspace is outside the MCP client's advertised roots.");
        }
        return root;
    }

    [McpServerTool(Name = "nudge_status"), Description("Read cached findings and scan coverage for an admitted workspace.")]
    public async Task<string> Status(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(service => Task.FromResult(
            JsonSerializer.Serialize(service.Status(root), Contract.Json)));
    }

    [McpServerTool(Name = "nudge_scan"), Description("Explicitly perform a read-only scan of the exact workspace root.")]
    public async Task<string> Scan(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        location.Registered.TryAdd(root, 0);
        return await WithServiceAsync(async service =>
            JsonSerializer.Serialize(await service.ScanAsync(root, cancellationToken), Contract.Json));
    }

    [McpServerTool(Name = "nudge_context"), Description("Read the evidence for a current finding without activating third-party instructions.")]
    public async Task<string> Context(McpServer server, string workspace, string findingId,
        CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(service =>
        {
            var report = service.Status(root);
            var finding = report.Findings.SingleOrDefault(f => f.Finding.Id == findingId)
                ?? throw new ArgumentException("Finding not present in current snapshot.");
            return Task.FromResult(JsonSerializer.Serialize(new { schemaVersion = Contract.Version,
                finding, coverage = report.Coverage }, Contract.Json));
        });
    }

    [McpServerTool(Name = "nudge_decide"), Description("Record a versioned snooze, skip or category preference.")]
    public async Task<string> Decide(McpServer server, string workspace, string findingId,
        string revision, string action, string? until = null, CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        if (!Enum.TryParse<DecisionAction>(action.Replace("-", ""), true, out var choice))
            throw new ArgumentException("Unknown decision action.");
        DateTimeOffset? expiry = until is null ? null : DateTimeOffset.Parse(until,
            System.Globalization.CultureInfo.InvariantCulture);
        return await WithServiceAsync(service => Task.FromResult(JsonSerializer.Serialize(
            service.Decide(root, findingId, revision, choice, expiry), Contract.Json)));
    }

    [McpServerTool(Name = "nudge_preferences"), Description("Read or change category preferences using a current finding revision.")]
    public async Task<string> Preferences(McpServer server, string workspace, string? findingId = null,
        string? revision = null, string? action = null, CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(service =>
        {
            if (action is null)
                return Task.FromResult(JsonSerializer.Serialize(service.Status(root).Findings
                    .Where(f => f.Suppression is not null), Contract.Json));
            if (action is not ("disable-category" or "enable-category") ||
                findingId is null || revision is null)
                throw new McpException("Changing a category requires action, findingId and current revision.");
            var choice = action == "disable-category" ? DecisionAction.DisableCategory : DecisionAction.EnableCategory;
            return Task.FromResult(JsonSerializer.Serialize(
                service.Decide(root, findingId, revision, choice), Contract.Json));
        });
    }

    [McpServerTool(Name = "nudge_trial"), Description("Trial operations require an approved plan and the trial module.")]
    public string Trial() => JsonSerializer.Serialize(new
    {
        schemaVersion = Contract.Version, assessment = Assessment.Blocked,
        detail = "Trial module is not installed; no changes were made."
    }, Contract.Json);

    [McpServerTool(Name = "nudge_register"), Description("Register an explicit workspace root and queue a durable read-only scan.")]
    public async Task<string> Register(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        location.Registered.TryAdd(root, 0);
        return await WithServiceAsync(service => Task.FromResult(
            JsonSerializer.Serialize(service.Register(root), Contract.Json)));
    }
}

public sealed class ScanWorker(McpStateDirectory location, ILogger<ScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var state = new NudgeState(location.Path);
        var owner = Guid.NewGuid().ToString("N");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var lastReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - lastReconcile >= TimeSpan.FromMinutes(1))
            {
                try { state.Reconcile(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "Workspace reconciliation failed."); }
                lastReconcile = DateTimeOffset.UtcNow;
            }
            var job = state.LeaseScan(owner);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }
            try
            {
                if (state.AdmittedRoot(job.Workspace) != job.Root)
                    throw new InvalidDataException("Registered workspace root changed or disappeared.");
                var source = new ReleaseMetadata(state, client,
                    fixtureDirectory: Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR"));
                var report = await new NudgeScanner(source).ScanAsync(job.Root, stoppingToken);
                state.FinishScan(job, owner, report, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "Scan failed for workspace {Workspace}", job.Workspace);
                state.FinishScan(job, owner, null, ex.Message);
            }
        }
    }
}

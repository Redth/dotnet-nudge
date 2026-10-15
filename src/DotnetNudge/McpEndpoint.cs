using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using DotnetNudge.Core;
using DotnetNudge.Trials;
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
    public TrialRunner Trials { get; } = new(new TrialStore(
        System.IO.Path.Combine(path, "trials")));
}

[McpServerToolType]
public sealed class NudgeTools(McpStateDirectory location)
{
    const string QuestionGuidance = " When offering maintenance choices, follow nudge-maintain's " +
        "interactive-question guidance and call the host's native AskUserQuestion (Claude Code) or " +
        "ask_user (Copilot) tool with one single-select question and 2-4 concrete choices including " +
        "Not now, not a choice menu in chat. Use short, friendly, plain-language questions and choices " +
        "such as What's new?, Help me update and Remind me next week, as appropriate. " +
        "For actionable NuGet package, workload, runtime and host-tool results, offer a relevant native " +
        "question too, not SDK-only choices or a report that stops without a way forward. Ask a separate " +
        "question for each actionable category, sequentially, waiting for each answer; do not stop after " +
        "SDKs or ask once per dependency. Bind each question to its described category and exact " +
        "findings; each answer authorizes only that scope. For deprecations or " +
        "advisories without a candidate, offer help finding an alternative or fix, not a promised update. " +
        "Configuration mismatches and existing graph changes are not available upgrades. Unknown, stale, " +
        "blocked or suppressed results are report-only for unsolicited suggestions; respect reminder " +
        "cooldowns and explicit report-only requests. " +
        "Keep finding IDs, revision tokens, assessment labels and ISO timestamps out of user-facing text. Details are " +
        "read-only; Help me update requests a plan, not approval for changes or trials. Explain the " +
        "actual changes and ask before executing them. If the tool " +
        "is unavailable or the session is noninteractive, report only; never assume consent.";

    async Task<string> WithServiceAsync(Func<NudgeService, Task<string>> operation)
    {
        using var state = new NudgeState(location.Path);
        using var service = new NudgeService(state, location.Owner, closeOnDispose: false,
            runner: location.Trials);
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

    [McpServerTool(Name = "nudge_status"), Description("Read cached findings and scan coverage for an admitted workspace." + QuestionGuidance)]
    public async Task<string> Status(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(service => Task.FromResult(
            JsonSerializer.Serialize(service.Status(root), Contract.Json)));
    }

    [McpServerTool(Name = "nudge_scan"), Description("Explicitly perform a read-only scan of the exact workspace root." + QuestionGuidance)]
    public async Task<string> Scan(McpServer server, string workspace, CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        location.Registered.TryAdd(root, 0);
        return await WithServiceAsync(async service =>
            JsonSerializer.Serialize(await service.ScanAsync(root, cancellationToken), Contract.Json));
    }

    [McpServerTool(Name = "nudge_context"), Description("Read the evidence for a current finding without activating third-party instructions." + QuestionGuidance)]
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

    [McpServerTool(Name = "nudge_package_context"), Description("List inert guidance handles from exact restored packages, or read one matching handle and its references." + QuestionGuidance)]
    public async Task<string> PackageContext(McpServer server, string workspace,
        string? handle = null, CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(async service =>
            JsonSerializer.Serialize(await service.PackageContextAsync(root, handle, cancellationToken),
                Contract.Json));
    }

    [McpServerTool(Name = "nudge_package_assess"), Description("Metadata-only package/advisory assessment for one exact restored project/TFM/RID/package, after the user explicitly approves the named configured sources (including audit sources). Does not download packages or install skills." + QuestionGuidance)]
    public async Task<string> PackageAssess(McpServer server, string workspace,
        string project, string framework, string packageId, string[] approvedSources,
        bool acquisitionApproved, string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        if (approvedSources is not { Length: > 0 })
            throw new McpException("At least one exact configured source name is required.");
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(async service => JsonSerializer.Serialize(
            await service.AssessPackageAsync(root, project, framework, runtimeIdentifier, packageId,
                approvedSources.ToHashSet(StringComparer.OrdinalIgnoreCase), acquisitionApproved,
                cancellationToken), Contract.Json));
    }

    [McpServerTool(Name = "nudge_toolchains"), Description("Inspect offline workload/host evidence and observed local runtime servicing without installing SDKs or workloads." + QuestionGuidance)]
    public async Task<string> Toolchains(McpServer server, string workspace,
        CancellationToken cancellationToken)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(async service =>
            JsonSerializer.Serialize(await service.ToolchainsAsync(root, cancellationToken), Contract.Json));
    }

    [McpServerTool(Name = "nudge_decide"), Description("Record a versioned snooze, skip or category preference only after the user's explicit approval of the reminder action, affected update/category and duration in plain language. Keep the exact finding ID, revision and expiry in tool arguments, not the question." + QuestionGuidance)]
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

    [McpServerTool(Name = "nudge_preferences"), Description("Read category preferences, disable with current finding evidence, or re-enable an already muted category by name. Mutations require explicit user approval of the exact category and scope." + QuestionGuidance)]
    public async Task<string> Preferences(McpServer server, string workspace, string? findingId = null,
        string? revision = null, string? action = null, string? category = null,
        CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(service =>
        {
            if (action is null)
                return Task.FromResult(JsonSerializer.Serialize(service.Preferences(root), Contract.Json));
            if (action == "enable-category" && category is not null &&
                findingId is null && revision is null)
                return Task.FromResult(JsonSerializer.Serialize(service.EnableCategory(root, category),
                    Contract.Json));
            if (action is not ("disable-category" or "enable-category") ||
                findingId is null || revision is null)
                throw new McpException("Changing a category requires action, findingId and current revision.");
            var choice = action == "disable-category" ? DecisionAction.DisableCategory : DecisionAction.EnableCategory;
            return Task.FromResult(JsonSerializer.Serialize(
                service.Decide(root, findingId, revision, choice), Contract.Json));
        });
    }

    [McpServerTool(Name = "nudge_trial"), Description("Propose/read a trial and execute only an already approved exact plan. Approval is unavailable via MCP; a human must confirm the persisted plan in an interactive CLI. No source checkout, global SDK, push or PR is changed." + QuestionGuidance)]
    public async Task<string> Trial(McpServer server, string workspace, string operation,
        string? id = null, int? revision = null, string? planJson = null,
        CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(server, workspace, cancellationToken);
        return await WithServiceAsync(async service =>
        {
            TrialPlan Plan()
            {
                if (planJson is null || System.Text.Encoding.UTF8.GetByteCount(planJson) > 1024 * 1024)
                    throw new McpException("An exact trial plan JSON of at most 1 MiB is required.");
                return JsonSerializer.Deserialize<TrialPlan>(planJson, Contract.Json)
                    ?? throw new McpException("Trial plan cannot be null.");
            }
            string Id() => id ?? throw new McpException("Trial id is required.");
            int Revision() => revision ?? throw new McpException("Exact plan revision is required.");
            object result = operation switch
            {
                "root" => new { schemaVersion = Contract.Version, trialRoot = service.TrialRoot },
                "propose" => service.ProposeTrial(root, Plan()),
                "approve" => throw new McpException(
                    "MCP cannot approve trial execution. A human must review the exact persisted plan and confirm it in an interactive dotnet-nudge trial approve CLI."),
                "execute" => await service.ExecuteTrialAsync(root, Id(), Revision(), cancellationToken),
                "status" => service.TrialStatus(root, Id()),
                "cancel" => service.CancelTrial(root, Id()),
                "cleanup" => await service.CleanupTrialAsync(root, Id()),
                _ => throw new McpException("Unknown trial operation.")
            };
            return JsonSerializer.Serialize(result, Contract.Json);
        });
    }

    [McpServerTool(Name = "nudge_register"), Description("Register an explicit workspace root and queue a durable read-only scan." + QuestionGuidance)]
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
                var inputFingerprint = NudgeState.WorkspaceFingerprint(job.Root);
                var source = new ReleaseMetadata(state, client,
                    fixtureDirectory: Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR"));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                var report = await new NudgeScanner(source).ScanAsync(job.Root, deadline.Token,
                    probeInstalled: true, state: state);
                state.FinishScan(job, owner, report, null, inputFingerprint);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Scan exceeded 90 seconds for workspace {Workspace}", job.Workspace);
                state.FinishScan(job, owner, null, "Read-only scan exceeded 90 seconds.");
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

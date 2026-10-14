using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetNudge.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Data.Sqlite;
using NuGet.Configuration;
using DotnetNudge.Trials;
using System.Runtime.InteropServices;
using Toolchain = DotnetNudge.Toolchains;

namespace DotnetNudge.Integration.Tests;

public sealed class ProtocolTests
{
    sealed class ReleaseFixture : IReleaseMetadata
    {
        public Task<(string Body, Assessment Freshness)> ReadAsync(string source,
            CancellationToken cancellationToken)
        {
            var body = source == ReleaseMetadata.IndexUrl
                ? """{"releases-index":[{"channel-version":"10.0","releases.json":"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"}]}"""
                : source.EndsWith("/10.0/releases.json", StringComparison.Ordinal)
                    ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "releases.json"))
                    : throw new IOException("Unexpected release source: " + source);
            return Task.FromResult((body, Assessment.Confirmed));
        }
    }

    sealed class SdkTrialFixture : IReleaseMetadata
    {
        public bool Security { get; set; }
        public Task<(string Body, Assessment Freshness)> ReadAsync(string source,
            CancellationToken cancellationToken)
        {
            var body = source == ReleaseMetadata.IndexUrl
                ? """{"releases-index":[{"channel-version":"10.0","releases.json":"https://example.invalid/10.0/releases.json"},{"channel-version":"11.0","releases.json":"https://example.invalid/11.0/releases.json"}]}"""
                : source.EndsWith("/11.0/releases.json", StringComparison.Ordinal)
                    ? """{"releases":[{"sdk":{"version":"11.0.100-rc.1"}}]}"""
                    : JsonSerializer.Serialize(new
                    {
                        releases = new[]
                        {
                            new { security = Security, sdk = new { version = "10.0.102" } },
                            new { security = false, sdk = new { version = "10.0.103" } }
                        }
                    });
            return Task.FromResult((body, Assessment.Confirmed));
        }
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ChildProcessInitializesListsToolsScansAndDecides()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var stateDir = Path.Combine(root, "state");
            var releases = Path.Combine(root, "releases");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(releases, "10.0"));
            File.WriteAllText(Path.Combine(workspace, "global.json"),
                """{"sdk":{"version":"10.0.101"}}""");
            File.WriteAllText(Path.Combine(releases, "releases-index.json"),
                """{"releases-index":[{"channel-version":"10.0","releases.json":"https://example.invalid/10.0/releases.json"}]}""");
            File.WriteAllText(Path.Combine(releases, "10.0", "releases.json"),
                """{"releases":[{"sdk":{"version":"10.0.113"}}]}""");
            var engine = Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_ENGINE_PATH");
            var assembly = typeof(DotnetNudge.McpEndpoint).Assembly.Location;
            var stderr = new List<string>();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "nudge-fixture",
                Command = engine ?? "dotnet",
                Arguments = engine is null
                    ? [assembly, "mcp", "--state-dir", stateDir]
                    : ["mcp", "--state-dir", stateDir],
                WorkingDirectory = root,
                EnvironmentVariables = new Dictionary<string, string?> { ["NUDGE_RELEASES_DIR"] = releases },
                StandardErrorLines = line => { lock (stderr) stderr.Add(line); }
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
                    {
                        Roots = [new Root { Uri = new Uri(workspace).AbsoluteUri }]
                    })
                }
            };
            await using var client = await McpClient.CreateAsync(transport, options,
                cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Contains(tools, t => t.Name == "nudge_status");
            Assert.Contains(tools, t => t.Name == "nudge_scan");
            Assert.Contains(tools, t => t.Name == "nudge_context");
            Assert.Contains(tools, t => t.Name == "nudge_decide");
            Assert.Contains(tools, t => t.Name == "nudge_trial");
            Assert.Contains(tools, t => t.Name == "nudge_toolchains");
            Assert.Contains(tools, t => t.Name == "nudge_preferences");
            Assert.Contains(tools, t => t.Name == "nudge_package_assess");
            foreach (var name in new[] { "nudge_status", "nudge_scan", "nudge_context", "nudge_package_context",
                "nudge_package_assess", "nudge_toolchains", "nudge_decide", "nudge_preferences",
                "nudge_trial", "nudge_register" })
            {
                var description = Assert.Single(tools, t => t.Name == name).Description!;
                Assert.Contains("AskUserQuestion (Claude Code)", description);
                Assert.Contains("ask_user (Copilot)", description);
                Assert.Contains("single-select", description);
                Assert.Contains("Not now, not a choice menu in chat", description);
                Assert.Contains("short, friendly, plain-language questions and choices", description);
                Assert.Contains("What's new?, Help me update and Remind me next week", description);
                Assert.Contains("Keep finding IDs, revision tokens, assessment labels and ISO timestamps out of user-facing text", description);
                Assert.Contains("Help me update requests a plan, not approval for changes or trials", description);
                Assert.Contains("Explain the actual changes and ask before executing them", description);
                Assert.Contains("noninteractive, report only; never assume consent", description);
            }
            Assert.Contains("explicit approval", Assert.Single(tools,
                t => t.Name == "nudge_decide").Description!);
            Assert.Contains("in tool arguments, not the question", Assert.Single(tools,
                t => t.Name == "nudge_decide").Description!);
            Assert.Contains("a human must confirm the persisted plan in an interactive CLI",
                Assert.Single(tools, t => t.Name == "nudge_trial").Description!);
            async Task<JsonDocument> Call(string tool, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync(tool, args, cancellationToken: timeout.Token);
                Assert.True(result.IsError != true,
                    string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)) +
                    "\n" + string.Join("\n", stderr.TakeLast(12)));
                return JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            }
            using (var initial = await Call("nudge_status", new() { ["workspace"] = workspace }))
                Assert.Equal("unknown", initial.RootElement.GetProperty("coverage")[0]
                    .GetProperty("assessment").GetString());
            using var scan = await Call("nudge_scan", new() { ["workspace"] = workspace });
            Assert.Contains(scan.RootElement.GetProperty("coverage").EnumerateArray(),
                c => c.GetProperty("check").GetString() == "installed-sdks"
                    && c.GetProperty("assessment").GetString() == "confirmed");
            var finding = scan.RootElement.GetProperty("findings")[0];
            var id = finding.GetProperty("id").GetString()!;
            var revision = finding.GetProperty("revision").GetString()!;
            using (var details = await Call("nudge_context", new()
                { ["workspace"] = workspace, ["findingId"] = id }))
                Assert.Equal(id, details.RootElement.GetProperty("finding")
                    .GetProperty("finding").GetProperty("id").GetString());
            using (var decision = await Call("nudge_decide", new()
                {
                    ["workspace"] = workspace, ["findingId"] = id,
                    ["revision"] = revision, ["action"] = "skip-version"
                }))
                Assert.Equal("skip-version", decision.RootElement.GetProperty("action").GetString());
            using (var status = await Call("nudge_status", new() { ["workspace"] = workspace }))
                Assert.Equal("version-skipped", status.RootElement.GetProperty("findings")[0]
                    .GetProperty("suppression").GetString());
            using (var mute = await Call("nudge_decide", new()
                {
                    ["workspace"] = workspace, ["findingId"] = id,
                    ["revision"] = revision, ["action"] = "disable-category"
                }))
                Assert.Equal("disable-category", mute.RootElement.GetProperty("action").GetString());
            using (var preferences = await Call("nudge_preferences", new() { ["workspace"] = workspace }))
                Assert.Equal("sdk", preferences.RootElement.GetProperty("disabledCategories")[0]
                    .GetProperty("category").GetString());
            using (var enabled = await Call("nudge_preferences", new()
                { ["workspace"] = workspace, ["action"] = "enable-category", ["category"] = "sdk" }))
                Assert.Equal("enable-category", enabled.RootElement.GetProperty("action").GetString());
            using (var preferences = await Call("nudge_preferences", new() { ["workspace"] = workspace }))
                Assert.Empty(preferences.RootElement.GetProperty("disabledCategories").EnumerateArray());
            var doctorStart = new ProcessStartInfo(engine ?? "dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            if (engine is null) doctorStart.ArgumentList.Add(assembly);
            foreach (var argument in new[] { "doctor", "--workspace", workspace,
                "--state-dir", stateDir, "--json" })
                doctorStart.ArgumentList.Add(argument);
            using (var doctor = Process.Start(doctorStart)!)
            {
                var output = doctor.StandardOutput.ReadToEndAsync();
                await doctor.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, doctor.ExitCode);
                using var report = JsonDocument.Parse(await output);
                var note = report.RootElement.GetProperty("note").GetString()!;
                Assert.Contains("interactive human terminal", note, StringComparison.Ordinal);
                Assert.Contains("unsupported on Windows", note, StringComparison.Ordinal);
                Assert.DoesNotContain("await integration", note, StringComparison.Ordinal);
                Assert.Contains(report.RootElement.GetProperty("coverage").EnumerateArray(),
                    c => c.GetProperty("check").GetString() == "installed-sdks");
            }
            var outside = Path.Combine(root, "other");
            Directory.CreateDirectory(outside);
            var denied = await client.CallToolAsync("nudge_scan", new Dictionary<string, object?>
                { ["workspace"] = outside }, cancellationToken: timeout.Token);
            Assert.True(denied.IsError);
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(workspace, "escape");
                Directory.CreateSymbolicLink(link, outside);
                denied = await client.CallToolAsync("nudge_scan", new Dictionary<string, object?>
                    { ["workspace"] = link }, cancellationToken: timeout.Token);
                Assert.True(denied.IsError);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegisteredJobsAreLeasedAndKeepScopedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a");
            var b = Path.Combine(root, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            using var state = new NudgeState(Path.Combine(root, "state"));
            state.Register(a, TimeSpan.FromMinutes(15));
            state.Register(b, TimeSpan.FromMinutes(15));
            state.QueueScan(WorkspaceDiscovery.ScopeId(a));
            state.QueueScan(WorkspaceDiscovery.ScopeId(b));
            var first = state.LeaseScan("worker-a")!;
            var second = state.LeaseScan("worker-b")!;
            Assert.NotEqual(first.Workspace, second.Workspace);
            Assert.Equal(WorkspaceDiscovery.ScopeId(first.Root), first.Workspace);
            Assert.Null(state.LeaseScan("worker-c"));
            state.FinishScan(first, "worker-a", null, "network unavailable");
            Assert.Contains(state.Status(WorkspaceDiscovery.Identities(first.Root).Repository,
                WorkspaceDiscovery.Identities(first.Root).Worktree, first.Workspace).Coverage,
                c => c.Check == "scan-job" && c.Detail.Contains("pending"));
            state.Close(second.Root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void QueuedAndFailedRefreshesSuppressCachedFindingsUntilAccepted()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new Clock();
            using var state = new NudgeState(Path.Combine(root, "state"), clock);
            var inventory = WorkspaceDiscovery.Discover(root);
            var finding = new Finding(Contract.Version, "sdk:test", "sdk:test", "rev1",
                "sdk-servicing", Priority.Low, "10.0.100", "10.0.101",
                new FindingScope(null, null, "global.json"), Assessment.Confirmed,
                [], "review", false, "sdk");
            var report = new ScanReport(Contract.Version, inventory, [finding], [], DateTimeOffset.UtcNow);
            state.Save(report);
            StatusReport Status() => state.Status(inventory.RepositoryId, inventory.WorktreeId,
                inventory.WorkspaceId);
            Assert.True(Assert.Single(Status().Findings).Eligible);
            state.Register(root, TimeSpan.FromMinutes(15));
            state.QueueScan(inventory.WorkspaceId);
            Assert.Equal("scan-pending", Assert.Single(Status().Findings).Suppression);
            var job = state.LeaseScan("worker")!;
            state.FinishScan(job, "worker", null, "failed refresh");
            Assert.Contains(Status().Coverage, c => c.Check == "snapshot" &&
                c.Assessment == Assessment.Stale);
            state.RequestScan(inventory.WorkspaceId);
            Assert.Equal("scan-pending", Assert.Single(Status().Findings).Suppression);
            clock.Now = clock.Now.AddMinutes(2);
            var retry = state.LeaseScan("worker")!;
            state.FinishScan(retry, "worker", report, null);
            Assert.True(Assert.Single(Status().Findings).Eligible);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReconcileQueuesOnlyChangedScopedInputsAndRecoversExpiredJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scope = Path.Combine(root, "workspace");
            Directory.CreateDirectory(scope);
            var stateDir = Path.Combine(root, "state");
            var project = Path.Combine(scope, "App.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"/>""");
            using var first = new NudgeState(stateDir);
            first.Register(scope, TimeSpan.FromMinutes(15));
            first.Reconcile();
            var job = first.LeaseScan("one")!;
            first.FinishScan(job, "one", new ScanReport(Contract.Version,
                WorkspaceDiscovery.Discover(scope), [], [], DateTimeOffset.UtcNow), null);
            first.Reconcile();
            Assert.Null(first.LeaseScan("two"));
            var assets = Path.Combine(scope, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
            File.WriteAllText(assets, "{}");
            using var second = new NudgeState(stateDir);
            second.Reconcile();
            var changed = second.LeaseScan("two")!;
            Assert.Equal(WorkspaceDiscovery.Canonical(scope), changed.Root);
            Assert.Equal(job.Workspace, changed.Workspace);
            Assert.Null(first.LeaseScan("three"));
            var (repo, tree) = WorkspaceDiscovery.Identities(scope);
            Assert.Contains(second.Status(repo, tree, changed.Workspace).Coverage,
                c => c.Check == "snapshot" && c.Assessment == Assessment.Stale);
            second.FinishScan(changed, "two", new ScanReport(Contract.Version,
                WorkspaceDiscovery.Discover(scope), [], [], DateTimeOffset.UtcNow), null);
            File.WriteAllText(Path.Combine(scope, "Directory.Build.props"),
                """<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>""");
            second.Reconcile();
            Assert.NotNull(second.LeaseScan("third"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CompletedExplicitScanSeedsFingerprintWithoutDuplicateWorkerJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-deduplicate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scope = Path.Combine(root, "workspace");
            Directory.CreateDirectory(scope);
            var project = Path.Combine(scope, "App.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"/>""");
            using var state = new NudgeState(Path.Combine(root, "state"));
            state.Register(scope, TimeSpan.FromMinutes(15), "host");
            state.QueueScan(WorkspaceDiscovery.ScopeId(scope));
            var job = state.LeaseScan("cli")!;
            var fingerprint = NudgeState.WorkspaceFingerprint(scope);
            state.FinishScan(job, "cli", new ScanReport(Contract.Version,
                WorkspaceDiscovery.Discover(scope), [], [], DateTimeOffset.UtcNow), null, fingerprint);
            state.QueueScan(WorkspaceDiscovery.ScopeId(scope));
            state.Reconcile();
            Assert.Null(state.LeaseScan("worker"));
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            state.Reconcile();
            Assert.NotNull(state.LeaseScan("worker"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActiveUnchangedScopeRefreshesAfterSixHoursWithoutRetryStorm()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nudge-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scope = Path.Combine(root, "workspace");
            Directory.CreateDirectory(scope);
            File.WriteAllText(Path.Combine(scope, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"/>""");
            var clock = new Clock();
            using var state = new NudgeState(Path.Combine(root, "state"), clock);
            state.Register(scope, TimeSpan.FromDays(2), "host");
            state.QueueScan(WorkspaceDiscovery.ScopeId(scope));
            var baseline = state.LeaseScan("worker")!;
            var inventory = WorkspaceDiscovery.Discover(scope);
            state.FinishScan(baseline, "worker", new ScanReport(Contract.Version, inventory, [], [],
                clock.Now), null, NudgeState.WorkspaceFingerprint(scope));
            clock.Now = clock.Now.AddHours(5);
            state.Reconcile();
            state.QueueScan(inventory.WorkspaceId);
            Assert.Null(state.LeaseScan("worker"));
            clock.Now = clock.Now.AddHours(1).AddSeconds(1);
            state.Reconcile();
            var refresh = state.LeaseScan("worker")!;
            state.Reconcile();
            Assert.Null(state.LeaseScan("another"));
            state.FinishScan(refresh, "worker", null, "metadata unavailable");
            state.Reconcile();
            Assert.Null(state.LeaseScan("another"));
            clock.Now = clock.Now.AddMinutes(2);
            var retry = state.LeaseScan("worker")!;
            state.FinishScan(retry, "worker", new ScanReport(Contract.Version, inventory, [], [],
                clock.Now), null, NudgeState.WorkspaceFingerprint(scope));
            state.Reconcile();
            Assert.Null(state.LeaseScan("worker"));
            clock.Now = clock.Now.AddHours(6).AddSeconds(1);
            state.Reconcile();
            Assert.NotNull(state.LeaseScan("worker"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SecuritySdkAndPreviewTrialRemainDistinctAndCanBeSkipped()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nudge-sdk-trial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "global.json"),
                """{"sdk":{"version":"10.0.101","allowPrerelease":false}}""");
            var metadata = new SdkTrialFixture { Security = true };
            var inventory = WorkspaceDiscovery.Discover(root);
            var (findings, coverage) = await SdkServicing.CheckAsync(inventory, metadata);
            var servicing = Assert.Single(findings, f => f.Kind == "sdk-servicing");
            Assert.Equal(Priority.High, servicing.Priority);
            Assert.Equal("10.0.103", servicing.Candidate);
            Assert.Equal("official-security-release", Assert.Single(servicing.Evidence).Type);
            Assert.Contains(coverage, c => c.Check == "sdk:global.json" &&
                c.Detail.Contains("CVE applicability", StringComparison.Ordinal));
            var preview = Assert.Single(findings, f => f.Kind == "sdk-preview-trial");
            Assert.Equal("11.0.100-rc.1", preview.Candidate);
            Assert.Equal(Assessment.Partial, preview.Assessment);
            Assert.Equal(Priority.Low, preview.Priority);
            Assert.True(preview.RequiresConsent);
            Assert.DoesNotContain(findings, f => f.Kind == "sdk-major-trial");
            using var state = new NudgeState(Path.Combine(root, "state"));
            state.Save(new ScanReport(Contract.Version, inventory, findings, coverage, DateTimeOffset.UtcNow));
            state.Decide(inventory.RepositoryId, inventory.WorkspaceId,
                preview.Id, preview.Revision, DecisionAction.SkipVersion, null);
            Assert.Equal("version-skipped", state.Status(inventory.RepositoryId,
                inventory.WorktreeId, inventory.WorkspaceId).Findings.Single(f =>
                    f.Finding.Id == preview.Id).Suppression);
            metadata.Security = false;
            var (changed, _) = await SdkServicing.CheckAsync(inventory, metadata);
            Assert.NotEqual(servicing.Revision,
                Assert.Single(changed, f => f.Kind == "sdk-servicing").Revision);
            Assert.Equal(Priority.Medium,
                Assert.Single(changed, f => f.Kind == "sdk-servicing").Priority);
            Assert.Contains("false", File.ReadAllText(Path.Combine(root, "global.json")),
                StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExistingV2StateMigratesWithoutDroppingPendingJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stateDir = Path.Combine(root, "state");
            var scope = Path.Combine(root, "scope");
            Directory.CreateDirectory(stateDir);
            Directory.CreateDirectory(scope);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(stateDir, "state.sqlite"), Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_version(version INTEGER NOT NULL);
                    INSERT INTO schema_version VALUES(2);
                    CREATE TABLE workspace_scan_jobs(workspace TEXT PRIMARY KEY,
                      requested INTEGER NOT NULL,status TEXT NOT NULL);
                    INSERT INTO workspace_scan_jobs VALUES($scope,1,'pending');
                    """;
                command.Parameters.AddWithValue("$scope", WorkspaceDiscovery.ScopeId(scope));
                command.ExecuteNonQuery();
            }
            using var state = new NudgeState(stateDir);
            state.Register(scope, TimeSpan.FromMinutes(15));
            Assert.Equal(WorkspaceDiscovery.Canonical(scope), state.LeaseScan("migration")!.Root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentFreshStateInitializationDoesNotRaceMigrations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stateDir = Path.Combine(root, "state");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            async Task<(int ExitCode, string Error)> Start()
            {
                using var process = Process.Start(new ProcessStartInfo("dotnet")
                {
                    ArgumentList =
                    {
                        typeof(DotnetNudge.McpEndpoint).Assembly.Location,
                        "status", "--workspace", root, "--state-dir", stateDir, "--json"
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                })!;
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw;
                }
                using var response = JsonDocument.Parse(await output);
                Assert.Equal(Contract.Version, response.RootElement.GetProperty("schemaVersion").GetInt32());
                return (process.ExitCode, await error);
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Start()));
            Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Error));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OversizedCachedSnapshotIsRejectedBeforeJsonMaterialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-oversized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stateDir = Path.Combine(root, "state");
            using var state = new NudgeState(stateDir);
            var (repo, tree) = WorkspaceDiscovery.Identities(root);
            var workspace = WorkspaceDiscovery.ScopeId(root);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(stateDir, "state.sqlite"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO workspace_snapshots(workspace,worktree,repository,report,scanned)
                VALUES($workspace,$tree,$repo,CAST(zeroblob(33554433) AS TEXT),1)
                """;
            command.Parameters.AddWithValue("$workspace", workspace);
            command.Parameters.AddWithValue("$tree", tree);
            command.Parameters.AddWithValue("$repo", repo);
            command.ExecuteNonQuery();
            Assert.Throws<InvalidDataException>(() => state.Status(repo, tree, workspace));
            Assert.Throws<InvalidDataException>(() =>
                state.Decide(repo, workspace, "missing", "r1", DecisionAction.Snooze,
                    DateTimeOffset.UtcNow.AddDays(1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedReadOnlyJobsStopRetryingAndExposeDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new Clock();
            using var state = new NudgeState(Path.Combine(root, "state"), clock);
            state.Register(root, TimeSpan.FromHours(1));
            var workspace = WorkspaceDiscovery.ScopeId(root);
            state.QueueScan(workspace);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var job = state.LeaseScan("worker")!;
                Assert.Equal(attempt, job.Attempts);
                state.FinishScan(job, "worker", null, "fixture failure");
                clock.Now = clock.Now.AddMinutes(2);
            }
            Assert.Null(state.LeaseScan("worker"));
            var (repo, tree) = WorkspaceDiscovery.Identities(root);
            Assert.Contains(state.Status(repo, tree, workspace).Coverage,
                c => c.Check == "scan-job" && c.Assessment == Assessment.Blocked &&
                    c.Detail.Contains("fixture failure"));
            state.RequestScan(workspace);
            Assert.Equal(1, state.LeaseScan("retry")!.Attempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ClosingOneHookSessionKeepsOtherWorkspaceSessionActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-presence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var state = new NudgeState(Path.Combine(root, "state"));
            state.Register(root, TimeSpan.FromMinutes(15), "copilot:one");
            state.Register(root, TimeSpan.FromMinutes(15), "copilot:two");
            state.Close(root, "copilot:one");
            state.QueueScan(WorkspaceDiscovery.ScopeId(root));
            Assert.NotNull(state.LeaseScan("worker"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnsupportedStateEmitsBoundedJsonErrorInsteadOfCrashing()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(stateDir, "state.sqlite"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(99)";
                command.ExecuteNonQuery();
            }
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    typeof(DotnetNudge.McpEndpoint).Assembly.Location,
                    "status", "--workspace", root, "--state-dir", stateDir, "--json"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("CLI did not exit after rejecting an unsupported state schema.");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(process.ExitCode == 2,
                $"Unexpected exit {process.ExitCode}; stderr: {stderr}; stdout: {stdout}");
            using var output = JsonDocument.Parse(stdout);
            Assert.Equal(1, output.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Contains("Unsupported", output.RootElement.GetProperty("error").GetString());
            Assert.Contains("Unsupported", stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProbeRejectsScriptMasqueradingAsDotnet()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "nudge-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var old = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            File.WriteAllText(Path.Combine(root, "dotnet"), "#!/bin/sh\nexit 0\n");
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
            var snapshot = await DotnetProbes.ReadAsync(WorkspaceDiscovery.Discover(root));
            Assert.Empty(snapshot.EffectiveSdks);
            Assert.Contains(snapshot.Coverage,
                c => c.Check == "dotnet-probe" && c.Assessment == Assessment.Blocked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", old);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeLookingButUnlaunchableDotnetIsBlockedCoverage()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-unlaunchable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            var executable = Path.Combine(root, "dotnet");
            File.WriteAllBytes(executable, OperatingSystem.IsMacOS()
                ? [0xfe, 0xed, 0xfa, 0xcf] : [0x7f, (byte)'E', (byte)'L', (byte)'F']);
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
            var inventory = WorkspaceDiscovery.Discover(root);
            var denied = await DotnetProbes.ReadAsync(inventory);
            Assert.Contains(denied.Coverage, c => c.Check == "dotnet-probe" &&
                c.Assessment == Assessment.Blocked);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var failedLaunch = await DotnetProbes.ReadAsync(inventory);
            Assert.Contains(failedLaunch.Coverage, c => c.Check == "effective-sdk:." &&
                c.Assessment == Assessment.Blocked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoredGraphBaselineTransitionAndExactGuidanceHandle()
    {
        var root = Path.Combine(Path.GetTempPath(), "nudge-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
                var project = Path.Combine(root, "App.csproj");
                File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
                var cache = Path.Combine(root, "cache");
                var package = Path.Combine(cache, "acme.parent", "1.0.0");
                var skill = Path.Combine(package, "skills", "review");
                Directory.CreateDirectory(skill);
                File.WriteAllText(Path.Combine(package, "acme.parent.nuspec"),
                    """<package><metadata><id>Acme.Parent</id><version>1.0.0</version></metadata></package>""");
                File.WriteAllText(Path.Combine(skill, "SKILL.md"),
                    "---\nname: review\ndescription: Read this package\n---\n[Details](details.md)\n");
                File.WriteAllText(Path.Combine(skill, "details.md"), "Local guidance reference.");
                var child = Path.Combine(cache, "acme.child", "1.2.0");
                var childSkill = Path.Combine(child, "skills", "child");
                Directory.CreateDirectory(childSkill);
                File.WriteAllText(Path.Combine(child, "acme.child.nuspec"),
                    """<package><metadata><id>Acme.Child</id><version>1.2.0</version></metadata></package>""");
                File.WriteAllText(Path.Combine(childSkill, "SKILL.md"),
                    "---\nname: child\ndescription: Transitive package guidance\n---\nChild guidance.\n");
                var assets = Path.Combine(root, "obj", "project.assets.json");
                Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
                var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                    .Replace("{CACHE}", cache.Replace('\\', '/'), StringComparison.Ordinal)
                    .Replace("{PROJECT}", project.Replace('\\', '/'), StringComparison.Ordinal)
                    .Replace("{OUTPUT}", Path.GetDirectoryName(assets)!.Replace('\\', '/'), StringComparison.Ordinal);
                File.WriteAllText(assets, fixture);
                var stateDir = Path.Combine(root, "state");
                var inventory = WorkspaceDiscovery.Discover(root);
                using var state = new NudgeState(stateDir);
                using var service = new NudgeService(state);
                var first = await service.ScanAsync(root);
                Assert.DoesNotContain(first.Findings, f => f.Category == "package");
                Assert.Contains(first.Coverage, c => c.Check == "package-graph:App.csproj" &&
                    c.Assessment == Assessment.Confirmed);
                await Assert.ThrowsAsync<ArgumentException>(() => service.AssessPackageAsync(root,
                    "App.csproj", "net8.0", null, "Acme.Parent",
                    new HashSet<string> { "not-approved" }, acquisitionApproved: false));
                await Assert.ThrowsAsync<ArgumentException>(() => service.AssessPackageAsync(root,
                    "App.csproj", "net8.0", null, "Acme.Parent",
                    new HashSet<string> { "not-configured" }, acquisitionApproved: true));
                var listed = await service.PackageContextAsync(root);
                Assert.Equal(4, listed.Skills.Count);
                Assert.Contains(listed.Skills, s => s.Framework == "net10.0" &&
                    s.RuntimeIdentifier == "osx-arm64");
                var childHandle = listed.Skills.Single(s =>
                    s.Framework == "net8.0" && s.PackageId == "Acme.Child").Handle;
                Assert.Contains("Child guidance.", (await service.PackageContextAsync(root, childHandle)).Entry!.Text);
                var handle = listed.Skills.Single(s =>
                    s.Framework == "net8.0" && s.PackageId == "Acme.Parent").Handle;
                var selected = await service.PackageContextAsync(root, handle);
                Assert.Equal(handle, selected.Selected!.Handle);
                Assert.Contains("Details", selected.Entry!.Text);
                Assert.Equal("Local guidance reference.", Assert.Single(selected.References).Text);
                if (!OperatingSystem.IsWindows())
                {
                    var originalInspector = Environment.GetEnvironmentVariable("NUDGE_INSPECTOR_PATH");
                    var script = Path.Combine(root, "fake-inspector");
                    var marker = Path.Combine(root, "inspector-ran");
                    try
                    {
                        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\necho 0.26.0\n");
                        Environment.SetEnvironmentVariable("NUDGE_INSPECTOR_PATH", script);
                        var fallback = await service.PackageContextAsync(root, handle);
                        Assert.Equal(Assessment.Partial, fallback.Assessment);
                        Assert.Contains("not an installed native executable", fallback.Diagnostic);
                        Assert.False(File.Exists(marker));
                    }
                    finally { Environment.SetEnvironmentVariable("NUDGE_INSPECTOR_PATH", originalInspector); }
                }
                var engine = Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_ENGINE_PATH");
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "guidance-fixture",
                    Command = engine ?? "dotnet",
                    Arguments = engine is null
                        ? [typeof(DotnetNudge.McpEndpoint).Assembly.Location, "mcp", "--state-dir", stateDir]
                        : ["mcp", "--state-dir", stateDir],
                    WorkingDirectory = root
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
                {
                    Assert.Contains(await client.ListToolsAsync(cancellationToken: timeout.Token),
                        tool => tool.Name == "nudge_package_context");
                    var call = await client.CallToolAsync("nudge_package_context",
                        new Dictionary<string, object?> { ["workspace"] = root, ["handle"] = handle },
                        cancellationToken: timeout.Token);
                    Assert.True(call.IsError != true);
                    using var payload = JsonDocument.Parse(Assert.Single(
                        call.Content.OfType<TextContentBlock>()).Text);
                    Assert.Equal("Local guidance reference.", payload.RootElement
                        .GetProperty("references")[0].GetProperty("text").GetString());
                }
                File.AppendAllText(assets, "\n");
                var refreshed = await service.PackageContextAsync(root);
                Assert.NotEqual(handle, refreshed.Skills.Single(s =>
                    s.Framework == "net8.0" && s.PackageId == "Acme.Parent").Handle);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.PackageContextAsync(root, handle));
                File.WriteAllText(assets, fixture.Replace("1.0.0", "1.0.1", StringComparison.Ordinal));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.PackageContextAsync(root, handle));
                var processStart = new ProcessStartInfo(engine ?? "dotnet")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, WorkingDirectory = root
                };
                if (engine is null)
                    processStart.ArgumentList.Add(typeof(DotnetNudge.McpEndpoint).Assembly.Location);
                foreach (var arg in new[] { "scan", "--workspace", root, "--state-dir", stateDir, "--json" })
                    processStart.ArgumentList.Add(arg);
                using var process = Process.Start(processStart)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var scanTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(scanTimeout.Token);
                Assert.True(process.ExitCode == 0, await error);
                using var changed = JsonDocument.Parse(await output);
                Assert.Contains(changed.RootElement.GetProperty("findings").EnumerateArray(), f =>
                    f.GetProperty("kind").GetString() == "restored-graph-change" &&
                    f.GetProperty("scope").GetProperty("project").GetString() == "App.csproj" &&
                    f.GetProperty("scope").GetProperty("framework").GetString() == "net8.0" &&
                    f.GetProperty("current").GetString() == "1.0.0" &&
                    f.GetProperty("candidate").GetString() == "1.0.1");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PackageScopeLimitReportsUnconsideredDirectScopes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-package-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "App.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
            var feed = Path.Combine(root, "feed");
            Directory.CreateDirectory(feed);
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
                <configuration><packageSources><clear /><add key="private" value="{feed}" /></packageSources>
                <packageSourceMapping><packageSource key="private"><package pattern="Acme.*" /></packageSource></packageSourceMapping></configuration>
                """);
            var settings = Settings.LoadDefaultSettings(root);
            Assert.Contains(new PackageSourceProvider(settings).LoadPackageSources(),
                source => source.Name == "private");
            var assets = Path.Combine(root, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                .Replace("{CACHE}", Path.Combine(root, "cache").Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{PROJECT}", project.Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{OUTPUT}", Path.GetDirectoryName(assets)!.Replace('\\', '/'), StringComparison.Ordinal);
            var graph = JsonNode.Parse(fixture)!;
            foreach (var name in new[] { "One", "Two", "Three", "Four" })
            {
                var id = "Acme." + name;
                graph["targets"]!["net8.0"]![id + "/1.0.0"] = JsonNode.Parse("""{"type":"package"}""");
                graph["libraries"]![id + "/1.0.0"] = JsonNode.Parse(
                    """{"sha512":"","type":"package","path":"acme.parent/1.0.0","files":[]}""");
                graph["project"]!["frameworks"]!["net8.0"]!["dependencies"]![id] =
                    JsonNode.Parse("""{"target":"Package","version":"[1.0.0, )"}""");
                ((JsonArray)graph["projectFileDependencyGroups"]!["net8.0"]!).Add(id + " >= 1.0.0");
            }
            File.WriteAllText(assets, graph.ToJsonString());
            using var state = new NudgeState(Path.Combine(root, "state"));
            var result = await PackageIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                state, CancellationToken.None);
            Assert.Contains(result.Coverage, c => c.Check == "package-check-limit:App.csproj" &&
                c.Assessment == Assessment.Partial && c.Detail.Contains("2 mapped direct package scopes"));
            Assert.Contains(result.Coverage, c => c.Check == "package-scan-limit" &&
                c.Assessment == Assessment.Partial);
            Assert.Equal(4, result.Coverage.Count(c => c.Check.StartsWith("package-feed:", StringComparison.Ordinal)));
            Assert.Contains(result.Coverage, c =>
                c.Check == "package-audit:App.csproj:net8.0:any:Acme.Four" &&
                c.Assessment == Assessment.Partial);
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
                <configuration><packageSources><clear /><add key="private" value="{feed}" /></packageSources>
                <packageSourceMapping><packageSource key="private"><package pattern="Acme.Four" /></packageSource></packageSourceMapping></configuration>
                """);
            File.WriteAllText(assets, graph.ToJsonString());
            var mappedLast = await PackageIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                state, CancellationToken.None);
            Assert.Contains(mappedLast.Coverage, c => c.Check.EndsWith(":Acme.Four", StringComparison.Ordinal) &&
                c.Check.StartsWith("package-feed:", StringComparison.Ordinal));
            Assert.DoesNotContain(mappedLast.Coverage, c => c.Check == "package-check-limit:App.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedScanDoesNotConsumeRestoredGraphTransition()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-graph-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "App.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"/>""");
            var assets = Path.Combine(root, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                .Replace("{CACHE}", Path.Combine(root, "cache").Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{PROJECT}", project.Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{OUTPUT}", Path.GetDirectoryName(assets)!.Replace('\\', '/'), StringComparison.Ordinal);
            File.WriteAllText(assets, fixture);
            var clock = new Clock();
            using var state = new NudgeState(Path.Combine(root, "state"), clock);
            var inventory = WorkspaceDiscovery.Discover(root);
            state.Register(root, TimeSpan.FromMinutes(15));
            state.RequestScan(inventory.WorkspaceId);
            var firstJob = state.LeaseScan("worker")!;
            var baseline = await PackageIntegration.ScanAsync(inventory, state, CancellationToken.None);
            Assert.Empty(baseline.Findings);
            Assert.Null(state.GraphSnapshot(inventory.WorkspaceId, "App.csproj"));
            state.FinishScan(firstJob, "worker", new ScanReport(Contract.Version, inventory,
                baseline.Findings, baseline.Coverage, clock.Now), null);
            var original = state.GraphSnapshot(inventory.WorkspaceId, "App.csproj");
            Assert.NotNull(original);

            File.WriteAllText(assets, fixture.Replace("1.0.0", "1.0.1", StringComparison.Ordinal));
            state.RequestScan(inventory.WorkspaceId);
            var job = state.LeaseScan("worker")!;
            var changed = await PackageIntegration.ScanAsync(inventory, state, CancellationToken.None);
            Assert.Contains(changed.Findings, finding => finding.Kind == "restored-graph-change");
            Assert.Equal(original, state.GraphSnapshot(inventory.WorkspaceId, "App.csproj"));
            state.FinishScan(job, "worker", null, "cancelled");
            Assert.Equal(original, state.GraphSnapshot(inventory.WorkspaceId, "App.csproj"));
            clock.Now = clock.Now.AddMinutes(2);
            var retry = state.LeaseScan("worker")!;
            var replayed = await PackageIntegration.ScanAsync(inventory, state, CancellationToken.None);
            Assert.Equal(changed.Findings.Select(f => f.Revision), replayed.Findings.Select(f => f.Revision));
            state.FinishScan(retry, "worker", new ScanReport(Contract.Version, inventory,
                replayed.Findings, replayed.Coverage, clock.Now), null);
            Assert.NotEqual(original, state.GraphSnapshot(inventory.WorkspaceId, "App.csproj"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WorkloadRecommendationsRespectPinsInventoryBandsAndEvidence()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-workload-findings-" + Guid.NewGuid().ToString("N"));
        var evidenceFile = Path.Combine(root, ".dotnet-nudge", "toolchains.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidenceFile)!);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var evidence = new Toolchain.Evidence("fixture:workload-set", now);
            Toolchain.WorkloadSetCandidate Set(string version, string band = "10.0.100") =>
                new(band, version, null, new("""{"android":"35.0.50/9.0.100"}""", evidence));
            var installed = new Toolchain.InstalledWorkloadInventory(
                Toolchain.WorkloadInstallMode.LooseManifests, null,
                new Dictionary<string, Toolchain.InstalledManifestInfo>
                { ["android"] = new("35.0.49", "9.0.100") },
                new HashSet<string>(), evidence);
            var request = new Toolchain.WorkloadAnalysisRequest("10.0.101", "10.0.102", false,
                [Set("10.0.102"), Set("10.0.105"), Set("10.0.106-preview.1"), Set("10.0.501", "10.0.500")],
                new HashSet<string> { "android" },
                new Dictionary<string, Toolchain.VersionedJsonDocument>
                { ["android"] = new("""{"android":{}}""", evidence, "9.0.100", "35.0.50") },
                new([], ["net10.0-android"]), installed, new(now, TimeSpan.FromDays(1)));
            var workspace = WorkspaceDiscovery.Discover(root);
            async Task<ToolchainScanResult> Analyze(Toolchain.WorkloadAnalysisRequest value,
                string sdk = "10.0.101")
            {
                File.WriteAllText(evidenceFile, JsonSerializer.Serialize(
                    new ToolchainEvidenceBundle(value, null, null, null, null), Contract.Json));
                return await ToolchainIntegration.ScanAsync(workspace,
                    new("dotnet", new Dictionary<string, string> { ["."] = sdk }, null, null, null, []),
                    new ReleaseFixture(), CancellationToken.None);
            }
            var result = await Analyze(request);
            var update = Assert.Single(result.Findings, f => f.Kind == "workload-set-update");
            Assert.Equal("10.0.102", update.Current);
            Assert.Equal("10.0.105", update.Candidate);
            Assert.Equal("10.0.100", update.Scope.SdkScope);
            Assert.Equal("android", Assert.Single(result.Findings, f => f.Kind == "workload-missing").Candidate);
            var manifest = Assert.Single(result.Findings, f => f.Kind == "workload-manifest-difference");
            Assert.Equal("35.0.49/9.0.100", manifest.Current);
            Assert.Equal("35.0.50/9.0.100", manifest.Candidate);
            Assert.DoesNotContain(result.Findings, f => f.Kind == "workload-set-difference");
            Assert.All(result.Findings, f =>
            {
                Assert.True(f.RequiresConsent);
                Assert.Equal(Assessment.Partial, f.Assessment);
                Assert.Equal("workload", f.Category);
            });
            using var state = new NudgeState(Path.Combine(root, "state"));
            state.Save(new(Contract.Version, workspace, result.Findings, result.Coverage, now));
            Assert.Equal(3, state.Status(workspace.RepositoryId, workspace.WorktreeId, workspace.WorkspaceId).Findings.Count);
            var correct = installed with
            {
                Mode = Toolchain.WorkloadInstallMode.WorkloadSet,
                WorkloadSetVersion = "10.0.102",
                Workloads = new HashSet<string> { "android" },
                Manifests = new Dictionary<string, Toolchain.InstalledManifestInfo>
                { ["android"] = new("35.0.50", "9.0.100") }
            };
            Assert.Empty((await Analyze(request with
            {
                InstalledInventory = correct, AvailableSets = [Set("10.0.102")]
            })).Findings);
            Assert.Single((await Analyze(request with { InstalledInventory = null })).Findings);
            Assert.Empty((await Analyze(request, "10.0.401")).Findings);
            Assert.Empty((await Analyze(request with { PinnedWorkloadSetVersion = "10.0.999" })).Findings);
            Assert.Empty((await Analyze(request with
            {
                InstalledInventory = installed with { Evidence = evidence with { RetrievedAt = now.AddDays(-2) } }
            })).Findings);
            var historical = request with
            {
                Freshness = new(now.AddDays(-2), TimeSpan.FromDays(1)),
                InstalledInventory = installed with { Evidence = evidence with { RetrievedAt = now.AddDays(-2) } }
            };
            Assert.Empty((await Analyze(historical)).Findings);
            var staleSet = Set("10.0.105");
            staleSet = staleSet with
            {
                WorkloadSetManifest = staleSet.WorkloadSetManifest with
                { Evidence = evidence with { RetrievedAt = now.AddDays(-2) } }
            };
            Assert.Empty((await Analyze(request with { AvailableSets = [Set("10.0.102"), staleSet] })).Findings);
            var unknownManifest = await Analyze(request with
            {
                InstalledInventory = installed with
                { Manifests = new Dictionary<string, Toolchain.InstalledManifestInfo>() }
            });
            Assert.DoesNotContain(unknownManifest.Findings, f => f.Kind == "workload-manifest-difference");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScopedToolchainEvidenceKeepsLocalDeploymentAndHostFindingsDistinct()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-toolchains-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"/>""");
            File.WriteAllText(Path.Combine(root, "global.json"), """{"sdk":{"version":"10.0.101","workloadVersion":"10.0.103"}}""");
            var evidenceFile = Path.Combine(root, ".dotnet-nudge", "toolchains.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidenceFile)!);
            var now = DateTimeOffset.UtcNow;
            var evidence = new Toolchain.Evidence("local-fixture", now);
            string Fixture(string name) => File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            var inventory = Toolchain.InstalledInventoryReader.Parse(
                Fixture("installed-workloads.json"), evidence).Value!;
            var request = new Toolchain.WorkloadAnalysisRequest(
                "10.0.101", null, false,
                [new("10.0.100", "10.0.103", "10.103.0",
                    new(Fixture("workloadset.json"), evidence))],
                new HashSet<string> { "microsoft.net.sdk.android" },
                new Dictionary<string, Toolchain.VersionedJsonDocument>
                {
                    ["microsoft.net.sdk.android"] = new(
                        Fixture("android-dependencies.json"), evidence, "9.0.100", "35.0.50")
                },
                new(["10.0.10"], ["net10.0-android"]), inventory,
                new(now, TimeSpan.FromDays(1)));
            var host = new Toolchain.HostToolInventory(null, "22.0", null, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(), evidence);
            var bundle = new ToolchainEvidenceBundle(request, host, [], null, null,
                new(Toolchain.RuntimeDeploymentKind.SelfContained,
                    EmbeddedRuntimeVersions: new Dictionary<string, string>
                    { ["Microsoft.NETCore.App"] = "10.0.1" }, Evidence: evidence));
            File.WriteAllText(evidenceFile, JsonSerializer.Serialize(bundle, Contract.Json));
            var observed = new ProbeSnapshot("dotnet",
                new Dictionary<string, string> { ["."] = "10.0.101" },
                "10.0.101", "Microsoft.NETCore.App 10.0.1 [/test]", null, []);
            var analyzed = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                observed, new ReleaseFixture(), CancellationToken.None);
            Assert.Contains(analyzed.Findings, f => f.Kind == "workload-set-difference");
            Assert.Contains(analyzed.Findings, f => f.Kind == "host-tool-incompatibility");
            Assert.True(analyzed.Findings.Any(f => f.Kind == "local-runtime-security-servicing"),
                string.Join("; ", analyzed.Coverage.Where(c => c.Check.StartsWith("runtime:", StringComparison.Ordinal))
                    .Select(c => c.Detail)));
            Assert.Contains(analyzed.Findings, f => f.Kind == "deployment-runtime-security-servicing");
            Assert.Contains(analyzed.Coverage, c => c.Check == "runtime:10.0" &&
                c.Detail.Contains("SelfContained deployment evidence analyzed", StringComparison.Ordinal));
            Assert.Contains(analyzed.Coverage, c => c.Check == "global-json:global.json");
            Assert.NotEmpty(analyzed.Runtimes);
            var containerBundle = bundle with { Deployment = new(
                Toolchain.RuntimeDeploymentKind.Container,
                DeclaredContainerRuntimeVersions: new Dictionary<string, string>
                { ["Microsoft.NETCore.App"] = "10.0.1" }, Evidence: evidence) };
            File.WriteAllText(evidenceFile, JsonSerializer.Serialize(containerBundle, Contract.Json));
            var container = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                observed, new ReleaseFixture(), CancellationToken.None);
            Assert.Contains(container.Coverage, c => c.Check == "runtime:10.0" &&
                c.Detail.Contains("Container deployment evidence analyzed", StringComparison.Ordinal));
            File.Delete(evidenceFile);
            var unknown = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                observed, new ReleaseFixture(), CancellationToken.None);
            Assert.Contains(unknown.Coverage, c => c.Check == "runtime:10.0" &&
                c.Detail.Contains("Deployment runtime remains unknown", StringComparison.Ordinal));
            var valid = JsonNode.Parse(JsonSerializer.Serialize(bundle, Contract.Json))!;
            foreach (var (path, label) in new[]
            {
                ("workload.availableSets", "workload.availableSets"),
                ("workload.tupleContext.targetFrameworks", "workload.tupleContext"),
                ("workload.installedInventory.evidence", "workload.installedInventory.evidence"),
                ("hostInventory.windowsSdkVersions", "hostInventory.windowsSdkVersions"),
                ("deployment.embeddedRuntimeVersions", "deployment.embeddedRuntimeVersions")
            })
            {
                var malformed = valid.DeepClone();
                var segments = path.Split('.');
                JsonNode parent = malformed;
                foreach (var segment in segments.SkipLast(1)) parent = parent[segment]!;
                parent[segments[^1]] = null;
                File.WriteAllText(evidenceFile, malformed.ToJsonString());
                var blocked = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                    observed, new ReleaseFixture(), CancellationToken.None);
                Assert.Contains(blocked.Coverage, c => c.Check == "toolchain-evidence" &&
                    c.Assessment == Assessment.Blocked &&
                    c.Detail.Contains(label, StringComparison.Ordinal));
            }
            var badDependency = valid.DeepClone();
            badDependency["workload"]!["dependencyDocumentsByWorkload"]!["microsoft.net.sdk.android"] = null;
            File.WriteAllText(evidenceFile, badDependency.ToJsonString());
            var missingDocument = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                observed, new ReleaseFixture(), CancellationToken.None);
            Assert.Contains(missingDocument.Coverage, c => c.Check == "toolchain-evidence" &&
                c.Assessment == Assessment.Blocked &&
                c.Detail.Contains("dependencyDocumentsByWorkload", StringComparison.Ordinal));
            var badVersion = valid.DeepClone();
            badVersion["deployment"]!["embeddedRuntimeVersions"]!["Microsoft.NETCore.App"] = null;
            File.WriteAllText(evidenceFile, badVersion.ToJsonString());
            var missingVersion = await ToolchainIntegration.ScanAsync(WorkspaceDiscovery.Discover(root),
                observed, new ReleaseFixture(), CancellationToken.None);
            Assert.Contains(missingVersion.Coverage, c => c.Check == "toolchain-evidence" &&
                c.Assessment == Assessment.Blocked &&
                c.Detail.Contains("embeddedRuntimeVersions", StringComparison.Ordinal));
            File.WriteAllText(evidenceFile, JsonSerializer.Serialize(bundle, Contract.Json));
            using var state = new NudgeState(Path.Combine(root, "state"));
            var scan = await new NudgeScanner(new ReleaseFixture()).ScanAsync(root,
                state: state, probeInstalled: false);
            state.Save(scan);
            using var service = new NudgeService(state);
            Assert.Contains(service.Status(root).Findings,
                f => f.Finding.Kind == "workload-set-difference");
            Assert.Contains(service.Status(root).Coverage,
                c => c.Check == "host-compatibility");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task McpRefusesApprovalButExecutesHumanApprovedIsolatedTrial()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_TEST_ROOT")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nudge-approved-trial-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(repo);
        try
        {
            async Task<string> Git(params string[] args)
            {
                var start = new ProcessStartInfo("git")
                {
                    WorkingDirectory = repo, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false
                };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await error);
                return (await output).Trim();
            }
            var project = Path.Combine(repo, "App.csproj");
            var original = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Acme.Parent" Version="1.0.0" /></ItemGroup></Project>""";
            File.WriteAllText(project, original);
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
            await Git("init", "-q");
            await Git("add", ".");
            await Git("-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture",
                "commit", "-qm", "fixture");
            var commit = await Git("rev-parse", "HEAD");
            var prepared = new[] { Path.Combine(root, "sdk-baseline"), Path.Combine(root, "sdk-candidate") };
            for (var i = 0; i < prepared.Length; i++)
            {
                Directory.CreateDirectory(prepared[i]);
                var version = i == 0 ? "10.0.100" : "10.0.401";
                var executable = Path.Combine(prepared[i], "dotnet");
                File.WriteAllText(executable, $"""
                    #!/bin/sh
                    case "$1" in
                      --version) echo {version};;
                      build) mkdir -p bin; printf '{version}' > bin/App.dll;;
                      test) echo 'Total tests: 1';;
                      *) echo 'fixture restore';;
                    esac
                    """);
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            var trialRoot = Path.Combine(stateDir, "trials");
            var plan = new TrialPlan("integration", 1, repo, commit, trialRoot,
                ["App.csproj"],
                new("10.0.100", Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_ENGINE_RID")
                    ?? RuntimeInformation.RuntimeIdentifier, prepared[0]),
                new("10.0.401", Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_ENGINE_RID")
                    ?? RuntimeInformation.RuntimeIdentifier, prepared[1]),
                [new("App.csproj", "Acme.Parent", "1.0.0", "2.0.0")],
                [new("restore", ["App.csproj"]), new("build", ["App.csproj"]),
                    new("test", ["App.csproj", "--no-build"])],
                new("artifacts/logs", "artifacts/changes.diff"),
                new(50_000_000, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15)), false);
            var engine = Environment.GetEnvironmentVariable("NUDGE_INTEGRATION_ENGINE_PATH");
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "trial-integration",
                Command = engine ?? "dotnet",
                Arguments = engine is null
                    ? [typeof(DotnetNudge.McpEndpoint).Assembly.Location,
                        "mcp", "--state-dir", stateDir]
                    : ["mcp", "--state-dir", stateDir],
                WorkingDirectory = root
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var client = await McpClient.CreateAsync(transport,
                cancellationToken: timeout.Token))
            {
                Assert.Contains(await client.ListToolsAsync(cancellationToken: timeout.Token),
                    t => t.Name == "nudge_trial");
                var toolchains = await client.CallToolAsync("nudge_toolchains",
                    new Dictionary<string, object?> { ["workspace"] = repo },
                    cancellationToken: timeout.Token);
                Assert.True(toolchains.IsError != true);
                using (var toolReport = JsonDocument.Parse(
                    Assert.Single(toolchains.Content.OfType<TextContentBlock>()).Text))
                    Assert.Contains(toolReport.RootElement.GetProperty("coverage").EnumerateArray(),
                        c => c.GetProperty("check").GetString() == "workload-set");
                async Task<JsonDocument> Trial(string operation, string? id = null,
                    int? revision = null, string? planJson = null)
                {
                    var call = await client.CallToolAsync("nudge_trial",
                        new Dictionary<string, object?>
                        {
                            ["workspace"] = repo, ["operation"] = operation,
                            ["id"] = id, ["revision"] = revision, ["planJson"] = planJson
                        }, cancellationToken: timeout.Token);
                    Assert.True(call.IsError != true, string.Join("; ",
                        call.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                    return JsonDocument.Parse(Assert.Single(call.Content.OfType<TextContentBlock>()).Text);
                }
                using (var proposed = await Trial("propose", planJson: JsonSerializer.Serialize(plan, Contract.Json)))
                {
                    Assert.Equal("proposed", proposed.RootElement.GetProperty("phase").GetString());
                    Assert.False(proposed.RootElement.TryGetProperty("ownerToken", out _));
                }
                var denied = await client.CallToolAsync("nudge_trial", new Dictionary<string, object?>
                    { ["workspace"] = repo, ["operation"] = "approve", ["id"] = "integration",
                        ["revision"] = 1 }, cancellationToken: timeout.Token);
                Assert.True(denied.IsError);
                using (var stillProposed = await Trial("status", "integration"))
                    Assert.Equal("proposed", stillProposed.RootElement.GetProperty("phase").GetString());
                var approvalStart = new ProcessStartInfo(engine ?? "dotnet")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root
                };
                if (engine is null)
                    approvalStart.ArgumentList.Add(typeof(DotnetNudge.McpEndpoint).Assembly.Location);
                foreach (var argument in new[] { "trial", "approve", "--workspace", repo,
                    "--state-dir", stateDir, "--id", "integration", "--revision", "1", "--json" })
                    approvalStart.ArgumentList.Add(argument);
                using (var approval = Process.Start(approvalStart)!)
                {
                    var output = approval.StandardOutput.ReadToEndAsync();
                    var error = approval.StandardError.ReadToEndAsync();
                    await approval.WaitForExitAsync(timeout.Token);
                    Assert.Equal(2, approval.ExitCode);
                    Assert.Contains("interactive terminal", await error);
                    using var rejected = JsonDocument.Parse(await output);
                    Assert.Equal(1, rejected.RootElement.GetProperty("schemaVersion").GetInt32());
                }
                var trustedRunner = new TrialRunner(new TrialStore(trialRoot));
                Assert.Throws<TrialException>(() => trustedRunner.Approve("integration", 1,
                    new string('0', 64)));
                var approvedRecord = trustedRunner.Approve("integration", 1,
                    TrialRunner.Digest(plan));
                Assert.Equal(TrialPhase.Approved, approvedRecord.Phase);
                using (var executed = await Trial("execute", "integration", 1))
                {
                    Assert.True(executed.RootElement.GetProperty("phase").GetString() == "ready-for-review",
                        executed.RootElement.GetProperty("blockReason").GetString());
                    var report = executed.RootElement.GetProperty("report");
                    Assert.Equal(1, report.GetProperty("baseline").GetProperty("testCount").GetInt32());
                    Assert.Equal(1, report.GetProperty("candidate").GetProperty("testCount").GetInt32());
                    Assert.Contains("2.0.0", File.ReadAllText(
                        report.GetProperty("diffPath").GetString()!));
                }
                var cleanup = await client.CallToolAsync("nudge_trial", new Dictionary<string, object?>
                    { ["workspace"] = repo, ["operation"] = "cleanup", ["id"] = "integration" },
                    cancellationToken: timeout.Token);
                Assert.True(cleanup.IsError);
                Assert.Equal(original, File.ReadAllText(project));
                Assert.Equal(commit, await Git("rev-parse", "HEAD"));
                Assert.Equal("", await Git("status", "--porcelain"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

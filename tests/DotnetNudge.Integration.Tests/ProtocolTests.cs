using System.Diagnostics;
using System.Text.Json;
using DotnetNudge.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Data.Sqlite;

namespace DotnetNudge.Integration.Tests;

public sealed class ProtocolTests
{
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
            var assembly = typeof(DotnetNudge.McpEndpoint).Assembly.Location;
            var stderr = new List<string>();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "nudge-fixture",
                Command = "dotnet",
                Arguments = [assembly, "mcp", "--state-dir", stateDir],
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
            Assert.Contains(tools, t => t.Name == "nudge_preferences");
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
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(stateDir, "state.sqlite")}"))
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
            using (var db = new SqliteConnection($"Data Source={Path.Combine(stateDir, "state.sqlite")}"))
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
}

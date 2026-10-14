using System.Diagnostics;
using System.Text.Json;
using DotnetNudge.Core;
using NuGet.Versioning;

namespace DotnetNudge.Tests;

public sealed class FoundationTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Releases : IReleaseMetadata
    {
        readonly Dictionary<string, string> data;
        public Releases(string index, string channel) => data = new()
        {
            [ReleaseMetadata.IndexUrl] = index, ["https://example.invalid/10.0/releases.json"] = channel
        };
        public Task<(string Body, Assessment Freshness)> ReadAsync(string source, CancellationToken token) =>
            Task.FromResult((data[source], Assessment.Confirmed));
    }

    const string Index = """
        {"releases-index":[{"channel-version":"10.0","latest-sdk":"10.0.402",
          "releases.json":"https://example.invalid/10.0/releases.json"}]}
        """;
    const string Channel = """
        {"releases":[
          {"sdk":{"version":"10.0.402"},"sdks":[{"version":"10.0.113"},{"version":"10.0.112"}]},
          {"sdk":{"version":"10.0.110"},"sdks":[{"version":"10.0.114-rc.1"}]}
        ]}
        """;

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nudge-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string File(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }
        public NudgeState State(TimeProvider? clock = null) => new(Path.Combine(Root, "state"), clock);
        public void Dispose() => Directory.Delete(Root, true);
    }

    static Finding Finding(string revision = "r1", string candidate = "10.0.113") =>
        new(1, "sdk:root", "sdk:root", revision, "sdk-servicing", Priority.Medium,
            "10.0.101", candidate, new(null, null, "global.json"), Assessment.Partial,
            [], "review", true, "sdk");
    static ScanReport Report(Fixture fixture, Finding finding) =>
        new(1, WorkspaceDiscovery.Discover(fixture.Root), [finding], [], DateTimeOffset.UtcNow);

    [Fact]
    public void DiscoveryKeepsScopedDeclarationsAndAvoidsGeneratedDirectories()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup Condition="'$(Target)' == 'x'">
            <TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        fixture.File("F/F.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("V/V.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("nested/N.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("bin/Noise.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("node_modules/Noise.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File(".artifacts/Noise.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("a.slnx", "<Solution />");
        fixture.File("nested/global.json", """
            {"sdk":{"version":"10.0.101","rollForward":"disable",
             "allowPrerelease":false,"paths":["$host$"],"workloadVersion":"10.0.100"},
             "msbuild-sdks":{"Example":"1.0.0"}}
            """);
        var inventory = WorkspaceDiscovery.Discover(fixture.Root);
        Assert.Equal(4, inventory.Projects.Count);
        Assert.Equal("net10.0", Assert.Single(inventory.ProjectDeclarations
            .Single(p => p.Path == "App.csproj").TargetFrameworkExpressions));
        Assert.Single(inventory.Solutions);
        var sdk = Assert.Single(inventory.SdkDeclarations);
        Assert.Equal("disable", sdk.RollForward);
        Assert.Equal("$host$", Assert.Single(sdk.Paths));
        Assert.Equal("10.0.100", sdk.WorkloadVersion);
        Assert.Contains("msbuild-sdks", sdk.RawJson);
        Assert.Contains(inventory.Coverage, c => c.Check == "nuget-resolved-graph" &&
            c.Assessment == Assessment.Unknown);
    }

    [Fact]
    public void NestedGlobalJsonOverridesRootOnlyForItsScope()
    {
        using var fixture = new Fixture();
        fixture.File("global.json", """{"sdk":{"version":"10.0.101"}}""");
        fixture.File("nested/global.json", """{"sdk":{"version":"10.0.401"}}""");
        fixture.File("unused/global.json", """{"sdk":{"version":"11.0.100"}}""");
        fixture.File("nested/N.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var declarations = WorkspaceDiscovery.Discover(fixture.Root).SdkDeclarations;
        Assert.Equal(2, declarations.Count);
        Assert.Contains(declarations, d => d.Scope == "global.json" && d.Version == "10.0.101");
        Assert.Contains(declarations, d => d.Scope == "nested/global.json" && d.Version == "10.0.401");
    }

    [Fact]
    public async Task CommentedGlobalJsonRemainsAServiceableDeclaration()
    {
        using var fixture = new Fixture();
        fixture.File("global.json", """
            {
              // SDK pins support JSON comments.
              "sdk": {
                /* keep the current band */
                "version": "10.0.101",
                "allowPrerelease": false,
              },
            }
            """);
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        Assert.Equal("10.0.101", Assert.Single(workspace.SdkDeclarations).Version);
        var (findings, coverage) = await SdkServicing.CheckAsync(workspace, new Releases(Index, Channel));
        Assert.Equal("10.0.113", Assert.Single(findings).Candidate);
        Assert.DoesNotContain(coverage, c => c.Assessment == Assessment.Blocked);
    }

    [Fact]
    public void GitCommonDirectoryFollowsWorktreesWhileSnapshotsDoNot()
    {
        using var fixture = new Fixture();
        var common = Path.Combine(fixture.Root, "common");
        var one = Path.Combine(fixture.Root, "one");
        var two = Path.Combine(fixture.Root, "two");
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);
        foreach (var path in new[] { one, two })
        {
            var gitdir = Path.Combine(fixture.Root, "git-" + Path.GetFileName(path));
            Directory.CreateDirectory(gitdir);
            System.IO.File.WriteAllText(Path.Combine(gitdir, "commondir"),
                Path.GetRelativePath(gitdir, common));
            System.IO.File.WriteAllText(Path.Combine(path, ".git"), "gitdir: " + gitdir);
        }
        var left = WorkspaceDiscovery.Identities(one);
        var right = WorkspaceDiscovery.Identities(two);
        Assert.Equal(left.Repository, right.Repository);
        Assert.NotEqual(left.Worktree, right.Worktree);
    }

    [Fact]
    public void DecisionsFollowRepositoryButSnapshotsRemainWorktreeScoped()
    {
        using var fixture = new Fixture();
        var common = Path.Combine(fixture.Root, "git-common");
        Directory.CreateDirectory(common);
        foreach (var name in new[] { "left", "right" })
        {
            var gitdir = Path.Combine(fixture.Root, name + "-metadata");
            Directory.CreateDirectory(gitdir);
            fixture.File(name + "/.git", "gitdir: " + gitdir);
            System.IO.File.WriteAllText(Path.Combine(gitdir, "commondir"),
                Path.GetRelativePath(gitdir, common));
        }
        var left = Path.Combine(fixture.Root, "left");
        var right = Path.Combine(fixture.Root, "right");
        var first = WorkspaceDiscovery.Discover(left);
        var other = WorkspaceDiscovery.Discover(right);
        using var state = fixture.State();
        state.Save(new(1, first, [Finding()], [], DateTimeOffset.UtcNow));
        Assert.Null(state.Status(other.RepositoryId, other.WorktreeId, other.WorkspaceId).ScannedAt);
        state.Save(new(1, other, [Finding()], [], DateTimeOffset.UtcNow));
        state.Decide(first.RepositoryId, first.WorkspaceId, "sdk:root", "r1",
            DecisionAction.DisableCategory, null);
        Assert.Equal("category-disabled", Assert.Single(
            state.Status(other.RepositoryId, other.WorktreeId, other.WorkspaceId).Findings).Suppression);
    }

    [Fact]
    public async Task AdmittedSubfoldersKeepIndependentSnapshotsJobsHooksAndSdkSubjects()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        fixture.File("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("A/global.json", """{"sdk":{"version":"10.0.101"}}""");
        fixture.File("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("B/global.json", """{"sdk":{"version":"10.0.102"}}""");
        var a = Path.Combine(fixture.Root, "A");
        var b = Path.Combine(fixture.Root, "B");
        var clock = new Clock();
        using var state = fixture.State(clock);
        var scanner = new NudgeScanner(new Releases(Index, Channel), clock);
        var left = await scanner.ScanAsync(a);
        var right = await scanner.ScanAsync(b);
        Assert.Equal(left.Workspace.RepositoryId, right.Workspace.RepositoryId);
        Assert.Equal(left.Workspace.WorktreeId, right.Workspace.WorktreeId);
        Assert.NotEqual(left.Workspace.WorkspaceId, right.Workspace.WorkspaceId);
        var aFinding = Assert.Single(left.Findings);
        var bFinding = Assert.Single(right.Findings);
        Assert.NotEqual(aFinding.SubjectKey, bFinding.SubjectKey);
        Assert.Contains("A/global.json", aFinding.SubjectKey);
        Assert.Contains("B/global.json", bFinding.SubjectKey);
        state.QueueScan(left.Workspace.WorkspaceId);
        state.QueueScan(right.Workspace.WorkspaceId);
        state.Save(left);
        var pending = state.Status(right.Workspace.RepositoryId, right.Workspace.WorktreeId,
            right.Workspace.WorkspaceId);
        Assert.Null(pending.ScannedAt);
        Assert.Contains(pending.Coverage, c => c.Check == "scan-job");
        state.Save(right);
        Assert.Equal("10.0.101", state.Latest(left.Workspace.WorkspaceId)!.Findings.Single().Current);
        Assert.Equal("10.0.102", state.Latest(right.Workspace.WorkspaceId)!.Findings.Single().Current);
        var aPayload = JsonSerializer.Serialize(new { cwd = a, session_id = "a" });
        var bPayload = JsonSerializer.Serialize(new { cwd = b, session_id = "b" });
        Assert.Contains(aFinding.Id, HookAdapter.Handle("copilot", "session-start", aPayload, state));
        Assert.Equal("{}", HookAdapter.Handle("claude", "session-start", bPayload, state));
        clock.Now = clock.Now.AddDays(8);
        Assert.Contains(bFinding.Id, HookAdapter.Handle("claude", "session-start", bPayload, state));
        state.Decide(left.Workspace.RepositoryId, left.Workspace.WorkspaceId,
            aFinding.Id, aFinding.Revision, DecisionAction.SkipVersion, null);
        Assert.Equal("version-skipped", state.Suppression(left.Workspace.RepositoryId, aFinding));
        Assert.Null(state.Suppression(right.Workspace.RepositoryId, bFinding));
    }

    [Fact]
    public async Task SameBandServicingReadsAllSdksAndHonorsPrerelease()
    {
        using var fixture = new Fixture();
        fixture.File("global.json", """{"sdk":{"version":"10.0.101","allowPrerelease":false}}""");
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        var (findings, coverage) = await SdkServicing.CheckAsync(workspace, new Releases(Index, Channel));
        Assert.Equal("10.0.113", Assert.Single(findings).Candidate);
        Assert.Equal(Assessment.Partial, Assert.Single(findings).Assessment);
        Assert.Contains(coverage, c => c.Detail.Contains("effective SDK not observed"));
        fixture.File("global.json", """{"sdk":{"version":"10.0.113","allowPrerelease":true}}""");
        var (previews, _) = await SdkServicing.CheckAsync(
            WorkspaceDiscovery.Discover(fixture.Root), new Releases(Index, Channel));
        Assert.Equal("10.0.114-rc.1", Assert.Single(previews).Candidate);
        Assert.Equal(100, SdkServicing.FeatureBand(NuGetVersion.Parse("10.0.113")));
        Assert.Equal(400, SdkServicing.FeatureBand(NuGetVersion.Parse("10.0.402")));
        fixture.File("global.json", """{"sdk":{"version":"10.0.113"}}""");
        var (unspecified, _) = await SdkServicing.CheckAsync(
            WorkspaceDiscovery.Discover(fixture.Root), new Releases(Index, Channel));
        Assert.Empty(unspecified);
    }

    [Fact]
    public async Task MissingReleaseSourceIsBlockedNotEmptySuccess()
    {
        using var fixture = new Fixture();
        fixture.File("global.json", """{"sdk":{"version":"10.0.101"}}""");
        var source = new BrokenSource();
        var (findings, coverage) = await SdkServicing.CheckAsync(
            WorkspaceDiscovery.Discover(fixture.Root), source);
        Assert.Empty(findings);
        Assert.Contains(coverage, c => c.Assessment == Assessment.Blocked);
    }

    [Theory]
    [InlineData("{}", Channel)]
    [InlineData("""{"releases-index":{}}""", Channel)]
    [InlineData("""{"releases-index":[{"releases.json":"https://example.invalid/10.0/releases.json"}]}""", Channel)]
    [InlineData("""{"releases-index":[{"channel-version":"10.0"}]}""", Channel)]
    [InlineData(Index, "{}")]
    [InlineData(Index, """{"releases":"invalid"}""")]
    [InlineData(Index, """{"releases":[{"sdk":{"version":12}}]}""")]
    public async Task MalformedReleaseMetadataReportsCoverageInsteadOfCrashing(string index, string channel)
    {
        using var fixture = new Fixture();
        fixture.File("global.json", """{"sdk":{"version":"10.0.101"}}""");
        var (findings, coverage) = await SdkServicing.CheckAsync(
            WorkspaceDiscovery.Discover(fixture.Root), new Releases(index, channel));
        Assert.Empty(findings);
        Assert.Contains(coverage, c => c.Assessment is Assessment.Blocked or Assessment.Partial);
    }
    sealed class BrokenSource : IReleaseMetadata
    {
        public Task<(string Body, Assessment Freshness)> ReadAsync(string source, CancellationToken token) =>
            throw new IOException("offline");
    }

    [Fact]
    public async Task StaleCachedMetadataNeverLooksFresh()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var state = fixture.State(clock);
        state.Cache(ReleaseMetadata.IndexUrl, Index, null);
        clock.Now = clock.Now.AddDays(2);
        using var http = new HttpClient(new BrokenHandler());
        var source = new ReleaseMetadata(state, http, clock);
        var result = await source.ReadAsync(ReleaseMetadata.IndexUrl, CancellationToken.None);
        Assert.Equal(Assessment.Stale, result.Freshness);
        await Assert.ThrowsAsync<IOException>(() =>
            source.ReadAsync("https://example.invalid/releases.json", CancellationToken.None));
    }
    sealed class BrokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }

    [Fact]
    public void UnknownSchemaDoesNotDiscardState()
    {
        using var fixture = new Fixture();
        using (fixture.State()) { }
        var database = Path.Combine(fixture.Root, "state", "state.sqlite");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_version SET version=99";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => fixture.State());
        Assert.True(System.IO.File.Exists(database));
    }

    [Fact]
    public void LegacyStateMigratesSnapshotsJobsAndVersionSkips()
    {
        using var fixture = new Fixture();
        var report = Report(fixture, Finding());
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(report, Contract.Json))!;
        legacy["workspace"]!.AsObject().Remove("workspaceId");
        var database = Path.Combine(fixture.Root, "state", "state.sqlite");
        using (fixture.State()) { }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE snapshots(worktree TEXT PRIMARY KEY,repository TEXT NOT NULL,
                  report TEXT NOT NULL,scanned INTEGER NOT NULL);
                CREATE TABLE scan_jobs(worktree TEXT PRIMARY KEY,requested INTEGER NOT NULL,status TEXT NOT NULL);
                UPDATE schema_version SET version=1;
                """;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO snapshots(worktree,repository,report,scanned) VALUES($w,$r,$json,1);
                INSERT INTO scan_jobs(worktree,requested,status) VALUES($w,1,'pending');
                INSERT INTO decisions(repository,subject,category,action,candidate,revision,recorded)
                VALUES($r,'sdk:root','sdk','skip-version','10.0.113','r1',1);
                """;
            command.Parameters.AddWithValue("$w", report.Workspace.WorktreeId);
            command.Parameters.AddWithValue("$r", report.Workspace.RepositoryId);
            command.Parameters.AddWithValue("$json", legacy.ToJsonString());
            command.ExecuteNonQuery();
        }
        using var migrated = fixture.State();
        Assert.Equal("r1", migrated.Latest(report.Workspace.WorkspaceId)!.Findings.Single().Revision);
        Assert.Equal("version-skipped", migrated.Suppression(report.Workspace.RepositoryId, Finding()));
        Assert.Contains(migrated.Status(report.Workspace.RepositoryId, report.Workspace.WorktreeId,
            report.Workspace.WorkspaceId).Coverage, c => c.Check == "scan-job");
    }

    [Fact]
    public void SkippingAnotherCandidateDoesNotForgetEarlierCandidate()
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var a = Finding();
        var b = Finding("r2", "10.0.114");
        var report = Report(fixture, a);
        state.Save(report);
        var repo = report.Workspace.RepositoryId;
        var scope = report.Workspace.WorkspaceId;
        state.Decide(repo, scope, a.Id, a.Revision, DecisionAction.SkipVersion, null);
        state.Save(report with { Findings = [b] });
        state.Decide(repo, scope, b.Id, b.Revision, DecisionAction.SkipVersion, null);
        state.Save(report);
        Assert.Equal("version-skipped", state.Suppression(repo, a));
        Assert.Equal("version-skipped", state.Suppression(repo, b));
    }

    [Fact]
    public void DecisionsAreRevisionBoundAcrossWorktreesAndLeasesAreAtomic()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var first = fixture.State(clock);
        using var second = fixture.State(clock);
        var report = Report(fixture, Finding());
        first.Save(report);
        var repo = report.Workspace.RepositoryId;
        var scope = report.Workspace.WorkspaceId;
        Assert.True(first.TryLease(repo, Finding(), "copilot", TimeSpan.FromMinutes(2)));
        Assert.False(second.TryLease(repo, Finding(), "claude", TimeSpan.FromMinutes(2)));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.True(second.TryLease(repo, Finding(), "claude", TimeSpan.FromMinutes(2)));
        second.MarkDelivered(repo, Finding(), "claude");
        Assert.False(first.TryLease(repo, Finding(), "copilot", TimeSpan.FromMinutes(2)));
        first.Decide(repo, scope, "sdk:root", "r1", DecisionAction.SkipVersion, null);
        Assert.Equal("version-skipped", second.Suppression(repo, Finding()));
        Assert.Null(second.Suppression(repo, Finding("r2", "10.0.114")));
        Assert.Throws<InvalidOperationException>(() =>
            first.Decide(repo, scope, "sdk:root", "r2", DecisionAction.Snooze, clock.Now.AddDays(1)));
        first.Decide(repo, scope, "sdk:root", "r1", DecisionAction.Snooze, clock.Now.AddHours(1));
        Assert.Equal("snoozed", second.Suppression(repo, Finding("r2", "10.0.114")));
        clock.Now = clock.Now.AddHours(2);
        Assert.Null(second.Suppression(repo, Finding("r2", "10.0.114")));
        first.Decide(repo, scope, "sdk:root", "r1", DecisionAction.DisableCategory, null);
        Assert.Equal("category-disabled", second.Suppression(repo, Finding("r2", "10.0.114")));
        first.Decide(repo, scope, "sdk:root", "r1", DecisionAction.EnableCategory, null);
        Assert.Null(second.Suppression(repo, Finding("r2", "10.0.114")));
    }

    [Fact]
    public async Task ConcurrentRoutineLeasesForDifferentSubjectsRespectRepositoryBudget()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var first = fixture.State(clock);
        using var second = fixture.State(clock);
        var repo = WorkspaceDiscovery.Discover(fixture.Root).RepositoryId;
        var a = Finding() with { Id = "a", SubjectKey = "a" };
        var b = Finding() with { Id = "b", SubjectKey = "b" };
        var results = await Task.WhenAll(
            Task.Run(() => first.TryLease(repo, a, "copilot:a", TimeSpan.FromMinutes(2))),
            Task.Run(() => second.TryLease(repo, b, "claude:b", TimeSpan.FromMinutes(2))));
        Assert.Single(results, value => value);
        clock.Now = clock.Now.AddMinutes(3);
        Assert.True(first.TryLease(repo, b, "copilot:retry", TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void HooksRenderHostSpecificContractsWithoutPromptInjection()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var state = fixture.State(clock);
        var report = Report(fixture, Finding());
        state.Save(report);
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "a" });
        Assert.Equal("{}", HookAdapter.Handle("copilot", "prompt-submit", payload, state));
        var copilot = HookAdapter.Handle("copilot", "session-start", payload, state);
        using var json = JsonDocument.Parse(copilot);
        Assert.True(json.RootElement.TryGetProperty("additionalContext", out var context));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context.GetString()!) <= HookAdapter.ContextLimitBytes);
        Assert.Equal("{}", HookAdapter.Handle("copilot", "post-tool-use", payload, state));
        var claudePayload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "b" });
        Assert.Equal("{}", HookAdapter.Handle("claude", "post-tool-use", claudePayload, state));
        state.Save(report with { Findings = [Finding("r2", "10.0.114")] });
        Assert.Equal("{}", HookAdapter.Handle("claude", "post-tool-use", claudePayload, state));
        clock.Now = clock.Now.AddDays(8);
        var claude = HookAdapter.Handle("claude", "post-tool-use", claudePayload, state);
        using var output = JsonDocument.Parse(claude);
        Assert.Equal("PostToolUse", output.RootElement.GetProperty("hookSpecificOutput")
            .GetProperty("hookEventName").GetString());
        Assert.Equal("{}", HookAdapter.Handle("claude", "session-end", "", state));
        Assert.Equal("{}", HookAdapter.Handle("copilot", "session-start", "", state));
    }

    [Fact]
    public async Task CliFixtureRoundtripPersistsSuppression()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.File("global.json", """{"sdk":{"version":"10.0.101","allowPrerelease":false}}""");
        var metadata = Path.Combine(fixture.Root, "metadata");
        fixture.File("metadata/releases-index.json", Index.Replace("https://example.invalid", "https://example.invalid"));
        fixture.File("metadata/10.0/releases.json", Channel);
        var state = Path.Combine(fixture.Root, "state");
        var scan = await Cli("scan", "--workspace", fixture.Root, "--state-dir", state, "--json");
        using var parsed = JsonDocument.Parse(scan);
        var finding = parsed.RootElement.GetProperty("findings")[0];
        var id = finding.GetProperty("id").GetString()!;
        var revision = finding.GetProperty("revision").GetString()!;
        var before = await Cli("status", "--workspace", fixture.Root, "--state-dir", state, "--json");
        Assert.True(JsonDocument.Parse(before).RootElement.GetProperty("findings")[0].GetProperty("eligible").GetBoolean());
        await Cli("decision", "--workspace", fixture.Root, "--state-dir", state,
            "--finding", id, "--revision", revision, "--action", "skip-version", "--json");
        var after = await Cli("status", "--workspace", fixture.Root, "--state-dir", state, "--json");
        Assert.Equal("version-skipped", JsonDocument.Parse(after).RootElement
            .GetProperty("findings")[0].GetProperty("suppression").GetString());
        async Task<string> Cli(params string[] arguments)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "DotnetNudge.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true,
                RedirectStandardError = true };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(Path.Combine(directory.FullName, "src/DotnetNudge"));
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name);
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--");
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            psi.Environment["NUDGE_RELEASES_DIR"] = metadata;
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, stderr + stdout);
            return stdout;
        }
    }
}

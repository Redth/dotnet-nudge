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
    public void MetadataRevalidationPreservesBodyFetchProvenance()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var state = fixture.State(clock);
        state.Cache(ReleaseMetadata.IndexUrl, Index, "etag");
        var fetched = state.Cached(ReleaseMetadata.IndexUrl)!.Value.Fetched;
        clock.Now = clock.Now.AddHours(25);
        state.Cache(ReleaseMetadata.IndexUrl, Index, "etag", revalidated: true);
        var revalidated = state.Cached(ReleaseMetadata.IndexUrl)!.Value;
        Assert.Equal(fetched, revalidated.Fetched);
        Assert.Equal(clock.Now, revalidated.Refreshed);
    }

    [Fact]
    public void AdmissionRejectsRelativeHostWorkspaceInsteadOfUsingProcessCwd()
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        using var service = new NudgeService(state);
        Assert.Throws<ArgumentException>(() => state.Register(".", TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => service.Status("."));
        Assert.Throws<ArgumentException>(() => HookAdapter.Handle("copilot", "session-start",
            """{"cwd":".","session_id":"relative"}""", state));
    }

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
        state.Register(a, TimeSpan.FromMinutes(15));
        state.Register(b, TimeSpan.FromMinutes(15));
        state.QueueScan(left.Workspace.WorkspaceId);
        state.QueueScan(right.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner-a", left.Workspace.WorkspaceId)!, "scanner-a",
            left, null, NudgeState.WorkspaceFingerprint(a));
        var pending = state.Status(right.Workspace.RepositoryId, right.Workspace.WorktreeId,
            right.Workspace.WorkspaceId);
        Assert.Null(pending.ScannedAt);
        Assert.Contains(pending.Coverage, c => c.Check == "scan-job");
        state.FinishScan(state.LeaseScan("scanner-b", right.Workspace.WorkspaceId)!, "scanner-b",
            right, null, NudgeState.WorkspaceFingerprint(b));
        Assert.Equal("10.0.101", state.Latest(left.Workspace.WorkspaceId)!.Findings.Single().Current);
        Assert.Equal("10.0.102", state.Latest(right.Workspace.WorkspaceId)!.Findings.Single().Current);
        var aPayload = JsonSerializer.Serialize(new { cwd = a, session_id = "a" });
        var bPayload = JsonSerializer.Serialize(new { cwd = b, session_id = "b" });
        Assert.Contains(aFinding.Id, HookAdapter.Handle("copilot", "session-start", aPayload, state));
        Assert.Equal("{}", HookAdapter.Handle("claude", "session-start", bPayload, state));
        clock.Now = clock.Now.AddDays(8);
        Assert.Equal("{}", HookAdapter.Handle("claude", "session-start", bPayload, state));
        state.FinishScan(state.LeaseScan("refresh-b", right.Workspace.WorkspaceId)!, "refresh-b",
            right with { ScannedAt = clock.Now }, null, NudgeState.WorkspaceFingerprint(b));
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
        Assert.Contains(coverage, c => c.Detail.Contains("probe coverage"));
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
    public void MutedCategoryCanBeListedAndReenabledAfterItsFindingDisappears()
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var report = Report(fixture, Finding());
        state.Save(report);
        state.Decide(report.Workspace.RepositoryId, report.Workspace.WorkspaceId,
            "sdk:root", "r1", DecisionAction.DisableCategory, null);
        state.Save(report with { Findings = [] });
        using var service = new NudgeService(state);
        Assert.Equal("sdk", Assert.Single(service.Preferences(fixture.Root).DisabledCategories).Category);
        Assert.Empty(service.Preferences(fixture.Root).SuppressedFindings);
        Assert.Equal("enable-category", service.EnableCategory(fixture.Root, "sdk").Action);
        Assert.Empty(service.Preferences(fixture.Root).DisabledCategories);
        Assert.Throws<InvalidOperationException>(() => service.EnableCategory(fixture.Root, "sdk"));
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
    public async Task ConcurrentRoutineLeasesForDifferentSubjectsRespectCategoryBudget()
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
    public async Task DifferentCategoriesHaveIndependentAtomicLeasesAndCooldowns()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var first = fixture.State(clock);
        using var second = fixture.State(clock);
        var sdk = Finding();
        var package = sdk with {
            Id = "package:a", SubjectKey = "package:a", Kind = "package-update", Category = "package"
        };
        var otherPackage = package with { Id = "package:b", SubjectKey = "package:b" };
        var workload = sdk with {
            Id = "workload:a", SubjectKey = "workload:a", Kind = "workload-set-update", Category = "workload"
        };
        var report = Report(fixture, sdk) with { Findings = [sdk, package, otherPackage, workload] };
        first.Save(report);
        var repo = report.Workspace.RepositoryId;
        var results = await Task.WhenAll(
            Task.Run(() => first.TryLease(repo, sdk, "copilot:a", TimeSpan.FromMinutes(2))),
            Task.Run(() => second.TryLease(repo, package, "copilot:a", TimeSpan.FromMinutes(2))));
        Assert.All(results, value => Assert.True(value));
        Assert.False(first.TryLease(repo, otherPackage, "claude:b", TimeSpan.FromMinutes(2)));
        first.MarkDelivered(repo, sdk, "copilot:a");
        second.MarkDelivered(repo, package, "copilot:a");
        var status = first.Status(repo, report.Workspace.WorktreeId, report.Workspace.WorkspaceId);
        Assert.Equal("delivered-recently", status.Findings.Single(f => f.Finding.Id == sdk.Id).Suppression);
        Assert.Equal("category-cooldown", status.Findings.Single(f => f.Finding.Id == otherPackage.Id).Suppression);
        Assert.True(status.Findings.Single(f => f.Finding.Id == workload.Id).Eligible);
        Assert.True(first.TryLease(repo, workload, "copilot:a", TimeSpan.FromMinutes(2)));
        first.MarkDelivered(repo, workload, "copilot:a");
        clock.Now = clock.Now.AddDays(1).AddSeconds(1);
        Assert.False(first.TryLease(repo, otherPackage, "copilot:a", TimeSpan.FromMinutes(2)));
        Assert.True(second.TryLease(repo, otherPackage, "claude:new", TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void V3DeliveryMigrationPreservesLegacyCooldownsAndRecordsNewCategories()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        var report = Report(fixture, Finding());
        var repo = report.Workspace.RepositoryId;
        var database = Path.Combine(fixture.Root, "state", "state.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_version(version INTEGER NOT NULL);
                INSERT INTO schema_version VALUES(3);
                CREATE TABLE deliveries (
                  repository TEXT NOT NULL, subject TEXT NOT NULL, revision TEXT NOT NULL,
                  owner TEXT NOT NULL, lease_until INTEGER NOT NULL, delivered INTEGER,
                  PRIMARY KEY(repository,subject,revision));
                INSERT INTO deliveries VALUES($r,'legacy','r1','old-session',$now,$now);
                """;
            command.Parameters.AddWithValue("$r", repo);
            command.Parameters.AddWithValue("$now", clock.Now.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        using (var migrated = fixture.State(clock))
        {
            var package = Finding() with {
                Id = "package:a", SubjectKey = "package:a", Category = "package"
            };
            migrated.Save(report with { Findings = [package] });
            Assert.Equal("category-cooldown", Assert.Single(migrated.Status(repo,
                report.Workspace.WorktreeId, report.Workspace.WorkspaceId).Findings).Suppression);
            Assert.False(migrated.TryLease(repo, package, "new-session", TimeSpan.FromMinutes(2)));
            clock.Now = clock.Now.AddDays(1).AddSeconds(1);
            Assert.True(migrated.TryLease(repo, package, "new-session", TimeSpan.FromMinutes(2)));
            migrated.MarkDelivered(repo, package, "new-session");
            Assert.True(migrated.TryLease(repo, Finding(), "new-session", TimeSpan.FromMinutes(2)));
        }
        using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT version FROM schema_version";
        Assert.Equal(4L, query.ExecuteScalar());
        query.CommandText = "SELECT category FROM deliveries WHERE subject='legacy'";
        Assert.Equal("*", query.ExecuteScalar());
        query.CommandText = "SELECT category FROM deliveries WHERE subject='package:a'";
        Assert.Equal("package", query.ExecuteScalar());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public void HooksDeliverOneFindingPerActionableCategoryWithoutRepeatingQuestions(string host)
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var findings = new[] { "sdk", "package", "workload", "runtime", "toolchain" }
            .Select(category => Finding() with {
                Id = "first-" + category, SubjectKey = "first-" + category, Category = category,
                Kind = category switch {
                    "package" => "package-update",
                    "workload" => "workload-set-update",
                    "runtime" => "local-runtime-security-servicing",
                    "toolchain" => "host-tool-incompatibility",
                    _ => "sdk-servicing"
                }
            }).ToArray();
        var report = Report(fixture, findings[0]) with {
            Findings = [.. findings, .. findings.Select(f => f with {
                Id = "second-" + f.Category, SubjectKey = "second-" + f.Category, Priority = Priority.Low
            })]
        };
        state.Register(fixture.Root, TimeSpan.FromMinutes(15));
        state.QueueScan(report.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
            NudgeState.WorkspaceFingerprint(fixture.Root));
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "categories" });
        using var output = JsonDocument.Parse(HookAdapter.Handle(host, "session-start", payload, state));
        var container = host == "copilot" ? output.RootElement : output.RootElement.GetProperty("hookSpecificOutput");
        var context = container.GetProperty("additionalContext").GetString()!;
        Assert.Contains("Ask a separate question for each delivered category, sequentially", context);
        Assert.Contains("waiting for each answer", context);
        foreach (var finding in findings)
        {
            Assert.Contains("finding ID " + finding.Id, context);
            Assert.DoesNotContain("finding ID second-" + finding.Category, context);
        }
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context) <= HookAdapter.ContextLimitBytes);
        Assert.Equal("{}", HookAdapter.Handle(host, "post-tool-use", payload, state));
        var status = state.Status(report.Workspace.RepositoryId,
            report.Workspace.WorktreeId, report.Workspace.WorkspaceId);
        Assert.All(status.Findings.Where(f => f.Finding.Id.StartsWith("first-")),
            f => Assert.Equal("delivered-recently", f.Suppression));
        Assert.All(status.Findings.Where(f => f.Finding.Id.StartsWith("second-")),
            f => Assert.Equal("category-cooldown", f.Suppression));
    }

    [Fact]
    public void SuppressedCategoryDoesNotPreventQuestionsForOtherCategories()
    {
        foreach (var action in new[] {
            DecisionAction.Snooze, DecisionAction.SkipVersion, DecisionAction.DisableCategory
        })
        {
            using var fixture = new Fixture();
            var clock = new Clock();
            using var state = fixture.State(clock);
            var sdk = Finding();
            var package = sdk with {
                Id = "package:a", SubjectKey = "package:a", Kind = "package-update", Category = "package"
            };
            var workload = sdk with {
                Id = "workload:a", SubjectKey = "workload:a", Kind = "workload-set-update", Category = "workload"
            };
            var report = Report(fixture, sdk) with { Findings = [sdk, package, workload] };
            state.Register(fixture.Root, TimeSpan.FromMinutes(15));
            state.QueueScan(report.Workspace.WorkspaceId);
            state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
                NudgeState.WorkspaceFingerprint(fixture.Root));
            state.Decide(report.Workspace.RepositoryId, report.Workspace.WorkspaceId,
                package.Id, package.Revision, action,
                action == DecisionAction.Snooze ? clock.Now.AddDays(7) : null);
            var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "categories" });
            using var output = JsonDocument.Parse(HookAdapter.Handle("copilot", "post-tool-use", payload, state));
            var context = output.RootElement.GetProperty("additionalContext").GetString()!;
            Assert.Contains("finding ID " + sdk.Id, context);
            Assert.Contains("finding ID " + workload.Id, context);
            Assert.DoesNotContain("finding ID " + package.Id, context);
        }
    }

    [Fact]
    public void HookContextCapLeavesOmittedCategoriesEligibleForLaterDelivery()
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var findings = new[] { "sdk", "package", "workload", "runtime", "toolchain" }
            .Select(category => Finding() with {
                Id = category + new string('x', 850), SubjectKey = category, Category = category
            }).ToArray();
        var report = Report(fixture, findings[0]) with { Findings = findings };
        state.Register(fixture.Root, TimeSpan.FromMinutes(15));
        state.QueueScan(report.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
            NudgeState.WorkspaceFingerprint(fixture.Root));
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "bounded" });
        var delivered = new HashSet<string>();
        for (var attempt = 0; attempt < findings.Length && delivered.Count < findings.Length; attempt++)
        {
            using var output = JsonDocument.Parse(HookAdapter.Handle("copilot", "post-tool-use", payload, state));
            var context = output.RootElement.GetProperty("additionalContext").GetString()!;
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(context) <= HookAdapter.ContextLimitBytes);
            foreach (var finding in findings.Where(f => context.Contains("finding ID " + f.Id)))
                Assert.True(delivered.Add(finding.Category));
            if (attempt == 0) Assert.InRange(delivered.Count, 1, findings.Length - 1);
            var status = state.Status(report.Workspace.RepositoryId,
                report.Workspace.WorktreeId, report.Workspace.WorkspaceId);
            Assert.All(status.Findings.Where(f => !delivered.Contains(f.Finding.Category)),
                f => Assert.True(f.Eligible));
        }
        Assert.Equal(findings.Length, delivered.Count);
    }

    [Fact]
    public void HooksRenderHostSpecificContractsWithoutPromptInjection()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var state = fixture.State(clock);
        var report = Report(fixture, Finding());
        state.Register(fixture.Root, TimeSpan.FromMinutes(15));
        state.QueueScan(report.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
            NudgeState.WorkspaceFingerprint(fixture.Root));
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

    [Theory]
    [InlineData("copilot", "session-start", "ask_user")]
    [InlineData("copilot", "post-tool-use", "ask_user")]
    [InlineData("claude", "session-start", "AskUserQuestion")]
    [InlineData("claude", "prompt-submit", "AskUserQuestion")]
    [InlineData("claude", "post-tool-use", "AskUserQuestion")]
    public void HookAdviceRequestsNativeChoicesWithoutRecordingAnAnswer(
        string host, string eventName, string questionTool)
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var report = Report(fixture, Finding());
        state.Register(fixture.Root, TimeSpan.FromMinutes(15));
        state.QueueScan(report.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
            NudgeState.WorkspaceFingerprint(fixture.Root));
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "question" });
        using var output = JsonDocument.Parse(HookAdapter.Handle(host, eventName, payload, state));
        var container = host == "copilot" ? output.RootElement :
            output.RootElement.GetProperty("hookSpecificOutput");
        var context = container.GetProperty("additionalContext").GetString()!;
        Assert.Contains("internal context (not user-facing text)", context);
        Assert.Contains($"`{questionTool}`", context);
        Assert.DoesNotContain(host == "copilot" ? "AskUserQuestion" : "ask_user", context);
        Assert.Contains("nudge-maintain", context);
        Assert.Contains("delivered-recently is a delivery cooldown, not a user answer", context);
        Assert.Contains("offer it once if the current ID and revision still match", context);
        Assert.Contains("single-select", context);
        Assert.Contains("2-4 concrete choices, including Not now", context);
        Assert.Contains("do not print a choice menu in chat", context);
        Assert.Contains("short, friendly, plain-language questions and choices", context);
        Assert.Contains("What's new?, Help me update", context);
        Assert.Contains("Remind me next week and Not now", context);
        Assert.Contains("Keep finding IDs, revision tokens, assessment labels and ISO timestamps out of user-facing text", context);
        Assert.Contains("Details are read-only", context);
        Assert.Contains("Help me update requests a plan, not approval", context);
        Assert.Contains("Explain the actual changes and ask before executing them", context);
        Assert.Contains("explicit approval for exact reminder decisions", context);
        Assert.Contains("noninteractive", context);
        Assert.Contains("without assuming an answer or recording a decision", context);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context) <= HookAdapter.ContextLimitBytes);
        Assert.False(output.RootElement.TryGetProperty("modifiedPrompt", out _));
        Assert.Null(state.Suppression(report.Workspace.RepositoryId, Finding()));
        var current = Assert.Single(state.Status(report.Workspace.RepositoryId,
            report.Workspace.WorktreeId, report.Workspace.WorkspaceId).Findings);
        Assert.False(current.Eligible);
        Assert.Equal("delivered-recently", current.Suppression);
        Assert.Equal("{}", HookAdapter.Handle(host, "post-tool-use", payload, state));
    }

    [Fact]
    public void NonSdkHookFindingsRequestCategoryAppropriateQuestionsForBothHosts()
    {
        var cases = new (string Kind, string? Candidate, string Guidance)[] {
            ("package-update", "2.0.0", "get help updating that package"),
            ("package-advisory", "2.0.0", "candidate is a verified fix without supporting evidence"),
            ("package-advisory", null, "Help me find a fix"),
            ("package-deprecation", null, "Help me find an alternative"),
            ("workload-set-update", "10.0.200", "get help planning that exact set update"),
            ("workload-set-difference", "10.0.100", "Help me check the mismatch"),
            ("workload-manifest-difference", "10.0.2/10.0.100", "Help me check the mismatch"),
            ("workload-missing", "android", "Help me plan installation"),
            ("host-tool-incompatibility", null, "Help me check compatibility"),
            ("local-runtime-security-servicing", "10.0.2", "does not update deployed apps"),
            ("deployment-runtime-security-servicing", "10.0.2", "local runtime is not a deployment fix"),
            ("restored-graph-change", "2.0.0", "not an available upgrade")
        };
        foreach (var host in new[] { "copilot", "claude" })
        foreach (var (kind, candidate, guidance) in cases)
        {
            using var fixture = new Fixture();
            using var state = fixture.State();
            var finding = Finding() with {
                Kind = kind, Candidate = candidate, Category = "package",
                Scope = new("App.csproj", "net10.0", null)
            };
            var report = Report(fixture, finding);
            state.Register(fixture.Root, TimeSpan.FromMinutes(15));
            state.QueueScan(report.Workspace.WorkspaceId);
            state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
                NudgeState.WorkspaceFingerprint(fixture.Root));
            var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "question" });
            using var output = JsonDocument.Parse(HookAdapter.Handle(host, "post-tool-use", payload, state));
            var container = host == "copilot" ? output.RootElement :
                output.RootElement.GetProperty("hookSpecificOutput");
            var context = container.GetProperty("additionalContext").GetString()!;
            Assert.Contains(host == "copilot" ? "`ask_user`" : "`AskUserQuestion`", context);
            Assert.Contains(guidance, context);
            Assert.Contains("including Not now", context);
            Assert.Contains("not approval to change files", context);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(context) <= HookAdapter.ContextLimitBytes);
            Assert.Null(state.Suppression(report.Workspace.RepositoryId, finding));
            Assert.Equal("{}", HookAdapter.Handle(host, "post-tool-use", payload, state));
        }
    }

    [Theory]
    [InlineData(Assessment.Unknown)]
    [InlineData(Assessment.Stale)]
    [InlineData(Assessment.Blocked)]
    public void PackageFindingsWithoutActionableEvidenceDoNotRequestQuestions(Assessment assessment)
    {
        using var fixture = new Fixture();
        using var state = fixture.State();
        var report = Report(fixture, Finding() with { Kind = "package-update", Assessment = assessment });
        state.Register(fixture.Root, TimeSpan.FromMinutes(15));
        state.QueueScan(report.Workspace.WorkspaceId);
        state.FinishScan(state.LeaseScan("scanner")!, "scanner", report, null,
            NudgeState.WorkspaceFingerprint(fixture.Root));
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Root, session_id = "question" });
        Assert.Equal("{}", HookAdapter.Handle("copilot", "post-tool-use", payload, state));
        Assert.Equal("{}", HookAdapter.Handle("claude", "post-tool-use", payload, state));
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

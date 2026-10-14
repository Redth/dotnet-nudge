using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using DotnetNudge.Trials;

var testRoot = Environment.GetEnvironmentVariable("NUDGE_TEST_ROOT")
    ?? throw new Exception("Set NUDGE_TEST_ROOT to a disposable directory (not the shared temp directory)");
Directory.CreateDirectory(testRoot);
var tests = new (string Name, Func<Task> Run)[] {
    ("approved revision and committed snapshot", Snapshot),
    ("baseline versus changed build and environment", Evidence),
    ("failure and dirty cleanup refusal", Failure),
    ("baseline source mutation blocks comparison", BaselineMutation),
    ("package edit and diff", Package),
    ("central package edit guards", CentralPackage),
    ("zero tests are not a pass", ZeroTests),
    ("failing test counts remain in evidence", FailedTestCounts),
    ("test artifact mutation invalidates test pass", ChangedTestArtifact),
    ("global.json comments", JsonComments),
    ("global.json paths block resolver override", ResolverPaths),
    ("archive traversal and digest failure", Archive),
    ("contained SDK links materialized", SdkLinks),
    ("timeout and owned child termination", Timeout),
    ("read-only resume and cancellation", Lifecycle),
    ("approved plan digest rejects mutation", TamperedPlan),
    ("active cancellation", ActiveCancel),
    ("unsupported source and SDK selection", Unsupported),
    ("preflight reads committed attributes", CommittedAttributes),
    ("exclusive ownership and non-overlapping roots", Ownership),
    ("committed project imports are contained", Imports),
    ("nested arbitrary-extension imports are contained", NestedImports),
    ("original dirty content and HEAD are fingerprinted", OriginalChanges),
    ("SDK copies share one disk budget", AggregateBudget),
    ("exact-ref cleanup retries and foreign checkout guard", CleanupRetry)
};
if (Environment.GetEnvironmentVariable("NUDGE_REAL_REPO") is not null)
    tests = [.. tests, ("real private-root worktree/build/test", RealSmoke)];
if (Environment.GetEnvironmentVariable("NUDGE_REAL_DOWNLOAD") == "1")
    tests = [.. tests, ("official metadata/download/digest/extraction", RealDownload)];
if (Environment.GetEnvironmentVariable("NUDGE_MAJOR_DOWNLOAD") == "1")
    tests = [.. tests, ("official .NET 11 SDK archive link extraction", RealMajorDownload)];
if (Environment.GetEnvironmentVariable("NUDGE_MAJOR_REPO") is not null)
    tests = [.. tests, ("real .NET 10 to 11 private SDK trial", RealMajorSmoke)];
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
return failures == 0 ? 0 : 1;

async Task Snapshot()
{
    var f = await Fixture.Create("snapshot");
    File.WriteAllText(Path.Combine(f.Repo, "dirty.txt"), "do not copy");
    var plan = f.Plan();
    var runner = f.Runner;
    runner.Propose(plan);
    Throws<TrialException>(() => runner.Approve(plan.Id, 2));
    runner.Approve(plan.Id, 1);
    Throws<TrialException>(() => runner.ExecuteAsync(plan.Id, 2).GetAwaiter().GetResult());
    var record = await runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.ReadyForReview, record.BlockReason ?? "no report");
    Check(record.Report!.OriginalDirtyExcluded, "dirty source exclusion not recorded");
    Check(record.Report.OriginalGitStatusBefore == record.Report.OriginalGitStatusAfter,
        "original checkout status not preserved");
    Check(!File.Exists(Path.Combine(record.Report.Worktree, "dirty.txt")), "dirty file copied");
    Check(File.ReadAllText(Path.Combine(f.Repo, "dirty.txt")) == "do not copy", "original changed");
    Check((await Fixture.Command(f.Repo, "git", "status", "--porcelain")).Contains("dirty.txt"), "original Git status changed");
    Check(record.Report.Worktree.StartsWith(f.Root + Path.DirectorySeparatorChar), "worktree escaped root");
    Check(record.Report.SourceCommit == plan.SourceCommit && record.Report.Branch.StartsWith("nudge-trial-"), "identity lost");
    Throws<TrialException>(() => runner.ExecuteAsync(plan.Id, 1).GetAwaiter().GetResult());
    Check(runner.MarkAdopted(plan.Id).Phase == TrialPhase.Adopted, "reviewed adoption not persisted");
}

async Task Evidence()
{
    var f = await Fixture.Create("evidence");
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var record = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.ReadyForReview, record.BlockReason ?? "trial not ready");
    var report = record.Report!;
    Check(report.Commands.Count == 10, $"expected version/info/restore/build/test each stage, got {report.Commands.Count}");
    Check(report.Baseline!.Compilation == TrialDimension.Passed &&
        report.Candidate!.Compilation == TrialDimension.Passed &&
        report.Baseline.AutomatedTests == TrialDimension.Passed &&
        report.Candidate.AutomatedTests == TrialDimension.Passed, "stage comparison incorrect");
    Check(report.Commands.Where(c => c.Arguments[0] == "test").All(c => c.TestCount == 1), "test counts not captured");
    Check(report.Commands.All(c => c.Executable.StartsWith(f.Root) &&
        c.Environment["NUDGE_TRIAL_ID"] == plan.Id &&
        c.Environment["DOTNET_ROOT"].StartsWith(f.Root) &&
        c.Environment["DOTNET_ROOT_" + (plan.CandidateSdk.Rid.EndsWith("arm64") ? "ARM64" : "X64")] ==
            c.Environment["DOTNET_ROOT"] &&
        c.Environment["DOTNET_CLI_HOME"].StartsWith(f.Root) &&
        c.Environment["HOME"] == c.Environment["DOTNET_CLI_HOME"] &&
        c.Environment["NUGET_PACKAGES"].StartsWith(f.Root) &&
        c.Environment["NUGET_HTTP_CACHE_PATH"].StartsWith(f.Root) &&
        c.Environment["NUGET_PLUGINS_CACHE_PATH"].StartsWith(f.Root) &&
        c.Environment["NUGET_SCRATCH"].StartsWith(f.Root) &&
        c.Environment["TMPDIR"].StartsWith(f.Root) &&
        c.Environment["TMP"] == c.Environment["TMPDIR"] &&
        c.Environment["TEMP"] == c.Environment["TMPDIR"] &&
        c.ExitCode == 0), "command identity/environment incorrect");
    var baselineEnvironment = report.Commands.First(c => c.Stage == "baseline").Environment;
    var candidateEnvironment = report.Commands.First(c => c.Stage == "candidate").Environment;
    Check(baselineEnvironment["DOTNET_ROOT"] != candidateEnvironment["DOTNET_ROOT"] &&
        baselineEnvironment["DOTNET_CLI_HOME"] != candidateEnvironment["DOTNET_CLI_HOME"] &&
        baselineEnvironment["NUGET_PACKAGES"] != candidateEnvironment["NUGET_PACKAGES"] &&
        baselineEnvironment["NUGET_HTTP_CACHE_PATH"] != candidateEnvironment["NUGET_HTTP_CACHE_PATH"] &&
        baselineEnvironment["TMPDIR"] != candidateEnvironment["TMPDIR"],
        "baseline and candidate shared mutable SDK, user, package, or temp roots");
    Check(report.Commands.Any(c => c.Stage == "baseline" && c.Arguments.SequenceEqual(["build", "fixture.csproj"])), "baseline build missing");
    Check(report.Commands.Any(c => c.Stage == "candidate" && c.Arguments.SequenceEqual(["build", "fixture.csproj"])), "changed build missing");
    Check(report.CandidateWorktree != report.Worktree &&
        report.Commands.Where(c => c.Stage == "candidate").All(c => c.WorkingDirectory == report.CandidateWorktree) &&
        report.Commands.Where(c => c.Stage == "baseline").All(c => c.WorkingDirectory == report.Worktree),
        "baseline and candidate reused source/output scope");
    Check(report.Commands.Where(c => c.Arguments[0] == "test").All(c =>
        c.ArtifactVerified &&
        c.ArtifactSha256 == report.Commands.Single(b => b.Stage == c.Stage && b.Arguments[0] == "build").ArtifactSha256 &&
        c.BuildArtifacts!.All(path => path.StartsWith(c.WorkingDirectory + Path.DirectorySeparatorChar))),
        "test did not use verified stage-local assembly");
    Check(File.ReadAllText(report.Commands.Single(c => c.Stage == "baseline" && c.Arguments[0] == "build").BuildArtifacts!.Single()) ==
        "10.0.100" &&
        File.ReadAllText(report.Commands.Single(c => c.Stage == "candidate" && c.Arguments[0] == "build").BuildArtifacts!.Single()) ==
        "10.0.401", "candidate binary was reused from baseline");
    Check(File.Exists(report.DiffPath), "diff missing");
    var source = report.Worktree;
    File.Delete(Path.Combine(source, "global.json"));
    var archived = await f.Runner.CleanupAsync(plan.Id);
    Check(archived.Phase == TrialPhase.Archived && !Directory.Exists(source) &&
        !Directory.Exists(report.CandidateWorktree), "clean worktrees not removed");
}

async Task Failure()
{
    var f = await Fixture.Create("failure");
    var plan = f.Plan();
    File.WriteAllText(Path.Combine(f.Candidate, "fail"), "");
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var record = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.Blocked && record.BlockReason!.Contains("exited"), "failed build reported as success");
    Check(record.Report!.Commands.Any(c => c.Stage == "candidate" && c.ExitCode == 8), "failure log/evidence missing");
    File.WriteAllText(Path.Combine(record.Report.Worktree, "new.txt"), "changed");
    Throws<TrialException>(() => f.Runner.CleanupAsync(plan.Id).GetAwaiter().GetResult());
    Check(Directory.Exists(record.Report.Worktree), "dirty trial deleted");
}

async Task BaselineMutation()
{
    var f = await Fixture.Create("baseline-mutation");
    var script = Path.Combine(f.Baseline, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace(
        "*) echo", "*) if [ \"$1\" = build ]; then echo '<!-- changed -->' >> fixture.csproj; fi; echo"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("Baseline mutated"),
        "baseline mutation accepted as upgrade");
    Check(result.Report!.Candidate is { Restore: TrialDimension.NotExercised },
        "candidate was executed after baseline mutation");
}

async Task Package()
{
    var f = await Fixture.Create("package");
    var project = Path.Combine(f.Repo, "fixture.csproj");
    File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>");
    await Fixture.Command(f.Repo, "git", "add", "fixture.csproj");
    await Fixture.Command(f.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "package");
    var plan = f.Plan() with {
        SourceCommit = await Fixture.Command(f.Repo, "git", "rev-parse", "HEAD"),
        SdkOnly = false, PackageEdits = [new("fixture.csproj", "Example", "1.0.0", "2.0.0")]
    };
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.ReadyForReview, result.BlockReason ?? "package trial failed");
    Check(File.ReadAllText(result.Report!.DiffPath).Contains("+  <ItemGroup>") ||
        File.ReadAllText(result.Report.DiffPath).Contains("2.0.0"), "package diff missing");
    Check(File.ReadAllText(project).Contains("1.0.0"), "source package declaration changed");
    Throws<TrialException>(() => f.Runner.CleanupAsync(plan.Id).GetAwaiter().GetResult());
}

async Task ZeroTests()
{
    var f = await Fixture.Create("no-tests");
    var script = Path.Combine(f.Candidate, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace("echo 'Total tests: 1'", "echo no-tests"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("zero discovered"), "zero tests accepted");
    Check(result.Report!.Candidate!.AutomatedTests == TrialDimension.NotExercised,
        "zero tests recorded as pass");
}

async Task FailedTestCounts()
{
    var f = await Fixture.Create("failed-test-count");
    var script = Path.Combine(f.Candidate, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace(
        "echo 'Total tests: 1'",
        "echo 'Failed: 1, Passed: 199, Total: 200'; exit 1"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var record = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.Blocked && record.Report!.Candidate!.AutomatedTests == TrialDimension.Failed,
        "failing test stage not reported");
    Check(record.Report!.Candidate!.TestCount == 200 &&
        record.Report.Commands.Single(c => c.Stage == "candidate" && c.Arguments[0] == "test").TestCount == 200,
        "discovered failed-test count was lost");
}

async Task ChangedTestArtifact()
{
    var f = await Fixture.Create("changed-test-artifact");
    var script = Path.Combine(f.Candidate, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace(
        "echo 'Total tests: 1'", "echo altered > bin/fixture.dll; echo 'Total tests: 1'"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var record = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.Blocked && record.BlockReason!.Contains("artifact changed") &&
        record.Report!.Candidate!.AutomatedTests == TrialDimension.Failed &&
        record.Report.Candidate.TestCount == 1 &&
        !record.Report.Commands.Last().ArtifactVerified,
        "test-modified build artifact was reported as a passing test");
}

Task CentralPackage()
{
    var root = Path.Combine(testRoot, "central-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "Directory.Packages.props");
    File.WriteAllText(path,
        "<Project><ItemGroup><PackageVersion Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>");
    TrialEdits.ApplyPackageEdit(root, new("Directory.Packages.props", "Example", "1.0.0", "2.0.0"));
    Check(File.ReadAllText(path).Contains("Version=\"2.0.0\""), "central version not updated");
    Throws<TrialException>(() => TrialEdits.ApplyPackageEdit(root,
        new("Directory.Packages.props", "Example", "1.0.0", "3.0.0")));
    return Task.CompletedTask;
}

async Task ResolverPaths()
{
    var f = await Fixture.Create("resolver");
    File.WriteAllText(Path.Combine(f.Repo, "global.json"),
        "{\"sdk\":{\"version\":\"10.0.100\",\"paths\":[\"/external/sdk\"]}}");
    await Fixture.Command(f.Repo, "git", "add", "global.json");
    await Fixture.Command(f.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "resolver");
    var plan = f.Plan() with { SourceCommit = await Fixture.Command(f.Repo, "git", "rev-parse", "HEAD") };
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("paths"), "global.json paths accepted");
    Check(File.ReadAllText(Path.Combine(f.Repo, "global.json")).Contains("/external/sdk"),
        "original global.json changed");
}

Task JsonComments()
{
    var path = Path.Combine(testRoot, "json-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    var text = "{\n // preserve me\n \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"disable\" },\n \"custom\": 1\n}";
    File.WriteAllText(Path.Combine(path, "global.json"), text);
    TrialEdits.UpdateSdkPin(path, "10.0.401");
    Check(File.ReadAllText(Path.Combine(path, "global.json")) == text.Replace("10.0.100", "10.0.401"), "JSONC changed unexpectedly");
    return Task.CompletedTask;
}

async Task Archive()
{
    var path = Path.Combine(testRoot, "tar-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    using var buffer = new MemoryStream();
    using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "../escape") { DataStream = new MemoryStream([1]) };
        writer.WriteEntry(entry);
    }
    buffer.Position = 0;
    Throws<TrialException>(() => TrialSdk.ExtractVerifiedTar(buffer, path));
    Check(!File.Exists(Path.Combine(testRoot, "escape")), "archive traversal wrote outside root");
    var f = await Fixture.Create("digest");
    var sdk = new TrialSdk(new HttpClient(new MaliciousMetadataHandler()));
    await ThrowsAsync<TrialException>(() => sdk.PrepareAsync(
        new SdkSelection("10.0.401", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            AllowDownload: true), Path.Combine(f.Root, "download"), true, CancellationToken.None));
}

async Task SdkLinks()
{
    var root = Path.Combine(testRoot, "sdk-links-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using (var buffer = new MemoryStream())
    {
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "real.dll") {
                DataStream = new MemoryStream([1, 2, 3])
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "linked.dll") {
                LinkName = "real.dll"
            });
        }
        buffer.Position = 0;
        var output = Path.Combine(root, "valid");
        TrialSdk.ExtractVerifiedTar(buffer, output);
        Check(File.ReadAllBytes(Path.Combine(output, "linked.dll")).SequenceEqual(new byte[] { 1, 2, 3 }) &&
            new FileInfo(Path.Combine(output, "linked.dll")).LinkTarget is null,
            "contained archive link was not materialized");
    }
    foreach (var (name, target) in new[] {
                 ("escape", "../../outside"), ("dangling", "absent.dll"), ("cycle", "cycle"),
                 ("directory", "dir") })
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "dir"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = target });
        }
        buffer.Position = 0;
        Throws<TrialException>(() => TrialSdk.ExtractVerifiedTar(buffer, Path.Combine(root, name)));
    }
    var f = await Fixture.Create("prepared-links");
    File.WriteAllText(Path.Combine(f.Baseline, "real.dll"), "verified");
    File.CreateSymbolicLink(Path.Combine(f.Baseline, "linked.dll"), "real.dll");
    var path = Path.Combine(f.Root, "prepared-copy");
    await new TrialSdk().PrepareAsync(
        new SdkSelection("10.0.100", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, f.Baseline),
        path, false, CancellationToken.None);
    Check(File.ReadAllText(Path.Combine(path, "linked.dll")) == "verified" &&
        new FileInfo(Path.Combine(path, "linked.dll")).LinkTarget is null,
        "prepared internal link not materialized");
    File.CreateSymbolicLink(Path.Combine(f.Baseline, "outside.dll"), "../../outside.dll");
    Throws<TrialException>(() => new TrialSdk().ValidateSelection(
        new SdkSelection("10.0.100", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, f.Baseline), false));
}

async Task Timeout()
{
    var f = await Fixture.Create("timeout");
    var command = new TrialProcess();
    var script = Path.Combine(f.Root, "slow.sh");
    File.WriteAllText(script, "#!/bin/sh\nsleep 90 &\necho $!\nwait\n");
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var result = await command.RunAsync("test", script, [], f.Repo,
        new Dictionary<string, string>(), Path.Combine(f.Root, "timeout-logs"),
        TimeSpan.FromMilliseconds(300), CancellationToken.None);
    Check(result.TimedOut && result.ExitCode != 0, "timeout not recorded");
    var pid = int.Parse(File.ReadAllText(result.StdoutPath).Trim());
    await Task.Delay(200);
    var check = await Fixture.Command(f.Repo, "ps", "-o", "stat=", "-p", pid.ToString());
    Check(check.Length == 0 || check.StartsWith("Z"), "owned child still running");
}

async Task Lifecycle()
{
    var f = await Fixture.Create("lifecycle");
    var plan = f.Plan();
    f.Runner.Propose(plan);
    using (var lease = new TrialStore(plan.TrialRoot).Lease(plan.Id))
        Throws<TrialException>(() => new TrialStore(plan.TrialRoot).Lease(plan.Id));
    Check(f.Runner.Status(plan.Id).Phase == TrialPhase.Proposed, "status failed");
    Check(f.Runner.Report(plan.Id) is null, "unexpected report");
    var revision = f.Runner.Revise(plan.Id, plan with { Revision = 2 });
    Check(revision.Plan.Revision == 2, "revision not persisted");
    Throws<TrialException>(() => f.Runner.Approve(plan.Id, 1));
    var cancelled = f.Runner.Cancel(plan.Id);
    Check(cancelled.Phase == TrialPhase.Cancelled, "cancel failed");
    Throws<TrialException>(() => f.Runner.Approve(plan.Id, 1));
}

async Task ActiveCancel()
{
    var f = await Fixture.Create("active-cancel");
    var script = Path.Combine(f.Baseline, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace(
        "*) echo", "*) printf running > \"$DOTNET_CLI_HOME/active-command\"; sleep 90; echo"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var running = f.Runner.ExecuteAsync(plan.Id, 1);
    var marker = Path.Combine(plan.TrialRoot, plan.Id, "home-baseline", "active-command");
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!File.Exists(marker) && DateTime.UtcNow < deadline)
        await Task.Delay(20);
    Check(File.Exists(marker) && f.Runner.Status(plan.Id).Phase == TrialPhase.Baseline,
        "baseline process never started");
    f.Runner.Cancel(plan.Id);
    var result = await running;
    Check(result.Phase == TrialPhase.Cancelled, "active cancellation not persisted");
    Check(result.Report!.Commands.Last().ExitCode != 0, "cancelled process recorded as success");
}

async Task TamperedPlan()
{
    var f = await Fixture.Create("tampered");
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var store = new TrialStore(plan.TrialRoot);
    var record = store.Load(plan.Id);
    record.Plan = record.Plan with { CandidateSdk = record.Plan.CandidateSdk with { Version = "10.0.999" } };
    store.Save(record);
    Throws<TrialException>(() => f.Runner.ExecuteAsync(plan.Id, 1).GetAwaiter().GetResult());
    Check(f.Runner.Status(plan.Id).Phase == TrialPhase.Approved, "tampered plan executed");
}

async Task Unsupported()
{
    var f = await Fixture.Create("unsupported");
    var plan = f.Plan();
    Throws<TrialException>(() => f.Runner.Propose(plan with
    {
        Commands = [new("restore", ["fixture.csproj"]), new("workload", ["install", "maui"]),
            new("build", ["fixture.csproj"])]
    }));
    var wrongRid = plan with { BaselineSdk = plan.BaselineSdk with { Rid = "win-x64" } };
    f.Runner.Propose(wrongRid);
    f.Runner.Approve(plan.Id, 1);
    var blocked = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(blocked.Phase == TrialPhase.Blocked && blocked.BlockReason!.Contains("RID"), "unsupported RID accepted");
    var symlink = await Fixture.Create("symlink");
    File.CreateSymbolicLink(Path.Combine(symlink.Repo, "link.txt"), "/etc/hosts");
    await Fixture.Command(symlink.Repo, "git", "add", "link.txt");
    await Fixture.Command(symlink.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "symlink");
    var symlinkPlan = symlink.Plan() with {
        SourceCommit = await Fixture.Command(symlink.Repo, "git", "rev-parse", "HEAD")
    };
    symlink.Runner.Propose(symlinkPlan);
    symlink.Runner.Approve(symlinkPlan.Id, 1);
    var rejected = await symlink.Runner.ExecuteAsync(symlinkPlan.Id, 1);
    Check(rejected.Phase == TrialPhase.Blocked &&
        rejected.BlockReason!.Contains("symlinks"), "tracked symlink accepted");
}

async Task CommittedAttributes()
{
    var f = await Fixture.Create("attributes");
    var attributes = Path.Combine(f.Repo, ".gitattributes");
    File.WriteAllText(attributes, "*.cs filter=lfs\n");
    await Fixture.Command(f.Repo, "git", "add", ".gitattributes");
    await Fixture.Command(f.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "filter");
    File.WriteAllText(attributes, "*.cs text\n");
    var plan = f.Plan() with { SourceCommit = await Fixture.Command(f.Repo, "git", "rev-parse", "HEAD") };
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("filters"),
        "committed filter masked by dirty source");
    Check(File.ReadAllText(attributes) == "*.cs text\n", "original attributes changed");
}

async Task Ownership()
{
    var f = await Fixture.Create("ownership");
    var plan = f.Plan();
    var trial = Path.Combine(plan.TrialRoot, plan.Id);
    Directory.CreateDirectory(Path.Combine(trial, "home"));
    Directory.CreateDirectory(Path.Combine(trial, "nuget"));
    File.WriteAllText(Path.Combine(trial, "home", "keep"), "shared");
    File.WriteAllText(Path.Combine(trial, "nuget", "keep"), "shared");
    Throws<TrialException>(() => f.Runner.Propose(plan));
    Check(File.ReadAllText(Path.Combine(trial, "home", "keep")) == "shared" &&
        File.ReadAllText(Path.Combine(trial, "nuget", "keep")) == "shared",
        "existing trial content modified");
    foreach (var root in new[] { f.Repo, f.Root, Path.Combine(f.Repo, "nested") })
        Throws<TrialException>(() => new TrialRunner(new TrialStore(root)).Propose(plan with { TrialRoot = root }));
    var g = await Fixture.Create("owner-marker");
    var owned = g.Plan();
    g.Runner.Propose(owned);
    File.WriteAllText(Path.Combine(owned.TrialRoot, owned.Id, "owner.json"),
        "{\"Id\":\"trial\",\"Token\":\"not-the-owner\"}");
    Throws<TrialException>(() => g.Runner.Approve(owned.Id, 1));
    Throws<TrialException>(() => g.Runner.CleanupAsync(owned.Id).GetAwaiter().GetResult());
    Check(File.Exists(Path.Combine(owned.TrialRoot, owned.Id, "state.json")), "tampered owner deleted");
}

async Task Imports()
{
    foreach (var (name, project) in new[] {
                 ("absolute", "<Project><ItemGroup><ProjectReference Include=\"/external/project.csproj\" /></ItemGroup></Project>"),
                 ("expanded", "<Project><ItemGroup><ProjectReference Include=\"$(ExternalProject)\" /></ItemGroup></Project>"),
                 ("ancestor", "<Project><Import Project=\"../../outside.targets\" /></Project>") })
    {
        var f = await Fixture.Create("imports-" + name);
        File.WriteAllText(Path.Combine(f.Repo, "fixture.csproj"), project);
        await Fixture.Command(f.Repo, "git", "add", "fixture.csproj");
        await Fixture.Command(f.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture",
            "commit", "-qm", name);
        var plan = f.Plan() with { SourceCommit = await Fixture.Command(f.Repo, "git", "rev-parse", "HEAD") };
        f.Runner.Propose(plan);
        f.Runner.Approve(plan.Id, 1);
        var result = await f.Runner.ExecuteAsync(plan.Id, 1);
        Check(result.Phase == TrialPhase.Blocked &&
            result.BlockReason!.Contains("import/reference"), $"unsafe {name} project reference accepted");
    }
    var external = await Fixture.Create("ancestor-config");
    File.WriteAllText(Path.Combine(external.Root, "Directory.Build.targets"), "<Project />");
    var ancestorPlan = external.Plan();
    external.Runner.Propose(ancestorPlan);
    external.Runner.Approve(ancestorPlan.Id, 1);
    var blocked = await external.Runner.ExecuteAsync(ancestorPlan.Id, 1);
    Check(blocked.Phase == TrialPhase.Blocked && blocked.BlockReason!.Contains("External ancestor"),
        "external ancestor Directory.Build.targets accepted");
}

async Task NestedImports()
{
    foreach (var (name, nested) in new[] {
                 ("absolute-import", "<Project><Import Project=\"/shared/outside.targets\" /></Project>"),
                 ("external-reference", "<Project><ItemGroup><ProjectReference Include=\"../../../outside.csproj\" /></ItemGroup></Project>"),
                 ("dynamic-reference", "<Project><ItemGroup><ProjectReference Include=\"$(ExternalProject)\" /></ItemGroup></Project>"),
                 ("uncommitted-import", "<Project><Import Project=\"missing.xml\" /></Project>")
             })
    {
        var f = await Fixture.Create("nested-" + name);
        var build = Path.Combine(f.Repo, "build");
        var deeper = Path.Combine(build, "deeper");
        Directory.CreateDirectory(deeper);
        File.WriteAllText(Path.Combine(f.Repo, "fixture.csproj"),
            "<Project><Import Project=\"build/common.xml\" /></Project>");
        File.WriteAllText(Path.Combine(build, "common.xml"),
            "<Project><Import Project=\"deeper/level.data\" /></Project>");
        var leaf = Path.Combine(deeper, "level.data");
        File.WriteAllText(leaf, nested);
        await Fixture.Command(f.Repo, "git", "add", "fixture.csproj", "build/common.xml", "build/deeper/level.data");
        await Fixture.Command(f.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture",
            "commit", "-qm", name);
        File.WriteAllText(leaf, "<Project />");
        var plan = f.Plan() with { SourceCommit = await Fixture.Command(f.Repo, "git", "rev-parse", "HEAD") };
        f.Runner.Propose(plan);
        f.Runner.Approve(plan.Id, 1);
        var result = await f.Runner.ExecuteAsync(plan.Id, 1);
        Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("import/reference") &&
            result.Report is null, $"committed nested {name} was not rejected before worktree creation");
        Check(File.ReadAllText(leaf) == "<Project />", "original nested XML was changed");
    }

    var cycle = await Fixture.Create("nested-cycle");
    var directory = Path.Combine(cycle.Repo, "build");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(cycle.Repo, "fixture.csproj"),
        "<Project><Import Project=\"build/common.xml\" /></Project>");
    File.WriteAllText(Path.Combine(directory, "common.xml"),
        "<Project><Import Project=\"loop.data\" /></Project>");
    File.WriteAllText(Path.Combine(directory, "loop.data"),
        "<Project><Import Project=\"common.xml\" /></Project>");
    await Fixture.Command(cycle.Repo, "git", "add", "fixture.csproj", "build/common.xml", "build/loop.data");
    await Fixture.Command(cycle.Repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture",
        "commit", "-qm", "cycle");
    var approved = cycle.Plan() with {
        SourceCommit = await Fixture.Command(cycle.Repo, "git", "rev-parse", "HEAD")
    };
    cycle.Runner.Propose(approved);
    cycle.Runner.Approve(approved.Id, 1);
    var complete = await cycle.Runner.ExecuteAsync(approved.Id, 1);
    Check(complete.Phase == TrialPhase.ReadyForReview, complete.BlockReason ?? "contained import cycle blocked");
}

async Task OriginalChanges()
{
    var dirty = await Fixture.Create("dirty-content");
    var original = Path.Combine(dirty.Repo, "dirty.txt");
    File.WriteAllText(original, "before");
    var script = Path.Combine(dirty.Baseline, "dotnet");
    File.WriteAllText(script, File.ReadAllText(script).Replace(
        "*) echo", $"*) if [ \"$1\" = build ]; then printf after > '{original}'; fi; echo"));
    var plan = dirty.Plan();
    dirty.Runner.Propose(plan);
    dirty.Runner.Approve(plan.Id, 1);
    var result = await dirty.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.Blocked && result.BlockReason!.Contains("Original Git HEAD") &&
        result.Report!.OriginalGitStatusBefore == result.Report.OriginalGitStatusAfter &&
        result.Report.OriginalBefore!.TrackedAndUntrackedSha256 != result.Report.OriginalAfter!.TrackedAndUntrackedSha256,
        "changed dirty bytes with same Git status not detected");

    var switched = await Fixture.Create("clean-head");
    File.WriteAllText(Path.Combine(switched.Repo, "other.txt"), "second commit");
    await Fixture.Command(switched.Repo, "git", "add", "other.txt");
    await Fixture.Command(switched.Repo, "git", "-c", "user.email=fixture@example.invalid",
        "-c", "user.name=Fixture", "commit", "-qm", "second");
    var head = await Fixture.Command(switched.Repo, "git", "rev-parse", "HEAD");
    var change = Path.Combine(switched.Baseline, "dotnet");
    File.WriteAllText(change, File.ReadAllText(change).Replace(
        "*) echo", $"*) if [ \"$1\" = build ]; then git -C '{switched.Repo}' checkout -q HEAD~1; fi; echo"));
    var next = switched.Plan() with { SourceCommit = head };
    switched.Runner.Propose(next);
    switched.Runner.Approve(next.Id, 1);
    var second = await switched.Runner.ExecuteAsync(next.Id, 1);
    Check(second.Phase == TrialPhase.Blocked && second.Report!.OriginalBefore!.Head != second.Report.OriginalAfter!.Head,
        "clean original HEAD switch not detected");
}

async Task AggregateBudget()
{
    var f = await Fixture.Create("aggregate");
    File.WriteAllBytes(Path.Combine(f.Candidate, "padding"), new byte[25_000]);
    var plan = f.Plan() with { Budget = new TrialBudget(12_000, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15)) };
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var record = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(record.Phase == TrialPhase.Blocked && record.BlockReason!.Contains("budget") &&
        record.Report!.BaselineSdkProvenance is not null &&
        record.Report.CandidateSdkProvenance is null, "SDK preparations each received the full disk allowance");
}

async Task CleanupRetry()
{
    var f = await Fixture.Create("cleanup-retry");
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var result = await f.Runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.ReadyForReview, result.BlockReason ?? "trial failed");
    var archived = await f.Runner.CleanupAsync(plan.Id);
    var state = new TrialStore(plan.TrialRoot);
    archived.Phase = TrialPhase.ReadyForReview;
    state.Save(archived);
    Check((await f.Runner.CleanupAsync(plan.Id)).Phase == TrialPhase.Archived &&
        !Directory.Exists(result.Report!.Worktree) &&
        !Directory.Exists(result.Report.CandidateWorktree), "partially completed cleanup did not resume safely");
    var branch = $"nudge-trial-{plan.Id}";
    var other = Path.Combine(f.Root, "other-worktree");
    await Fixture.Command(f.Repo, "git", "worktree", "add", "-q", "-b", branch, other, plan.SourceCommit);
    await ThrowsAsync<TrialException>(() => new TrialGit().DeleteOwnedBranchAsync(f.Repo, branch, plan.SourceCommit));
    Check((await Fixture.Command(f.Repo, "git", "rev-parse", $"refs/heads/{branch}")) == plan.SourceCommit,
        "foreign checked-out branch was deleted");
}

async Task RealSmoke()
{
    var repo = Environment.GetEnvironmentVariable("NUDGE_REAL_REPO")!;
    var sdkRoot = Environment.GetEnvironmentVariable("NUDGE_REAL_SDK_ROOT")!;
    Check(Directory.Exists(sdkRoot) && Directory.Exists(repo), "real smoke inputs missing");
    var project = Path.GetFileName(Directory.GetFiles(repo, "*.csproj").Single());
    File.WriteAllText(Path.Combine(repo, "global.json"), "{\n  // preserve\n  \"sdk\": {\"version\":\"10.0.401\"}\n}");
    await Fixture.Command(repo, "git", "init", "-q");
    await Fixture.Command(repo, "git", "add", "global.json", project, "UnitTest1.cs");
    await Fixture.Command(repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "fixture");
    var sourceStatus = await Fixture.Command(repo, "git", "status", "--porcelain");
    var sdkFingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sdkRoot, "dotnet"))));
    var hostDotnet = Environment.GetEnvironmentVariable("NUDGE_ORIGINAL_DOTNET") ?? Path.Combine(sdkRoot, "dotnet");
    var hostFingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostDotnet)));
    var hostSdks = await Fixture.Command(repo, hostDotnet, "--list-sdks");
    var root = Path.Combine(testRoot, "real-run-" + Guid.NewGuid().ToString("N"));
    var runner = new TrialRunner(new TrialStore(root));
    var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    var plan = new TrialPlan("smoke", 1, repo, await Fixture.Command(repo, "git", "rev-parse", "HEAD"),
        root, [project], new("10.0.401", rid, sdkRoot),
        new("10.0.401", rid, sdkRoot), [],
        [new("restore", [project]),
         new("build", [project, "--no-restore"]),
         new("test", [project, "--no-build"])],
        new("artifacts/logs", "artifacts/changes.diff"),
        new(2_000_000_000, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(3)), true, AllowNetwork: true);
    runner.Propose(plan);
    runner.Approve(plan.Id, 1);
    var result = await runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.ReadyForReview, result.BlockReason ?? "real trial incomplete");
    Check(result.Report!.Commands.Count == 10 &&
        result.Report.Commands.Where(c => c.Arguments[0] == "test").All(c => c.TestCount > 0),
        "real tests not measured");
    Check(result.Report.Commands.All(c => c.Executable.StartsWith(root)), "real test used shared SDK");
    Check(sourceStatus == await Fixture.Command(repo, "git", "status", "--porcelain"), "original Git status changed");
    Check(sdkFingerprint == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sdkRoot, "dotnet")))),
        "SDK source fingerprint changed");
    Check(hostFingerprint == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostDotnet))) &&
        hostSdks == await Fixture.Command(repo, hostDotnet, "--list-sdks"),
        "original toolchain fingerprint/selection changed");
    Console.WriteLine($"REAL_SMOKE_REPORT={Path.Combine(root, "smoke", "state.json")}");
}

async Task RealDownload()
{
    var root = Path.Combine(testRoot, "official-download-" + Guid.NewGuid().ToString("N"));
    var prepared = await new TrialSdk().PrepareAsync(
        new SdkSelection("10.0.401", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            AllowDownload: true), root, true, CancellationToken.None, 2_000_000_000);
    Check(prepared.ArchiveSha512?.Length == 128 && prepared.Provenance.Contains("dotnetcli.blob.core.windows.net"),
        "official acquisition provenance missing");
    Check(await Fixture.Command(root, prepared.Executable, "--version") == "10.0.401",
        "downloaded private SDK version mismatch");
    Console.WriteLine($"OFFICIAL_PRIVATE_SDK={root}");
}

async Task RealMajorDownload()
{
    const string version = "11.0.100-rc.1.26425.128";
    var root = Path.Combine(testRoot, "major-official-" + Guid.NewGuid().ToString("N"));
    var prepared = await new TrialSdk().PrepareAsync(
        new SdkSelection(version, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            AllowDownload: true), root, true, CancellationToken.None, 2_000_000_000);
    Check(await Fixture.Command(root, prepared.Executable, "--version") == version,
        "official .NET 11 private SDK selection failed");
    Check(Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories).Any(file =>
        file.Contains("/zh-Hans/zh-Hans/") && new FileInfo(file).LinkTarget is null),
        "official SDK internal links were not materialized");
    Console.WriteLine($"OFFICIAL_MAJOR_SDK={root}");
}

async Task RealMajorSmoke()
{
    var repo = Environment.GetEnvironmentVariable("NUDGE_MAJOR_REPO")!;
    var baselineRoot = Environment.GetEnvironmentVariable("NUDGE_REAL_SDK_ROOT")!;
    var candidateRoot = Environment.GetEnvironmentVariable("NUDGE_MAJOR_SDK_ROOT")!;
    var project = Path.GetFileName(Directory.GetFiles(repo, "*.csproj").Single());
    File.WriteAllText(Path.Combine(repo, "global.json"), "{\"sdk\":{\"version\":\"10.0.401\"}}");
    await Fixture.Command(repo, "git", "init", "-q");
    await Fixture.Command(repo, "git", "add", "global.json", project, "UnitTest1.cs");
    await Fixture.Command(repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture",
        "commit", "-qm", "major-sdk-fixture");
    var originalStatus = await Fixture.Command(repo, "git", "status", "--porcelain");
    var root = Path.Combine(testRoot, "major-run-" + Guid.NewGuid().ToString("N"));
    var runner = new TrialRunner(new TrialStore(root));
    var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    var plan = new TrialPlan("major", 1, repo, await Fixture.Command(repo, "git", "rev-parse", "HEAD"),
        root, [project], new("10.0.401", rid, baselineRoot),
        new("11.0.100-rc.1.26425.128", rid, candidateRoot), [],
        [new("restore", [project]), new("build", [project, "--no-restore"]),
         new("test", [project, "--no-build"])],
        new("artifacts/logs", "artifacts/changes.diff"),
        new(2_000_000_000, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(3)), true, AllowNetwork: true);
    runner.Propose(plan);
    runner.Approve(plan.Id, 1);
    var result = await runner.ExecuteAsync(plan.Id, 1);
    Check(result.Phase == TrialPhase.ReadyForReview, result.BlockReason ?? "major SDK trial failed");
    var report = result.Report!;
    Check(report.Baseline!.TestCount == 1 && report.Candidate!.TestCount == 1 &&
        File.ReadAllText(report.DiffPath).Contains("11.0.100-rc.1.26425.128"), "major upgrade evidence missing");
    Check(report.Commands.Where(c => c.Stage == "candidate").All(c => c.WorkingDirectory == report.CandidateWorktree) &&
        report.Commands.Where(c => c.Stage == "baseline").All(c => c.WorkingDirectory == report.Worktree),
        "candidate reused baseline worktree");
    Check(Directory.GetFiles(report.CandidateWorktree!, "*.dll", SearchOption.AllDirectories)
        .Any(file => file.Contains("/bin/")), "candidate produced no distinct build artifact");
    Check(originalStatus == await Fixture.Command(repo, "git", "status", "--porcelain"),
        "original repository changed in major SDK trial");
    Console.WriteLine($"MAJOR_SMOKE_REPORT={Path.Combine(root, "major", "state.json")}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
sealed class MaliciousMetadataHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string metadata = """
        {"releases":[{"sdk":{"version":"10.0.401","files":[{"rid":"osx-arm64","name":"dotnet-sdk.tar.gz","url":"https://builds.dotnet.microsoft.com/dotnet/fake.tar.gz","hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}}]}
        """;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith(".tar.gz") ? "modified archive" : metadata)
        });
    }
}

sealed class Fixture
{
    public required string Root { get; init; }
    public required string Repo { get; init; }
    public required string Baseline { get; init; }
    public required string Candidate { get; init; }
    public required TrialRunner Runner { get; init; }
    public required string Commit { get; init; }

    public static async Task<string> Command(string cwd, string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && exe != "ps") throw new Exception($"{exe} {string.Join(' ', args)}: {await error}");
        return (await output).Trim();
    }

    public static async Task<Fixture> Create(string name)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NUDGE_TEST_ROOT")!, name + "-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var baseline = Path.Combine(root, "baseline");
        var candidate = Path.Combine(root, "candidate");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(baseline);
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(repo, "fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        await Command(repo, "git", "init", "-q");
        await Command(repo, "git", "add", ".");
        await Command(repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "fixture");
        foreach (var (path, version) in new[] { (baseline, "10.0.100"), (candidate, "10.0.401") })
        {
            var script = Path.Combine(path, "dotnet");
            File.WriteAllText(script, $"#!/bin/sh\ncase \"$1\" in\n--version) echo {version};;\n--info) echo 'SDK {version}';;\n*) echo \"NUDGE_TRIAL_ID=$NUDGE_TRIAL_ID DOTNET_ROOT=$DOTNET_ROOT\"; if [ -f \"{path}/fail\" ]; then exit 8; fi; if [ \"$1\" = build ]; then mkdir -p bin; printf '{version}' > bin/fixture.dll; fi; if [ \"$1\" = test ]; then if [ \"$(cat bin/fixture.dll)\" != '{version}' ]; then exit 9; fi; echo 'Total tests: 1'; fi;;\nesac\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new Fixture {
            Root = root, Repo = repo, Baseline = baseline, Candidate = candidate,
            Commit = await Command(repo, "git", "rev-parse", "HEAD"),
            Runner = new TrialRunner(new TrialStore(Path.Combine(root, "trials")))
        };
    }

    public TrialPlan Plan() => new("trial", 1, Repo, Commit, Path.Combine(Root, "trials"),
        ["fixture.csproj"],
        new("10.0.100", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, Baseline),
        new("10.0.401", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, Candidate),
        [], [new("restore", ["fixture.csproj"]), new("build", ["fixture.csproj"]), new("test", ["fixture.csproj", "--no-build"])],
        new("artifacts/logs", "artifacts/changes.diff"),
        new(50_000_000, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15)), true);
}

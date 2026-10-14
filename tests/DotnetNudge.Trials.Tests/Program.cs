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
    ("global.json comments", JsonComments),
    ("global.json paths block resolver override", ResolverPaths),
    ("archive traversal and digest failure", Archive),
    ("timeout and owned child termination", Timeout),
    ("read-only resume and cancellation", Lifecycle),
    ("approved plan digest rejects mutation", TamperedPlan),
    ("active cancellation", ActiveCancel),
    ("unsupported source and SDK selection", Unsupported),
    ("preflight reads committed attributes", CommittedAttributes)
};
if (Environment.GetEnvironmentVariable("NUDGE_REAL_REPO") is not null)
    tests = [.. tests, ("real private-root worktree/build/test", RealSmoke)];
if (Environment.GetEnvironmentVariable("NUDGE_REAL_DOWNLOAD") == "1")
    tests = [.. tests, ("official metadata/download/digest/extraction", RealDownload)];
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
        c.Environment["NUGET_PACKAGES"].StartsWith(f.Root) &&
        c.ExitCode == 0), "command identity/environment incorrect");
    Check(report.Commands.Any(c => c.Stage == "baseline" && c.Arguments.SequenceEqual(["build", "fixture.csproj"])), "baseline build missing");
    Check(report.Commands.Any(c => c.Stage == "candidate" && c.Arguments.SequenceEqual(["build", "fixture.csproj"])), "changed build missing");
    Check(File.Exists(report.DiffPath), "diff missing");
    var source = report.Worktree;
    File.Delete(Path.Combine(source, "global.json"));
    var archived = await f.Runner.CleanupAsync(plan.Id);
    Check(archived.Phase == TrialPhase.Archived && !Directory.Exists(source), "clean worktree not removed");
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
        "*) echo", "*) sleep 90; echo"));
    var plan = f.Plan();
    f.Runner.Propose(plan);
    f.Runner.Approve(plan.Id, 1);
    var running = f.Runner.ExecuteAsync(plan.Id, 1);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (f.Runner.Status(plan.Id).Phase != TrialPhase.Baseline && DateTime.UtcNow < deadline)
        await Task.Delay(20);
    Check(f.Runner.Status(plan.Id).Phase == TrialPhase.Baseline, "baseline never started");
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
        await Command(repo, "git", "init", "-q");
        await Command(repo, "git", "add", ".");
        await Command(repo, "git", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-qm", "fixture");
        foreach (var (path, version) in new[] { (baseline, "10.0.100"), (candidate, "10.0.401") })
        {
            var script = Path.Combine(path, "dotnet");
            File.WriteAllText(script, $"#!/bin/sh\ncase \"$1\" in\n--version) echo {version};;\n--info) echo 'SDK {version}';;\n*) echo \"NUDGE_TRIAL_ID=$NUDGE_TRIAL_ID DOTNET_ROOT=$DOTNET_ROOT\"; if [ -f \"{path}/fail\" ]; then exit 8; fi; if [ \"$1\" = test ]; then echo 'Total tests: 1'; fi;;\nesac\n");
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
        [], [new("restore", ["fixture.csproj"]), new("build", ["fixture.csproj"]), new("test", ["fixture.csproj"])],
        new("artifacts/logs", "artifacts/changes.diff"),
        new(50_000_000, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15)), true);
}

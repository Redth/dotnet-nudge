using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetNudge.Trials;

public sealed class TrialRunner(TrialStore store, TrialGit? git = null, TrialSdk? sdk = null, TrialProcess? process = null)
{
    private readonly TrialGit _git = git ?? new TrialGit();
    private readonly TrialSdk _sdk = sdk ?? new TrialSdk();
    private readonly TrialProcess _process = process ?? new TrialProcess();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public TrialRecord Propose(TrialPlan plan)
    {
        Validate(plan);
        if (Path.GetFullPath(plan.TrialRoot) != store.Root)
            throw new TrialException("Plan trial root differs from store root");
        using var lease = store.Lease(plan.Id);
        if (File.Exists(TrialPaths.Child(store.Root, Path.Combine(plan.Id, "state.json"))))
            throw new TrialException("Trial ID already exists");
        var record = new TrialRecord { Plan = plan };
        store.Save(record);
        return record;
    }

    public TrialRecord Revise(string id, TrialPlan plan)
    {
        Validate(plan);
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase != TrialPhase.Proposed || plan.Id != id ||
            plan.Revision != record.Plan.Revision + 1 || plan.TrialRoot != record.Plan.TrialRoot ||
            plan.Repository != record.Plan.Repository)
            throw new TrialException("Only the next revision of the same proposed trial can replace its plan");
        record.Plan = plan;
        store.Save(record);
        return record;
    }

    public TrialRecord Approve(string id, int exactRevision)
    {
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase != TrialPhase.Proposed || record.Plan.Revision != exactRevision)
            throw new TrialException("Approval must match the current proposed revision");
        record.ApprovedRevision = exactRevision;
        record.ApprovedPlanDigest = Digest(record.Plan);
        Move(record, TrialPhase.Approved);
        return record;
    }

    public TrialRecord Status(string id) => store.Load(id);
    public TrialReport? Report(string id) => store.Load(id).Report;

    public TrialRecord MarkAdopted(string id)
    {
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase != TrialPhase.ReadyForReview)
            throw new TrialException("Only a reviewed ready trial can be marked adopted");
        Move(record, TrialPhase.Adopted);
        return record;
    }

    public async Task<TrialRecord> ExecuteAsync(string id, int exactApprovedRevision, CancellationToken cancellationToken = default)
    {
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase != TrialPhase.Approved ||
            record.ApprovedRevision != exactApprovedRevision ||
            record.Plan.Revision != exactApprovedRevision ||
            record.ApprovedPlanDigest != Digest(record.Plan))
            throw new TrialException("Execution requires the exact approved revision and an unused approval");
        var plan = record.Plan;
        var root = TrialPaths.Child(store.Root, id);
        record.OwnerToken = Guid.NewGuid().ToString("N");
        Move(record, TrialPhase.Preparing);
        var commands = new List<CommandEvidence>();
        string? source = null;
        string branch = "";
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_active.TryAdd(id, duration)) throw new TrialException("Trial already active in this runner");
        try
        {
            duration.CancelAfter(plan.Budget.MaxDuration);
            _sdk.ValidateSelection(plan.BaselineSdk, plan.AllowNetwork);
            _sdk.ValidateSelection(plan.CandidateSdk, plan.AllowNetwork);
            var dirty = await _git.PreflightAsync(plan, duration.Token, plan.Budget.MaxProcessDuration);
            var originalStatus = await _git.StatusAsync(plan.Repository, duration.Token, plan.Budget.MaxProcessDuration);
            (branch, source) = await _git.CreateAsync(plan, duration.Token, plan.Budget.MaxProcessDuration);
            record.Checkpoints.Add($"Created owned worktree {branch} at {source}; original dirty excluded: {dirty}");
            var report = new TrialReport(plan.SourceCommit, dirty, branch, source,
                plan.BaselineSdk.Version, plan.CandidateSdk.Version,
                TrialPaths.Child(root, plan.Artifacts.DiffFile),
                "Worktrees and private roots do not sandbox MSBuild/test code; OS-level sandboxing is required for untrusted projects. Workloads, external platform tools, package graph/advisory comparison, runtime smoke, and platform matrix are not validated." +
                (plan.Commands.Any(c => c.Verb == "test") ? "" : " Compile-only: automated tests not exercised."),
                commands, OriginalGitStatusBefore: originalStatus);
            record.Report = report;
            store.Save(record);
            var baseline = await _sdk.PrepareAsync(plan.BaselineSdk, TrialPaths.Child(root, "sdk-baseline"),
                plan.AllowNetwork, duration.Token, plan.Budget.MaxDiskBytes);
            report = report with { BaselineSdkProvenance = baseline };
            record.Report = report;
            store.Save(record);
            var candidate = await _sdk.PrepareAsync(plan.CandidateSdk, TrialPaths.Child(root, "sdk-candidate"),
                plan.AllowNetwork, duration.Token, plan.Budget.MaxDiskBytes);
            report = report with { CandidateSdkProvenance = candidate };
            record.Report = report;
            store.Save(record);
            EnforceDisk(root, plan.Budget.MaxDiskBytes);
            Move(record, TrialPhase.Baseline);
            await RunStage("baseline", baseline.Executable, plan.BaselineSdk, source, root, plan, record, commands, duration.Token);
            if ((await _git.DiffAsync(source, duration.Token, plan.Budget.MaxProcessDuration)).Length != 0)
                throw new TrialException("Baseline mutated tracked source; upgrade comparison is invalid");
            Move(record, TrialPhase.Upgrading);
            foreach (var edit in plan.PackageEdits) TrialEdits.ApplyPackageEdit(source, edit);
            TrialEdits.UpdateSdkPin(source, plan.CandidateSdk.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(report.DiffPath)!);
            File.WriteAllText(report.DiffPath, await _git.DiffAsync(source, duration.Token, plan.Budget.MaxProcessDuration));
            record.Checkpoints.Add("Applied approved edits; diff persisted");
            store.Save(record);
            Move(record, TrialPhase.Validating);
            await RunStage("candidate", candidate.Executable, plan.CandidateSdk, source, root, plan, record, commands, duration.Token);
            EnforceDisk(root, plan.Budget.MaxDiskBytes);
            Move(record, TrialPhase.ReadyForReview);
        }
        catch (OperationCanceledException)
        {
            record.BlockReason = "Trial cancelled or overall time budget exceeded; owned process terminated";
            Move(record, TrialPhase.Cancelled);
        }
        catch (TrialException ex)
        {
            record.BlockReason = ex.Message;
            Move(record, TrialPhase.Blocked);
        }
        catch (Exception ex)
        {
            record.BlockReason = ex.ToString();
            Move(record, TrialPhase.Failed);
        }
        finally
        {
            _active.TryRemove(id, out _);
            if (record.Report is { } current)
            {
                try
                {
                    var after = await _git.StatusAsync(plan.Repository);
                    record.Report = current with { OriginalGitStatusAfter = after };
                    if (record.Phase == TrialPhase.ReadyForReview && current.OriginalGitStatusBefore != after)
                    {
                        record.BlockReason = "Original Git status changed during the trial; source isolation cannot be attested";
                        Move(record, TrialPhase.Blocked);
                    }
                    else store.Save(record);
                }
                catch (TrialException ex)
                {
                    record.BlockReason = $"Cannot inspect original Git status: {ex.Message}";
                    Move(record, TrialPhase.Failed);
                }
            }
        }
        return record;
    }

    public TrialRecord Cancel(string id)
    {
        if (_active.TryGetValue(id, out var active))
        {
            active.Cancel();
            return store.Load(id);
        }
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase == TrialPhase.Approved || record.Phase == TrialPhase.Proposed)
            Move(record, TrialPhase.Cancelled);
        else if (record.Phase is TrialPhase.Preparing or TrialPhase.Baseline or TrialPhase.Upgrading or TrialPhase.Validating)
            throw new TrialException("Runner interrupted; inspect checkpoint before manual recovery (no process ownership established)");
        return record;
    }

    public async Task<TrialRecord> CleanupAsync(string id)
    {
        using var lease = store.Lease(id);
        var record = store.Load(id);
        if (record.Phase == TrialPhase.Archived) return record;
        if (record.Phase is TrialPhase.Preparing or TrialPhase.Baseline or TrialPhase.Upgrading or TrialPhase.Validating)
            throw new TrialException("Active trial cannot be cleaned up");
        if (record.Report is { } report)
        {
            var expectedSource = TrialPaths.Child(store.Root, Path.Combine(id, "source"));
            if (record.OwnerToken is null || record.ApprovedPlanDigest != Digest(record.Plan) ||
                Path.GetFullPath(record.Plan.TrialRoot) != store.Root || report.Worktree != expectedSource ||
                report.Branch != $"nudge-trial-{id}" || report.SourceCommit != record.Plan.SourceCommit)
                throw new TrialException("Worktree ownership metadata does not match trial");
            if (await _git.StatusAsync(report.Worktree) is { Length: > 0 })
                throw new TrialException("Dirty trial worktree retained for review");
            await _git.RemoveAsync(record.Plan.Repository, report.Worktree, report.Branch, report.SourceCommit);
            foreach (var sdkRoot in new[] { "sdk-baseline", "sdk-candidate", "home", "nuget", "temp" })
            {
                var path = TrialPaths.Child(store.Root, Path.Combine(id, sdkRoot));
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        Move(record, TrialPhase.Archived);
        return record;
    }

    private async Task RunStage(string stage, string executable, SdkSelection selection,
        string source, string root, TrialPlan plan, TrialRecord record, List<CommandEvidence> evidence, CancellationToken token)
    {
        var sdkRoot = Path.GetDirectoryName(executable)!;
        var home = TrialPaths.Child(root, "home");
        var nuget = TrialPaths.Child(root, "nuget");
        var temp = TrialPaths.Child(root, "temp");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(nuget);
        Directory.CreateDirectory(temp);
        var env = new Dictionary<string, string> {
            ["DOTNET_ROOT"] = sdkRoot,
            ["DOTNET_ROOT_" + (selection.Rid.EndsWith("arm64") ? "ARM64" : "X64")] = sdkRoot,
            ["DOTNET_CLI_HOME"] = home, ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1", ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["NUGET_PACKAGES"] = TrialPaths.Child(nuget, "packages"),
            ["NUGET_HTTP_CACHE_PATH"] = TrialPaths.Child(nuget, "http"),
            ["NUGET_PLUGINS_CACHE_PATH"] = TrialPaths.Child(nuget, "plugins"),
            ["NUGET_SCRATCH"] = TrialPaths.Child(nuget, "scratch"),
            ["NUDGE_TRIAL_ID"] = plan.Id,
            ["HOME"] = home, ["TMPDIR"] = temp, ["TMP"] = temp, ["TEMP"] = temp,
            ["PATH"] = sdkRoot + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };
        AssertResolverPaths(source, source);
        var logs = TrialPaths.Child(root, plan.Artifacts.LogsDirectory);
        async Task<CommandEvidence> Run(string[] args, string cwd)
        {
            var result = await _process.RunAsync(stage, executable, args, cwd, env, logs,
                plan.Budget.MaxProcessDuration, token);
            evidence.Add(result);
            store.Save(WithReport(record, evidence));
            if (result.TimedOut || token.IsCancellationRequested)
                throw new OperationCanceledException("Owned process timed out or cancelled", token);
            if (result.ExitCode != 0) throw new TrialException($"{stage}: dotnet {string.Join(' ', args)} exited {result.ExitCode}; see {result.StderrPath}");
            return result;
        }
        var version = await Run(["--version"], source);
        if (File.ReadAllText(version.StdoutPath).Trim() != selection.Version)
            throw new TrialException($"{stage}: selected SDK differs from {selection.Version}");
        await Run(["--info"], source);
        foreach (var command in plan.Commands)
        {
            var cwd = command.WorkingDirectory == "." ? source : TrialPaths.Child(source, command.WorkingDirectory);
            AssertResolverPaths(cwd, source);
            if (cwd != source)
            {
                var scoped = await Run(["--version"], cwd);
                if (File.ReadAllText(scoped.StdoutPath).Trim() != selection.Version)
                    throw new TrialException($"{stage}: nested project selected an unexpected SDK");
            }
            var result = await Run([command.Verb, ..command.Arguments], cwd);
            if (command.Verb == "test")
            {
                var output = File.ReadAllText(result.StdoutPath);
                var counts = Regex.Matches(output, @"(?:Total tests:\s*|Total:\s*)(\d+)", RegexOptions.IgnoreCase);
                var count = counts.Sum(m => int.Parse(m.Groups[1].Value));
                evidence[^1] = result with { TestCount = count };
                store.Save(WithReport(record, evidence));
                if (count == 0) throw new TrialException($"{stage}: zero discovered tests is not a test pass");
            }
            EnforceDisk(root, plan.Budget.MaxDiskBytes);
        }
    }

    private static TrialRecord WithReport(TrialRecord record, List<CommandEvidence> evidence)
    {
        if (record.Report is { } report)
        {
            var baselineWarnings = Warnings("baseline", evidence);
            var candidateWarnings = Warnings("candidate", evidence);
            record.Report = report with {
                Commands = [.. evidence],
                Baseline = Summarize("baseline", evidence),
                Candidate = Summarize("candidate", evidence),
                BaselineWarnings = baselineWarnings,
                CandidateWarnings = candidateWarnings,
                AddedWarnings = candidateWarnings.Except(baselineWarnings, StringComparer.Ordinal).ToArray()
            };
        }
        return record;
    }
    private static string[] Warnings(string stage, List<CommandEvidence> evidence) =>
        evidence.Where(c => c.Stage == stage).SelectMany(c =>
                new[] { c.StdoutPath, c.StderrPath }.Where(File.Exists).SelectMany(File.ReadLines))
            .Where(line => Regex.IsMatch(line, @"\bwarning\b", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();
    private static StageSummary Summarize(string stage, List<CommandEvidence> evidence)
    {
        TrialDimension Dimension(string verb)
        {
            var commands = evidence.Where(c => c.Stage == stage && c.Arguments.FirstOrDefault() == verb).ToArray();
            if (commands.Length == 0) return TrialDimension.NotExercised;
            return commands.All(c => c.ExitCode == 0 && !c.TimedOut)
                ? TrialDimension.Passed : TrialDimension.Failed;
        }
        var tests = evidence.Where(c => c.Stage == stage && c.Arguments.FirstOrDefault() == "test").ToArray();
        var status = Dimension("test");
        if (status == TrialDimension.Passed && tests.Length > 0 && tests.Sum(c => c.TestCount ?? 0) == 0)
            status = TrialDimension.NotExercised;
        return new StageSummary(Dimension("restore"), Dimension("build"), status,
            tests.Sum(c => c.TestCount ?? 0), TrialDimension.NotExercised, TrialDimension.NotExercised);
    }
    private static IEnumerable<string> Ancestors(string path)
    {
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p)) yield return p;
    }
    private static void AssertResolverPaths(string cwd, string source)
    {
        foreach (var ancestor in Ancestors(cwd))
        {
            var global = Path.Combine(ancestor, "global.json");
            if (!File.Exists(global)) continue;
            if (ancestor != source &&
                !ancestor.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new TrialException($"External ancestor global.json is unsupported: {global}");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(global),
                new System.Text.Json.JsonDocumentOptions {
                    AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                });
            if (document.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("paths", out _))
                throw new TrialException($"global.json paths override private SDK selection: {global}");
        }
    }
    private static void EnforceDisk(string root, long limit)
    {
        var bytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        if (bytes > limit) throw new TrialException($"Trial disk budget exceeded: {bytes} > {limit}");
    }
    private void Move(TrialRecord record, TrialPhase phase)
    {
        record.Phase = phase;
        record.Checkpoints.Add($"{DateTimeOffset.UtcNow:O} {phase}");
        store.Save(record);
    }
    private static void Validate(TrialPlan plan)
    {
        if (!Regex.IsMatch(plan.Id, @"^[a-zA-Z0-9-]{1,64}$") || plan.Revision < 1 ||
            !Regex.IsMatch(plan.SourceCommit, @"^[a-f0-9]{40}$") ||
            plan.Budget.MaxDiskBytes <= 0 || plan.Budget.MaxDuration <= TimeSpan.Zero ||
            plan.Budget.MaxProcessDuration <= TimeSpan.Zero ||
            plan.Commands.Length == 0 || plan.ProjectScopes.Length == 0)
            throw new TrialException("Invalid trial identity, source, budget, commands, or scope");
        if (plan.SdkOnly != (plan.PackageEdits.Length == 0))
            throw new TrialException("SDK-only and package edits are mutually exclusive");
        var trialDirectory = TrialPaths.Child(plan.TrialRoot, plan.Id);
        var logs = TrialPaths.Child(trialDirectory, plan.Artifacts.LogsDirectory);
        var diff = TrialPaths.Child(trialDirectory, plan.Artifacts.DiffFile);
        var artifactRoot = TrialPaths.Child(trialDirectory, "artifacts") + Path.DirectorySeparatorChar;
        if (!logs.StartsWith(artifactRoot, StringComparison.Ordinal) ||
            !diff.StartsWith(artifactRoot, StringComparison.Ordinal) ||
            diff.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.Ordinal) || logs == diff)
            throw new TrialException("Logs and diff must be disjoint paths inside trial-owned artifacts/");
        if (!plan.Commands.Any(c => c.Verb == "restore") || !plan.Commands.Any(c => c.Verb == "build"))
            throw new TrialException("Trial requires explicit restore and build commands");
        if (Path.GetFullPath(plan.TrialRoot).StartsWith(Path.GetFullPath(plan.Repository) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new TrialException("Trial root must be outside the original repository");
        foreach (var scope in plan.ProjectScopes) TrialPaths.Child(plan.Repository, scope);
        foreach (var edit in plan.PackageEdits)
        {
            var file = TrialPaths.Child(plan.Repository, edit.ProjectPath);
            if (!plan.ProjectScopes.Any(scope => file == TrialPaths.Child(plan.Repository, scope) ||
                file.StartsWith(TrialPaths.Child(plan.Repository, scope) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new TrialException("Package edit is outside approved project scopes");
            if (edit.FromVersion == edit.ToVersion) throw new TrialException("Package version must change");
        }
        foreach (var command in plan.Commands)
        {
            if (command.Verb is not ("restore" or "build" or "test") ||
                command.Arguments.Length == 0 ||
                command.Arguments.Any(a => a.Contains('\0') || a.StartsWith("--artifacts-path", StringComparison.Ordinal) ||
                    a.StartsWith("--output", StringComparison.Ordinal) || a == "-o" ||
                    a.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--property", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--test-adapter-path", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--configfile", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--packages", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--source", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("-bl", StringComparison.OrdinalIgnoreCase)))
                throw new TrialException("Only restore/build/test with trial-local outputs are supported");
            var target = TrialPaths.Child(plan.Repository, command.Arguments[0]);
            if (!plan.ProjectScopes.Any(scope => target == TrialPaths.Child(plan.Repository, scope) ||
                target.StartsWith(TrialPaths.Child(plan.Repository, scope) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new TrialException("Command target is outside approved project scopes");
            if (command.Arguments.Skip(1).Any(a => Path.IsPathRooted(a) || a is ".." || a.StartsWith("../", StringComparison.Ordinal)))
                throw new TrialException("External command argument paths are unsupported");
            if (command.WorkingDirectory != ".") TrialPaths.Child(plan.Repository, command.WorkingDirectory);
        }
    }

    private static string Digest(TrialPlan plan) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan))));
}

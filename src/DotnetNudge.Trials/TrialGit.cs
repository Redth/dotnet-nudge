using System.Diagnostics;

namespace DotnetNudge.Trials;

public sealed class TrialGit
{
    private static Task<string> GitAsync(string directory, params string[] args) =>
        GitAsync(directory, CancellationToken.None, TimeSpan.FromMinutes(2), args);

    private static async Task<string> GitAsync(string directory, CancellationToken cancellationToken,
        TimeSpan limit, params string[] args)
    {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new TrialException("Git could not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TrialException($"git {args[0]} exceeded its process limit");
        }
        if (process.ExitCode != 0) throw new TrialException($"git {args[0]} failed: {await error}");
        return (await output).Trim();
    }

    public async Task<bool> PreflightAsync(TrialPlan plan, CancellationToken token = default,
        TimeSpan? processLimit = null)
    {
        var repo = Path.GetFullPath(plan.Repository);
        Task<string> Run(params string[] args) =>
            GitAsync(repo, token, processLimit ?? TimeSpan.FromMinutes(2), args);
        if (await Run("rev-parse", "--show-toplevel") != repo)
            throw new TrialException("Repository must be its canonical Git root");
        if (await Run("rev-parse", "HEAD") != plan.SourceCommit ||
            await Run("rev-parse", $"{plan.SourceCommit}^{{commit}}") != plan.SourceCommit)
            throw new TrialException("Source commit must be the checkout's committed HEAD");
        if ((await Run("rev-parse", "--is-shallow-repository")) != "false")
            throw new TrialException("Shallow repository is unsupported");
        if (await Run("config", "--local", "--bool", "--default=false", "core.sparseCheckout") == "true")
            throw new TrialException("Sparse checkout is unsupported");
        if (await Run("config", "--local", "--bool", "--default=false", "extensions.worktreeConfig") == "true")
            throw new TrialException("Worktree-specific Git configuration is unsupported");
        if (await Run("ls-tree", "-r", "-z", plan.SourceCommit) is var entries &&
            entries.Split('\0').Any(x => x.StartsWith("160000 ", StringComparison.Ordinal) ||
                                         x.StartsWith("120000 ", StringComparison.Ordinal)))
            throw new TrialException("Submodules and symlinks are unsupported");
        var tracked = (await Run("ls-tree", "-r", "--name-only", "-z", plan.SourceCommit))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (tracked.Contains(".gitmodules", StringComparer.Ordinal))
            throw new TrialException("Submodules are unsupported");
        foreach (var name in tracked.Where(name => Path.GetFileName(name) == ".gitattributes"))
            if (System.Text.RegularExpressions.Regex.IsMatch(
                await Run("show", $"{plan.SourceCommit}:{name}"),
                @"\bfilter\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new TrialException("Git content filters (including LFS) are unsupported");
        foreach (var name in tracked.Where(name =>
                     name.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await Run("show", $"{plan.SourceCommit}:{name}");
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"<Import\b|<ProjectReference\b[^>]*\.\.([/\\]|%2[fF])", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new TrialException($"External/conditional imports or parent project references require manual review: {name}");
        }
        return (await Run("status", "--porcelain", "--untracked-files=all")).Length > 0;
    }

    public async Task<(string Branch, string Directory)> CreateAsync(TrialPlan plan, CancellationToken token = default,
        TimeSpan? processLimit = null)
    {
        var trialDirectory = TrialPaths.Child(plan.TrialRoot, Path.Combine(plan.Id, "source"));
        if (Directory.Exists(trialDirectory) || File.Exists(trialDirectory))
            throw new TrialException("Trial source path already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(trialDirectory)!);
        var branch = $"nudge-trial-{plan.Id}";
        await GitAsync(plan.Repository, token, processLimit ?? TimeSpan.FromMinutes(2),
            "worktree", "add", "-b", branch, trialDirectory, plan.SourceCommit);
        TrialPaths.AssertNoLinks(trialDirectory);
        return (branch, trialDirectory);
    }

    public Task<string> StatusAsync(string source, CancellationToken token = default,
        TimeSpan? processLimit = null) => GitAsync(source, token, processLimit ?? TimeSpan.FromMinutes(2),
            "status", "--porcelain", "--untracked-files=all");
    public Task<string> DiffAsync(string source, CancellationToken token = default,
        TimeSpan? processLimit = null) => GitAsync(source, token, processLimit ?? TimeSpan.FromMinutes(2),
            "diff", "HEAD", "--");

    public async Task RemoveAsync(string repository, string source, string branch, string baseCommit)
    {
        if (await GitAsync(source, "rev-parse", "--show-toplevel") != source ||
            await GitAsync(source, "symbolic-ref", "--short", "HEAD") != branch ||
            await GitAsync(source, "rev-parse", "HEAD") != baseCommit)
            throw new TrialException("Trial worktree identity/branch changed; preserve for manual review");
        if ((await StatusAsync(source)).Length != 0)
            throw new TrialException("Dirty trial worktree retained for review");
        await GitAsync(repository, "worktree", "remove", source);
        await GitAsync(repository, "branch", "-d", branch);
    }
}

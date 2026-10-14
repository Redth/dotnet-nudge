using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

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
        if (tracked.Any(name => name.Split('/').Any(part => part is "bin" or "obj")))
            throw new TrialException("Tracked bin/obj outputs make candidate compilation identity indeterminate");
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
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(text),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (XmlException ex) { throw new TrialException($"Cannot inspect project imports in {name}: {ex.Message}"); }
            foreach (var node in document.Descendants().Where(n => n.Name.LocalName is "Import" or "ProjectReference"))
            {
                var value = (string?)node.Attribute(node.Name.LocalName == "Import" ? "Project" : "Include");
                if (string.IsNullOrWhiteSpace(value) || value.Contains("$(", StringComparison.Ordinal) ||
                    value.Contains("@(", StringComparison.Ordinal) || value.IndexOfAny(['*', '?', '%', ';']) >= 0 ||
                    Path.IsPathRooted(value) ||
                    System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:[/\\]|^[/\\]"))
                    throw new TrialException($"Indeterminate or absolute project import/reference in {name}: {value}");
                var target = Path.GetFullPath(Path.Combine(repo, Path.GetDirectoryName(name) ?? "",
                    value.Replace('\\', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !tracked.Contains(Path.GetRelativePath(repo, target).Replace('\\', '/'), StringComparer.Ordinal))
                    throw new TrialException($"External or uncommitted project import/reference in {name}: {value}");
            }
        }
        CheckExternalBuildConfig(plan);
        return (await Run("status", "--porcelain", "--untracked-files=all")).Length > 0;
    }

    public static void CheckExternalBuildConfig(TrialPlan plan)
    {
        var trialRoot = Path.GetFullPath(plan.TrialRoot);
        for (DirectoryInfo? parent = new(trialRoot); parent is not null; parent = parent.Parent)
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets",
                         "Directory.Packages.props", "NuGet.Config", "nuget.config" })
                if (File.Exists(Path.Combine(parent.FullName, name)))
                    throw new TrialException($"External ancestor build configuration is unsupported: {Path.Combine(parent.FullName, name)}");
    }

    public async Task<(string Branch, string Directory)> CreateAsync(TrialPlan plan, bool candidate = false,
        CancellationToken token = default, TimeSpan? processLimit = null)
    {
        var trialDirectory = TrialPaths.Child(plan.TrialRoot, Path.Combine(plan.Id, candidate ? "candidate-source" : "source"));
        if (Directory.Exists(trialDirectory) || File.Exists(trialDirectory))
            throw new TrialException("Trial source path already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(trialDirectory)!);
        var branch = $"nudge-trial-{plan.Id}" + (candidate ? "-candidate" : "");
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

    public async Task<OriginalFingerprint> FingerprintAsync(string repository,
        CancellationToken token = default, TimeSpan? processLimit = null)
    {
        Task<string> Run(params string[] args) =>
            GitAsync(repository, token, processLimit ?? TimeSpan.FromMinutes(2), args);
        var head = await Run("rev-parse", "HEAD");
        var branch = await Run("rev-parse", "--abbrev-ref", "HEAD");
        var indexPath = await Run("rev-parse", "--git-path", "index");
        if (!Path.IsPathRooted(indexPath)) indexPath = Path.Combine(repository, indexPath);
        if (!File.Exists(indexPath)) throw new TrialException("Original Git index is missing");
        await using var index = File.OpenRead(indexPath);
        var indexHash = Convert.ToHexString(await SHA256.HashDataAsync(index, token));
        var tracked = (await Run("ls-files", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var untracked = (await Run("ls-files", "--others", "--exclude-standard", "-z"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in tracked.Concat(untracked).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(repository, name));
            if (!path.StartsWith(Path.GetFullPath(repository) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new TrialException($"Original Git path escaped repository: {name}");
            for (var parent = Path.GetDirectoryName(path); parent is not null &&
                 parent != Path.GetFullPath(repository); parent = Path.GetDirectoryName(parent))
                if (new DirectoryInfo(parent).LinkTarget is not null)
                    throw new TrialException($"Original Git path has a symlinked parent: {name}");
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
            var info = new FileInfo(path);
            if (info.LinkTarget is { } link)
                hash.AppendData(Encoding.UTF8.GetBytes("link:" + link));
            else if (info.Exists)
            {
                hash.AppendData(Encoding.UTF8.GetBytes("file:"));
                await using var stream = File.OpenRead(path);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            else hash.AppendData(Encoding.UTF8.GetBytes("missing"));
            hash.AppendData([0]);
        }
        return new OriginalFingerprint(head, branch, indexHash,
            Convert.ToHexString(hash.GetHashAndReset()), await Run("status", "--porcelain", "--untracked-files=all"));
    }

    public async Task EnsureCleanOwnedAsync(string repository, string source, string branch, string baseCommit)
    {
        var registered = (await GitAsync(repository, "worktree", "list", "--porcelain"))
            .Split('\n').Any(line => line == "worktree " + source);
        if (!Directory.Exists(source))
        {
            if (registered) throw new TrialException("Missing worktree is still registered; manual review required");
        }
        else
        {
            if (!registered || await GitAsync(source, "rev-parse", "--show-toplevel") != source ||
                await GitAsync(source, "symbolic-ref", "--short", "HEAD") != branch ||
                await GitAsync(source, "rev-parse", "HEAD") != baseCommit)
                throw new TrialException("Trial worktree identity/branch changed; preserve for manual review");
            if ((await StatusAsync(source)).Length != 0)
                throw new TrialException("Dirty trial worktree retained for review");
        }
        var exists = await GitAsync(repository, "branch", "--list", "--format=%(refname:short)", branch);
        if (exists.Length > 0 &&
            (exists != branch || await GitAsync(repository, "rev-parse", $"refs/heads/{branch}") != baseCommit))
            throw new TrialException("Owned trial branch no longer points at approved source commit");
        if (Directory.Exists(source) && exists.Length == 0)
            throw new TrialException("Trial worktree lost its owned branch");
    }

    public async Task RemoveWorktreeAsync(string repository, string source)
    {
        if (!Directory.Exists(source)) return;
        await GitAsync(repository, "worktree", "remove", source);
    }

    public async Task DeleteOwnedBranchAsync(string repository, string branch, string baseCommit)
    {
        if ((await GitAsync(repository, "worktree", "list", "--porcelain"))
            .Split('\n').Any(line => line == $"branch refs/heads/{branch}"))
            throw new TrialException($"Owned branch remains checked out in a worktree: {branch}");
        var exists = await GitAsync(repository, "branch", "--list", "--format=%(refname:short)", branch);
        if (exists == branch)
            await GitAsync(repository, "update-ref", "-d", $"refs/heads/{branch}", baseCommit);
    }
}

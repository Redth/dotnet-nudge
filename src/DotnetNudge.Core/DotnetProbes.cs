using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace DotnetNudge.Core;

public sealed record ProbeSnapshot(
    string Executable,
    IReadOnlyDictionary<string, string> EffectiveSdks,
    string? InstalledSdks,
    string? InstalledRuntimes,
    string? LocalTools,
    IReadOnlyList<Coverage> Coverage);

public static class DotnetProbes
{
    const int MaxOutput = 128 * 1024;

    public static async Task<ProbeSnapshot> ReadAsync(WorkspaceInventory inventory,
        CancellationToken cancellationToken = default)
    {
        var coverage = new List<Coverage>();
        string? executable;
        try { executable = FindExecutable(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            coverage.Add(new("dotnet-probe", Assessment.Blocked,
                $"Could not inspect the configured dotnet executable: {ex.Message}"));
            return new("", new Dictionary<string, string>(), null, null, null, coverage);
        }
        if (executable is null)
        {
            coverage.Add(new("dotnet-probe", Assessment.Blocked,
                "A native dotnet executable was not found; no SDK or runtime was observed."));
            return new("", new Dictionary<string, string>(), null, null, null, coverage);
        }
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopes = inventory.Projects.Select(project =>
            Path.GetDirectoryName(Path.Combine(inventory.Root, project))!)
            .Append(inventory.Root).Distinct(StringComparer.Ordinal).ToArray();
        if (scopes.Length > 8)
            coverage.Add(new("effective-sdk", Assessment.Partial,
                $"Only eight of {scopes.Length} project execution scopes were probed."));
        foreach (var scope in scopes.Take(8))
        {
            var key = Path.GetRelativePath(inventory.Root, scope).Replace('\\', '/');
            var observed = await Probe(executable, scope, ["--version"], "effective-sdk:" + key,
                coverage, cancellationToken);
            if (observed is not null) effective[key] = observed.Trim();
        }
        var sdks = await Probe(executable, inventory.Root, ["--list-sdks"],
            "installed-sdks", coverage, cancellationToken);
        var runtimes = await Probe(executable, inventory.Root, ["--list-runtimes"],
            "installed-runtimes", coverage, cancellationToken);
        var tools = await Probe(executable, inventory.Root, ["tool", "list", "--local"],
            "local-tools", coverage, cancellationToken);
        return new(executable, effective, sdks, runtimes, tools, coverage);
    }

    static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } sdkRoot)
            roots.Add(sdkRoot);
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var root in roots)
        {
            var file = Path.Combine(root, name);
            if (!File.Exists(file)) continue;
            file = WorkspaceDiscovery.Canonical(file);
            return IsNativeExecutable(file) ? file : null;
        }
        return null;
    }

    public static bool IsNativeExecutable(string file)
    {
        if (!File.Exists(file)) return false;
        file = WorkspaceDiscovery.Canonical(file);
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            return false;
        using var stream = File.OpenRead(file);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4) return false;
        if (OperatingSystem.IsWindows())
            return signature[0] == 'M' && signature[1] == 'Z';
        if (OperatingSystem.IsLinux())
            return signature[0] == 0x7f && signature[1] == 'E'
                && signature[2] == 'L' && signature[3] == 'F';
        var magic = Convert.ToHexString(signature);
        return magic is "FEEDFACF" or "CFFAEDFE" or "CAFEBABE" or "BEBAFECA";
    }

    static async Task<string?> Probe(string executable, string scope, string[] arguments,
        string check, List<Coverage> coverage, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = scope,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        try
        {
            using var process = Process.Start(start)
                ?? throw new IOException("dotnet probe failed to start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var stdout = ReadLimited(process.StandardOutput, MaxOutput, timeout.Token);
                var stderr = ReadLimited(process.StandardError, 8192, timeout.Token);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
                var output = await stdout;
                var diagnostics = await stderr;
                if (process.ExitCode != 0)
                {
                    coverage.Add(new(check, Assessment.Blocked,
                        $"dotnet {string.Join(' ', arguments)} exited {process.ExitCode}: {diagnostics.Trim()}"));
                    return null;
                }
                coverage.Add(new(check, Assessment.Confirmed,
                    $"Read-only dotnet {string.Join(' ', arguments)} observed in {scope}."));
                return output;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                coverage.Add(new(check, Assessment.Blocked, "Read-only dotnet probe exceeded four seconds."));
                return null;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception)
        {
            coverage.Add(new(check, Assessment.Blocked,
                ex.Message.Length > 300 ? ex.Message[..300] : ex.Message));
            return null;
        }
    }

    static async Task<string> ReadLimited(StreamReader reader, int max, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (result.Length + count > max)
                throw new InvalidDataException("dotnet probe output exceeds its size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

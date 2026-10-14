using System.Diagnostics;

namespace DotnetNudge.Trials;

public sealed class TrialProcess
{
    public async Task<CommandEvidence> RunAsync(
        string stage, string executable, IEnumerable<string> arguments, string workingDirectory,
        Dictionary<string, string> environment, string logDirectory, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        var index = Directory.GetFiles(logDirectory, "*.stdout.log").Length;
        var stdout = Path.Combine(logDirectory, $"{index:D3}.stdout.log");
        var stderr = Path.Combine(logDirectory, $"{index:D3}.stderr.log");
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        var language = Environment.GetEnvironmentVariable("LANG");
        start.Environment.Clear();
        if (language is not null) start.Environment["LANG"] = language;
        foreach (var (key, value) in environment) start.Environment[key] = value;
        var timer = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new TrialException($"Failed to start {executable}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var outTask = DrainAsync(process.StandardOutput, stdout);
        var errTask = DrainAsync(process.StandardError, stderr);
        bool timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) {
            timedOut = !cancellationToken.IsCancellationRequested;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        await Task.WhenAll(outTask, errTask);
        timer.Stop();
        return new CommandEvidence(stage, executable, start.ArgumentList.ToArray(), workingDirectory,
            environment.Where(kv => kv.Key != "PATH").ToDictionary(), process.ExitCode, timedOut,
            stdout, stderr, timer.ElapsedMilliseconds);
    }

    private static async Task DrainAsync(StreamReader input, string path)
    {
        await using var output = File.Create(path);
        await input.BaseStream.CopyToAsync(output);
    }
}

using System.Text.Json;
using DotnetNudge.Core;

return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine("""
            dotnet-nudge 1.0
            scan --workspace PATH [--state-dir PATH] --json
            status --workspace PATH [--state-dir PATH] --json
            decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until TIMESTAMP] [--state-dir PATH] --json
            doctor [--workspace PATH] [--state-dir PATH] --json
            hook --host copilot|claude --event session-start|prompt-submit|post-tool-use|session-end [--state-dir PATH]
            version
            """);
        return 0;
    }
    if (args[0] is "version" or "--version")
    {
        Console.WriteLine("dotnet-nudge 1.0.0");
        return 0;
    }
    var hook = args[0] == "hook";
    try
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json")
            {
                options.Add("--json", "true");
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) ||
                i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected value for option {args[i]}.");
            options.Add(args[i], args[++i]);
        }
        var allowed = args[0] switch
        {
            "scan" or "status" => new[] { "--workspace", "--state-dir", "--json" },
            "decision" => ["--workspace", "--state-dir", "--json", "--finding", "--revision", "--action", "--until"],
            "doctor" => ["--workspace", "--state-dir", "--json"],
            "hook" => ["--host", "--event", "--state-dir"],
            _ => throw new ArgumentException($"Unknown command {args[0]}.")
        };
        if (options.Keys.Except(allowed).Any())
            throw new ArgumentException($"Unknown option: {options.Keys.Except(allowed).First()}.");
        string Required(string key) => options.TryGetValue(key, out var value) ? value :
            throw new ArgumentException($"Missing required option {key}.");
        if (!hook && args[0] != "doctor" && !options.ContainsKey("--json"))
            throw new ArgumentException("This command requires --json.");
        var stateDir = options.GetValueOrDefault("--state-dir") ?? NudgeState.DefaultDirectory;
        if (args[0] == "doctor")
        {
            var root = options.GetValueOrDefault("--workspace");
            var identity = root is null ? ((string Repository, string Worktree)?)null
                : WorkspaceDiscovery.Identities(root);
            Console.WriteLine(JsonSerializer.Serialize(new DoctorReport(Contract.Version,
                Path.GetFullPath(stateDir), root, identity?.Repository, identity?.Worktree,
                Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR") is null
                    ? ReleaseMetadata.IndexUrl : "local fixture",
                "SDK selection, NuGet graphs, MCP worker and trials are not implemented in this foundation."),
                Contract.Json));
            return 0;
        }
        using var state = new NudgeState(stateDir);
        if (hook)
        {
            var payload = await Console.In.ReadToEndAsync();
            if (payload.Length > 1024 * 1024) throw new ArgumentException("Hook payload exceeds 1 MiB.");
            Console.WriteLine(HookAdapter.Handle(Required("--host"), Required("--event"), payload, state));
            return 0;
        }
        var workspace = WorkspaceDiscovery.Canonical(Required("--workspace"));
        var (repository, worktree) = WorkspaceDiscovery.Identities(workspace);
        object result;
        switch (args[0])
        {
            case "scan":
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    var source = new ReleaseMetadata(state, client,
                        fixtureDirectory: Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR"));
                    result = await new NudgeScanner(source).ScanAsync(workspace);
                    state.Save((ScanReport)result);
                }
                break;
            case "status":
                result = state.Status(repository, worktree);
                break;
            case "decision":
                if (!Enum.TryParse<DecisionAction>(Required("--action").Replace("-", ""), true, out var action))
                    throw new ArgumentException("Invalid decision action.");
                DateTimeOffset? until = null;
                if (options.TryGetValue("--until", out var text))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(text,
                        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$") ||
                        !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var timestamp))
                        throw new ArgumentException("--until requires an RFC3339 timestamp with a timezone.");
                    until = timestamp;
                }
                result = state.Decide(repository, worktree, Required("--finding"),
                    Required("--revision"), action, until);
                break;
            default: throw new ArgumentException($"Unknown command {args[0]}.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, Contract.Json));
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or
        InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"dotnet-nudge: {ex.Message}");
        if (!hook && args.Contains("--json"))
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = Contract.Version,
                error = ex.Message }));
        else if (hook) Console.WriteLine("{}");
        return hook ? 0 : 2;
    }
}

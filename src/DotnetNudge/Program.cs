using System.Text.Json;
using DotnetNudge;
using DotnetNudge.Core;

return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine("""
            dotnet-nudge 1.0
            mcp [--state-dir PATH]
            scan --workspace PATH [--state-dir PATH] --json
            status --workspace PATH [--state-dir PATH] --json
            package-context --workspace PATH [--handle EXACT_HANDLE] [--state-dir PATH] --json
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
            "package-context" => ["--workspace", "--handle", "--state-dir", "--json"],
            "mcp" => ["--state-dir"],
            "decision" => ["--workspace", "--state-dir", "--json", "--finding", "--revision", "--action", "--until"],
            "doctor" => ["--workspace", "--state-dir", "--json"],
            "hook" => ["--host", "--event", "--state-dir"],
            _ => throw new ArgumentException($"Unknown command {args[0]}.")
        };
        if (options.Keys.Except(allowed).Any())
            throw new ArgumentException($"Unknown option: {options.Keys.Except(allowed).First()}.");
        string Required(string key) => options.TryGetValue(key, out var value) ? value :
            throw new ArgumentException($"Missing required option {key}.");
        if (!hook && args[0] is not ("doctor" or "mcp") && !options.ContainsKey("--json"))
            throw new ArgumentException("This command requires --json.");
        var stateDir = options.GetValueOrDefault("--state-dir") ?? NudgeState.DefaultDirectory;
        if (args[0] == "mcp")
        {
            await McpEndpoint.RunAsync(stateDir);
            return 0;
        }
        if (args[0] == "doctor")
        {
            var root = options.GetValueOrDefault("--workspace");
            if (root is not null && !Directory.Exists(root))
                throw new DirectoryNotFoundException(root);
            var identity = root is null ? ((string Repository, string Worktree)?)null
                : WorkspaceDiscovery.Identities(root);
            using var doctorState = new NudgeState(stateDir);
            var coverage = root is null ? null : doctorState.Status(identity!.Value.Repository,
                identity.Value.Worktree, WorkspaceDiscovery.ScopeId(root)).Coverage;
            Console.WriteLine(JsonSerializer.Serialize(new DoctorReport(Contract.Version,
                Path.GetFullPath(stateDir), root, identity?.Repository, identity?.Worktree,
                root is null ? null : WorkspaceDiscovery.ScopeId(root),
                Environment.GetEnvironmentVariable("NUDGE_RELEASES_DIR") is null
                    ? ReleaseMetadata.IndexUrl : "local fixture",
                "MCP worker available while host session is active; restored package graphs and read-only probes are supported, trials await integration.",
                coverage),
                Contract.Json));
            return 0;
        }
        using var state = new NudgeState(stateDir);
        using var service = new NudgeService(state);
        if (hook)
        {
            var buffer = new char[8192];
            using var text = new StringWriter();
            int count;
            while ((count = await Console.In.ReadAsync(buffer.AsMemory())) > 0)
            {
                if (text.GetStringBuilder().Length + count > 1024 * 1024)
                    throw new InvalidDataException("Hook payload exceeds 1 MiB.");
                text.Write(buffer, 0, count);
            }
            var payload = text.ToString();
            Console.WriteLine(HookAdapter.Handle(Required("--host"), Required("--event"), payload, state));
            return 0;
        }
        var workspace = WorkspaceDiscovery.Canonical(Required("--workspace"));
        object result;
        switch (args[0])
        {
            case "scan":
                result = await service.ScanAsync(workspace);
                break;
            case "status":
                result = service.Status(workspace);
                break;
            case "package-context":
                result = await service.PackageContextAsync(workspace, options.GetValueOrDefault("--handle"));
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
                result = service.Decide(workspace, Required("--finding"),
                    Required("--revision"), action, until);
                break;
            default: throw new ArgumentException($"Unknown command {args[0]}.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, Contract.Json));
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or JsonException or
        InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
    {
        var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        Console.Error.WriteLine($"dotnet-nudge: {message}");
        if (!hook && args.Contains("--json"))
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = Contract.Version,
                error = message }));
        else if (hook) Console.WriteLine("{}");
        return hook ? 0 : 2;
    }
}

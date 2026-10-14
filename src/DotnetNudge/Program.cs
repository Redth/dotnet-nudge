using System.Text.Json;
using DotnetNudge;
using DotnetNudge.Core;
using DotnetNudge.Trials;

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
            package-assess --workspace PATH --project RELATIVE_PROJECT --framework TFM --package ID --sources NAME[,NAME...] --approve-metadata [--rid RID] [--state-dir PATH] --json
            toolchains --workspace PATH [--state-dir PATH] --json
            trial root|propose|approve|execute|status|cancel|cleanup --workspace PATH [--plan FILE] [--id ID] [--revision N] [--state-dir PATH] --json
            preferences --workspace PATH [--enable-category NAME] [--state-dir PATH] --json
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
        var trialOperation = args[0] == "trial"
            ? args.Length > 1 ? args[1] : throw new ArgumentException("Trial operation is required.")
            : null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = trialOperation is null ? 1 : 2; i < args.Length; i++)
        {
            if (args[i] is "--json" or "--approve-metadata")
            {
                options.Add(args[i], "true");
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) ||
                i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected value for option {args[i]}.");
            options.Add(args[i], args[++i]);
        }
        var allowed = args[0] switch
        {
            "scan" or "status" or "toolchains" => new[] { "--workspace", "--state-dir", "--json" },
            "package-context" => ["--workspace", "--handle", "--state-dir", "--json"],
            "package-assess" => ["--workspace", "--project", "--framework", "--package",
                "--sources", "--approve-metadata", "--rid", "--state-dir", "--json"],
            "trial" => trialOperation switch
            {
                "root" => ["--workspace", "--state-dir", "--json"],
                "propose" => ["--workspace", "--plan", "--state-dir", "--json"],
                "approve" or "execute" => ["--workspace", "--id", "--revision", "--state-dir", "--json"],
                "status" or "cancel" or "cleanup" => ["--workspace", "--id", "--state-dir", "--json"],
                _ => throw new ArgumentException("Unknown trial operation.")
            },
            "preferences" => ["--workspace", "--enable-category", "--state-dir", "--json"],
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
            if (root is not null && !Path.IsPathFullyQualified(root))
                throw new ArgumentException("Workspace path must be absolute.");
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
                "MCP worker available while host session is active; restored package graphs and read-only probes are supported. Isolated trials require exact-plan approval in an interactive human terminal and trusted project code (not an OS sandbox); execution is unsupported on Windows.",
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
        var workspace = Required("--workspace");
        if (!Path.IsPathFullyQualified(workspace))
            throw new ArgumentException("Workspace path must be absolute.");
        object result;
        switch (args[0])
        {
            case "scan":
                result = await service.ScanAsync(workspace);
                break;
            case "status":
                result = service.Status(workspace);
                break;
            case "toolchains":
                result = await service.ToolchainsAsync(workspace);
                break;
            case "package-context":
                result = await service.PackageContextAsync(workspace, options.GetValueOrDefault("--handle"));
                break;
            case "package-assess":
                result = await service.AssessPackageAsync(workspace, Required("--project"),
                    Required("--framework"), options.GetValueOrDefault("--rid"), Required("--package"),
                    Required("--sources").Split(',', StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    options.ContainsKey("--approve-metadata"));
                break;
            case "trial":
                int Revision() => int.TryParse(Required("--revision"), out var value) && value > 0
                    ? value : throw new ArgumentException("Trial revision must be a positive integer.");
                async Task<TrialStatusReport> Execute()
                {
                    using var cancellation = new CancellationTokenSource();
                    ConsoleCancelEventHandler handler = (_, signal) =>
                    {
                        signal.Cancel = true;
                        cancellation.Cancel();
                    };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        return await service.ExecuteTrialAsync(workspace, Required("--id"),
                            Revision(), cancellation.Token);
                    }
                    finally { Console.CancelKeyPress -= handler; }
                }
                result = trialOperation switch
                {
                    "root" => new { schemaVersion = Contract.Version, trialRoot = service.TrialRoot },
                    "propose" => service.ProposeTrial(workspace, ReadPlan(Required("--plan"))),
                    "approve" => ConfirmTrialApproval(service, workspace, Required("--id"), Revision()),
                    "execute" => await Execute(),
                    "status" => service.TrialStatus(workspace, Required("--id")),
                    "cancel" => service.CancelTrial(workspace, Required("--id")),
                    "cleanup" => await service.CleanupTrialAsync(workspace, Required("--id")),
                    _ => throw new ArgumentException("Unknown trial operation.")
                };
                break;
            case "preferences":
                result = options.TryGetValue("--enable-category", out var category)
                    ? service.EnableCategory(workspace, category) : service.Preferences(workspace);
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
        InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException
        or TrialException or OperationCanceledException or System.ComponentModel.Win32Exception)
    {
        var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        Console.Error.WriteLine($"dotnet-nudge: {message}");
        if (!hook && args.Contains("--json"))
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = Contract.Version,
                error = message }));
        else if (hook) Console.WriteLine("{}");
        return hook ? 0 : 2;
    }

    static TrialPlan ReadPlan(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Trial plan file must be an absolute path.");
        var text = DotnetNudge.Toolchains.SafeMetadataReader.ReadFile(
            Path.GetDirectoryName(path)!, Path.GetFileName(path), 1024 * 1024);
        return JsonSerializer.Deserialize<TrialPlan>(text, Contract.Json)
            ?? throw new InvalidDataException("Trial plan cannot be null.");
    }

    static TrialStatusReport ConfirmTrialApproval(NudgeService service, string workspace, string id,
        int revision)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            throw new InvalidOperationException(
                "Trial approval requires a local interactive terminal; redirected/noninteractive agents cannot approve.");
        var status = service.TrialStatus(workspace, id);
        if (status.Phase != "proposed" || status.Revision != revision)
            throw new InvalidOperationException("Trial is no longer proposed at the requested revision.");
        var digest = TrialRunner.Digest(status.Plan);
        Console.Error.WriteLine(JsonSerializer.Serialize(status.Plan,
            new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        Console.Error.WriteLine($"Workspace: {workspace}\nPlan revision: {revision}\nPlan SHA-256: {digest}");
        Console.Error.Write($"To approve execution of the displayed plan, type: approve {id} {revision} {digest}\n> ");
        var input = Console.ReadLine();
        if (input != $"approve {id} {revision} {digest}")
            throw new InvalidOperationException("Trial approval was not confirmed; no approval recorded.");
        return service.ApproveTrial(workspace, id, revision, digest);
    }
}

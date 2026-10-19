using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotnetNudge.Toolchains;
using NuGet.Versioning;

namespace DotnetNudge.Core;

public sealed record AgentProjectSignal(string Topic, string Evidence);
public sealed record InstalledAgentResource(string Repository, string Name, string? Version,
    string? Revision, string Scope, bool Enabled, bool ManagedByNudge);
public sealed record AgentToolInventory(int SchemaVersion, string WorkspaceId, string Host,
    DateTimeOffset ObservedAt, IReadOnlyList<InstalledAgentResource> Resources)
{
    public bool Complete { get; init; }
}
public sealed record AgentToolRecommendation(AgentToolResource Resource, string Action,
    string? InstalledVersion, string? InstalledRevision, IReadOnlyList<string> Reasons,
    IReadOnlyList<AgentSkillInfo> RelevantSkills, string Revision, Assessment Assessment);
public sealed record AgentCatalogStatus(string Repository, Assessment Freshness, bool Complete,
    DateTimeOffset? RetrievedAt, int ResourceCount);
public sealed record AgentToolsReport(int SchemaVersion, string WorkspaceId, DateTimeOffset CheckedAt,
    IReadOnlyList<AgentProjectSignal> Context, AgentToolInventory? Inventory,
    IReadOnlyList<AgentToolRecommendation> Recommendations, IReadOnlyList<Coverage> Coverage)
{
    public IReadOnlyList<AgentCatalogStatus> Sources { get; init; } = [];
}
public sealed record AgentToolsScanResult(AgentToolsReport Report, IReadOnlyList<Finding> Findings);

public static class AgentTools
{
    static readonly IReadOnlyDictionary<string, string[]> TopicWords = new Dictionary<string, string[]>
    {
        ["maui"] = ["maui", "devflow", "mobile"],
        ["web"] = ["aspnetcore", "asp.net", "web development"],
        ["blazor"] = ["blazor"],
        ["data"] = ["entity framework", "data access", "ef core", "efcore"],
        ["ai"] = ["llm", "agentic", "mcp", "machine learning", "rag pipelines"],
        ["tests"] = ["test execution", "testing", "testability", "mstest", "xunit"],
        ["packages"] = ["nuget", "package management"],
        ["net11"] = [".net 11", "dotnet11"],
        ["winforms"] = ["winforms", "windows forms"],
        ["wpf"] = ["wpf", "windows presentation foundation"],
        ["bindings"] = ["platform bindings", "java/kotlin", "slim bindings"],
        ["templates"] = ["template discovery", "template authoring", "template engine"],
        ["migration"] = ["migrating", "migration", "upgrading .net", "framework versions"]
    };

    public static async Task<AgentToolsScanResult> AssessAsync(WorkspaceInventory workspace,
        NudgeState state, string? inventoryJson = null, CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow;
        var key = "agent-inventory:" + workspace.WorkspaceId;
        AgentToolInventory? inventory = null;
        if (inventoryJson is not null)
        {
            inventory = ReadInventory(inventoryJson, workspace.WorkspaceId, now);
            state.Cache(key, JsonSerializer.Serialize(inventory, Contract.Json), null);
        }
        else if (state.Cached(key) is { } cached)
            inventory = ReadInventory(cached.Body, workspace.WorkspaceId, now);
        var catalogs = new List<AgentToolCatalog>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (Environment.GetEnvironmentVariable("NUDGE_AGENT_CATALOG_FILE") is { Length: > 0 } fixture)
        {
            var text = SafeMetadataReader.ReadFile(WorkspaceDiscovery.Canonical(Path.GetDirectoryName(Path.GetFullPath(fixture))!),
                Path.GetFileName(fixture), 4 * 1024 * 1024);
            catalogs.AddRange(JsonSerializer.Deserialize<AgentToolCatalog[]>(text, Contract.Json)
                ?? throw new InvalidDataException("Offline helper catalogs cannot be null."));
            if (catalogs.Any(c => c is null || !AgentToolCatalogSource.Repositories.Contains(c.Repository)) ||
                catalogs.Select(c => c.Repository).Distinct().Count() != catalogs.Count)
                throw new InvalidDataException("Offline helper catalogs must identify distinct configured sources.");
            for (var i = 0; i < catalogs.Count; i++)
            {
                var catalog = catalogs[i];
                if (catalog.Resources is null || catalog.Coverage is null ||
                    catalog.Resources.Any(resource => resource is null || resource.Repository != catalog.Repository ||
                        resource.Id != $"{catalog.Repository}:{resource.Name}" ||
                        string.IsNullOrWhiteSpace(resource.Name) || resource.Revision is null ||
                        resource.Skills is null || resource.Capabilities is null ||
                        resource.Url is null ||
                        !resource.Url.StartsWith($"https://github.com/{catalog.Repository}/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Offline catalog resources must retain their exact configured source identities.");
                if (catalog.RetrievedAt is null)
                    catalogs[i] = catalog with { Freshness = Assessment.Unknown };
                else if (catalog.RetrievedAt > now.AddMinutes(5) ||
                    now - catalog.RetrievedAt > TimeSpan.FromHours(24))
                    catalogs[i] = catalog with { Freshness = Assessment.Stale };
            }
            foreach (var missing in AgentToolCatalogSource.Repositories.Except(catalogs.Select(c => c.Repository)))
                catalogs.Add(new(missing, Assessment.Unknown, false, [],
                    [new("agent-catalog:" + missing, Assessment.Unknown, "Offline catalog evidence was not supplied for this source.")]));
        }
        else
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(8) };
            var source = new AgentToolCatalogSource(state, client);
            foreach (var repository in AgentToolCatalogSource.Repositories)
            {
                try { catalogs.Add(await source.ReadAsync(repository, deadline.Token)); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    catalogs.Add(new(repository, Assessment.Unknown, false, [],
                        [new("agent-catalog:" + repository, Assessment.Unknown,
                            "Helper catalog metadata exceeded its 30-second budget; no absence or installation state was inferred.")]));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return Evaluate(workspace, catalogs, inventory, now);
    }

    public static AgentToolInventory ReadInventory(string json, string workspaceId, DateTimeOffset now)
    {
        if (Encoding.UTF8.GetByteCount(json) > 256 * 1024)
            throw new InvalidDataException("Host helper inventory exceeds 256 KiB.");
        var inventory = JsonSerializer.Deserialize<AgentToolInventory>(json, Contract.Json)
            ?? throw new InvalidDataException("Host helper inventory cannot be null.");
        if (inventory.SchemaVersion != 1 || inventory.WorkspaceId != workspaceId ||
            string.IsNullOrWhiteSpace(inventory.Host) || inventory.Host.Length > 64 ||
            inventory.ObservedAt > now.AddMinutes(5) ||
            inventory.Resources is null || inventory.Resources.Count > 512 ||
            inventory.Resources.Any(resource =>
                resource is null || !AgentToolCatalogSource.Repositories.Contains(resource.Repository) ||
                string.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > 64 ||
                resource.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) ||
                resource.Version is { Length: > 128 } || resource.Revision is { Length: > 64 } ||
                resource.Scope is not ("project" or "user")) ||
            inventory.Resources.Select(resource => (resource.Repository, resource.Name)).Distinct().Count()
                != inventory.Resources.Count)
            throw new InvalidDataException("Host inventory must name this exact workspace, host and distinct source-qualified project/user resources.");
        return inventory;
    }

    public static AgentToolsScanResult Evaluate(WorkspaceInventory workspace,
        IReadOnlyList<AgentToolCatalog> catalogs, AgentToolInventory? inventory, DateTimeOffset now)
    {
        var coverage = catalogs.SelectMany(c => c.Coverage).ToList();
        var context = ProjectContext(workspace, coverage);
        var currentInventory = inventory is not null && now - inventory.ObservedAt <= TimeSpan.FromHours(24);
        if (!currentInventory)
            coverage.Add(new("agent-inventory", Assessment.Unknown,
                "Fresh host-reported installed inventory is unavailable. Matching helpers are for review, not assumed missing; updates/removals cannot be established."));
        else if (!inventory!.Complete)
            coverage.Add(new("agent-inventory", Assessment.Partial,
                "Host inventory is incomplete; helpers absent from this list are not assumed missing."));
        var recommendations = new List<AgentToolRecommendation>();
        var contextComplete = !coverage.Any(c => c.Check.StartsWith("agent-context", StringComparison.Ordinal)
            && c.Assessment != Assessment.Confirmed);
        foreach (var catalog in catalogs)
        foreach (var resource in catalog.Resources)
        {
            var installed = inventory?.Resources.SingleOrDefault(item =>
                item.Repository == resource.Repository && item.Name == resource.Name);
            var reasons = Match(resource.Name + " " + resource.Description, resource.Name, context);
            var action = reasons.Count > 0 ? "review" : null;
            if (reasons.Count > 0 && currentInventory)
            {
                action = installed is null ? inventory!.Complete ? "install" : "review" : !installed.Enabled ? "disabled" :
                    UpdateAvailable(installed, resource) ? "update" : "installed";
                if (installed is { Enabled: true } &&
                    !NuGetVersion.TryParse(installed.Version, out _) && installed.Revision is not { Length: 40 })
                    coverage.Add(new("agent-inventory-version:" + resource.Id, Assessment.Unknown,
                        "A helper is present but its installed version/revision is unverified; up-to-date status cannot be established."));
            }
            if (reasons.Count == 0 && currentInventory && installed is
                { ManagedByNudge: true, Scope: "project", Enabled: true } &&
                contextComplete && catalog.Complete && catalog.Freshness == Assessment.Confirmed)
            {
                action = "review-removal";
                reasons = ["This Nudge-managed project helper no longer matches the current project declarations. Review usage before removing it; static context does not prove it is unused."];
            }
            if (action is null) continue;
            var skills = resource.Skills.Where(skill => !skill.Legacy)
                .Where(skill => Match(PositiveUsage(skill.Description), resource.Name, context).Count > 0)
                .Take(4).ToArray();
            var revision = Digest(JsonSerializer.Serialize(new
            {
                resource.Id, resource.Revision, action, installed?.Version,
                InstalledRevision = installed?.Revision, Reasons = reasons.Order(StringComparer.Ordinal)
            }, Contract.Json));
            recommendations.Add(new(resource, action, installed?.Version, installed?.Revision,
                reasons, skills, revision, catalog.Freshness is Assessment.Stale or Assessment.Unknown or Assessment.Blocked
                    ? catalog.Freshness : Assessment.Partial));
        }
        // Two broad MAUI packs are alternatives, not a reason to install duplicate bundles.
        foreach (var group in recommendations.Where(r => r.Resource.Name == "dotnet-maui" &&
            r.Action is not "review-removal").GroupBy(r => r.Resource.Name))
        {
            var preferred = group.OrderBy(r => r.Action is "installed" or "update" or "disabled" ? 0 : 1)
                .ThenBy(r => r.Resource.Repository == "dotnet/skills" ? 0 : 1).First();
            foreach (var other in group.Where(r => r != preferred && r.Action is "install" or "review"))
            {
                var index = recommendations.IndexOf(other);
                recommendations[index] = other with { Action = "alternative" };
            }
        }
        foreach (var installed in inventory?.Resources ?? [])
        {
            var catalog = catalogs.SingleOrDefault(c => c.Repository == installed.Repository);
            if (!currentInventory || !contextComplete || installed.Scope != "project" ||
                !installed.ManagedByNudge || !installed.Enabled || catalog is not
                { Complete: true, Freshness: Assessment.Confirmed } ||
                catalog.Resources.Any(resource => resource.Name == installed.Name)) continue;
            var resource = new AgentToolResource($"{installed.Repository}:{installed.Name}",
                installed.Repository, installed.Name, "Previously recorded project helper is no longer in its source marketplace.",
                null, "", $"https://github.com/{installed.Repository}", "", [], []);
            recommendations.Add(new(resource, "review-removal", installed.Version, installed.Revision,
                ["The fresh, complete source catalog no longer lists this Nudge-managed project helper. Verify its usage and origin before removal."],
                [], Digest(resource.Id + "|catalog-removed|" + installed.Version), Assessment.Partial));
        }
        var ordered = recommendations.OrderBy(r => r.Action is "update" or "install" ? 0 :
                r.Action == "review" ? 1 : 2)
            .ThenByDescending(r => r.Reasons.Count)
            .ThenBy(r => r.Resource.Id, StringComparer.Ordinal).ToArray();
        var report = new AgentToolsReport(Contract.Version, workspace.WorkspaceId, now,
            context, inventory, ordered, coverage)
        {
            Sources = catalogs.Select(c => new AgentCatalogStatus(c.Repository, c.Freshness,
                c.Complete, c.RetrievedAt, c.Resources.Count)).ToArray()
        };
        var findings = ordered.Where(r => r.Action is "install" or "update" or "review" or "review-removal")
            .Select(r =>
            {
                var subject = $"agent-helper:{WorkspaceDiscovery.SdkScopeKey(workspace.Root, ".")}:{r.Resource.Id}";
                return new Finding(Contract.Version, subject, subject, r.Revision,
                    r.Action == "update" ? "agent-helper-update" :
                    r.Action == "review-removal" ? "agent-helper-fit-review" : "agent-helper-recommendation",
                    Priority.Low, r.InstalledVersion, r.Resource.Version ??
                        (string.IsNullOrEmpty(r.Resource.Revision) ? null : r.Resource.Revision),
                    new(null, null, null, Source: r.Resource.Repository), r.Assessment,
                    [new("official-helper-catalog", r.Resource.Url, now)],
                    r.Action == "review-removal" ? "review-project-helper-usage-before-removal" :
                        "review-helper-scope-capabilities-and-native-installation", true, "agent-tools");
            }).ToArray();
        return new(report, findings);
    }

    static bool UpdateAvailable(InstalledAgentResource installed, AgentToolResource resource) =>
        NuGetVersion.TryParse(installed.Version, out var current) &&
        NuGetVersion.TryParse(resource.Version, out var candidate) && candidate > current ||
        installed.Version == resource.Version && installed.Revision is { Length: 40 } revision &&
        resource.Revision.Length == 40 && revision != resource.Revision;

    static string PositiveUsage(string description)
    {
        var end = description.IndexOf("DO NOT USE FOR:", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? description[..end] : description;
    }

    static List<string> Match(string text, string name, IReadOnlyList<AgentProjectSignal> context)
    {
        var required = name switch
        {
            "dotnet-maui" or "dotnet-maui-tooling" => new[] { "maui", "bindings" },
            "dotnet-template-engine" => ["templates"],
            "dotnet-blazor" => ["blazor"],
            "dotnet-aspnetcore" => ["web"],
            "dotnet-data" => ["data"],
            "dotnet-ai" => ["ai"],
            "dotnet-winforms" => ["winforms"],
            "dotnet-test" or "dotnet-test-migration" => ["tests"],
            "dotnet-upgrade" => ["migration"],
            "dotnet11" => ["net11"],
            _ => []
        };
        if (required.Length > 0 && !context.Any(signal => required.Contains(signal.Topic))) return [];
        if (name.Contains("migration", StringComparison.OrdinalIgnoreCase) &&
            !context.Any(signal => signal.Topic == "migration")) return [];
        var matches = context.Where(signal => TopicWords.TryGetValue(signal.Topic, out var words) &&
            (required.Length == 0 || required.Contains(signal.Topic)) &&
            words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(signal => signal.Evidence).Distinct(StringComparer.Ordinal).ToList();
        if (name == "dotnet" && context.Any(signal => signal.Topic == "csharp"))
            matches.Add("SDK-style .NET projects can use the core C# language-server and development helpers.");
        if (name == "dotnet-msbuild" && context.Any(signal => signal.Topic == "dotnet"))
            matches.Add("SDK-style projects use MSBuild; the build-analysis helper is available for explicit build diagnosis.");
        return matches.Take(8).ToList();
    }

    static IReadOnlyList<AgentProjectSignal> ProjectContext(WorkspaceInventory workspace, List<Coverage> coverage)
    {
        var signals = new List<AgentProjectSignal>();
        void Add(string topic, string evidence) => signals.Add(new(topic, evidence));
        coverage.AddRange(workspace.Coverage.Where(c => c.Assessment != Assessment.Confirmed &&
            (c.Check.StartsWith("project:", StringComparison.Ordinal) ||
             c.Check.StartsWith("agent-context", StringComparison.Ordinal)))
            .Select(c => new Coverage("agent-context:" + c.Check, c.Assessment, c.Detail)));
        if (workspace.Projects.Count > 0) Add("dotnet", "The workspace contains SDK-style .NET projects.");
        if (workspace.Projects.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            Add("csharp", "The workspace contains C# project declarations.");
        foreach (var file in workspace.ContextFiles)
            Add(file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? "blazor" : "templates",
                file + ": declares a component/template file (presence only; content was not executed).");
        var sdkMajor = workspace.SdkDeclarations.Select(sdk => NuGetVersion.TryParse(sdk.Version, out var version)
            ? version.Major : 0).DefaultIfEmpty().Max();
        var paths = workspace.Projects.Concat(workspace.ConfigurationFiles.Where(path =>
            path.EndsWith(".props", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 128)
            coverage.Add(new("agent-context-limit", Assessment.Partial, "Only 128 project/props declarations were read."));
        foreach (var path in paths.Take(128))
        {
            try
            {
                var text = SafeMetadataReader.ReadFile(workspace.Root, path, 2 * 1024 * 1024);
                if (text.StartsWith('\uFEFF')) text = text[1..];
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var xml = XDocument.Load(reader);
                if (xml.Descendants().Any(e => e.Name.LocalName is "UseMaui" or "TargetFramework" or
                    "TargetFrameworks" or "UseWPF" or "UseWindowsForms" &&
                    (e.Value.Contains("$(", StringComparison.Ordinal) ||
                     e.AncestorsAndSelf().Any(parent => parent.Attribute("Condition") is not null))))
                    coverage.Add(new("agent-context-evaluation:" + path, Assessment.Partial,
                        "Computed/conditional project features were not evaluated; absence cannot establish a helper removal."));
                var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                    .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "").ToArray();
                bool Property(string name) => xml.Descendants().Any(e => e.Name.LocalName == name &&
                    e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                bool Package(params string[] names) => packages.Any(package => names.Any(name =>
                    package.Contains(name, StringComparison.OrdinalIgnoreCase)));
                void Signal(string topic, string reason) => Add(topic, $"{path}: {reason} (declarations only; MSBuild conditions/imports were not executed).");
                if (Property("UseMaui") || Package("Microsoft.Maui")) Signal("maui", "declares .NET MAUI usage");
                if (xml.Root?.Attribute("Sdk")?.Value.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true ||
                    xml.Descendants().Any(e => e.Name.LocalName == "FrameworkReference" &&
                        e.Attribute("Include")?.Value == "Microsoft.AspNetCore.App"))
                    Signal("web", "declares ASP.NET Core server usage");
                if (Package("Blazor", "Microsoft.AspNetCore.Components") ||
                    xml.Root?.Attribute("Sdk")?.Value.Contains("Blazor", StringComparison.OrdinalIgnoreCase) == true)
                    Signal("blazor", "declares Blazor/component usage");
                if (Package("EntityFramework", "Dapper", "Microsoft.Data.Sqlite", "SqlClient", "Npgsql", "MySqlConnector"))
                    Signal("data", "declares data-access dependencies");
                if (Package("ModelContextProtocol", "Microsoft.Extensions.AI", "SemanticKernel", "Microsoft.ML"))
                    Signal("ai", "declares AI or MCP dependencies");
                if (Property("IsTestProject") || Package("xunit", "NUnit", "MSTest", "Microsoft.NET.Test.Sdk"))
                    Signal("tests", "declares a test project/framework");
                if (Property("UseWindowsForms")) Signal("winforms", "declares Windows Forms");
                if (Property("UseWPF")) Signal("wpf", "declares WPF");
                if (Property("IsBindingProject")) Signal("bindings", "declares a platform binding project");
                if (packages.Length > 0) Signal("packages", "declares NuGet package references");
                if (xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                    .Any(e => e.Value.Split(';').Any(tfm => tfm.Trim().StartsWith("net11.", StringComparison.Ordinal))))
                    Signal("net11", "targets .NET 11");
                if (sdkMajor > 0 && xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                    .SelectMany(e => e.Value.Split(';')).Any(tfm => tfm.Trim().StartsWith("net", StringComparison.Ordinal) &&
                        tfm.Contains('.') && int.TryParse(tfm.Trim()[3..].Split('.')[0], out var major) && major < sdkMajor))
                    Signal("migration", "targets an older framework than the declared SDK; migration help is optional, not a required upgrade");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                coverage.Add(new("agent-context:" + path, Assessment.Partial, ex.Message));
            }
        }
        return signals.Distinct().ToArray();
    }

    static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant()[..16];
}

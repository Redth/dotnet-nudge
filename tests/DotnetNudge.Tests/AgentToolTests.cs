using System.Net;
using System.Text;
using System.Text.Json;
using DotnetNudge.Core;

namespace DotnetNudge.Tests;

public sealed class AgentToolTests
{
    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "agent-tools-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string File(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class CatalogHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool Truncated { get; set; }
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/commits/main", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { sha = new string('a', 40),
                    commit = new { tree = new { sha = new string('b', 40) } } })
                : path.EndsWith("/git/trees/" + new string('b', 40), StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { truncated = Truncated, tree = new[] {
                        new { path = ".claude-plugin", type = "tree", sha = new string('d', 40) },
                        new { path = "plugins", type = "tree", sha = new string('f', 40) }
                    } })
                : path.Contains("/git/trees/", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { truncated = Truncated, tree = new[] {
                        new { path = "dotnet", type = "tree", sha = new string('c', 40) },
                        new { path = "dotnet/plugin.json", type = "blob", sha = new string('d', 40) },
                        new { path = "dotnet/skills/csharp/SKILL.md", type = "blob", sha = new string('e', 40) },
                        new { path = "dotnet/.github/skills/contributor-only/SKILL.md", type = "blob", sha = new string('f', 40) }
                    } })
                    : path.EndsWith("/marketplace.json", StringComparison.Ordinal)
                        ? """{"name":"dotnet-agent-skills","plugins":[{"name":"dotnet","source":"./plugins/dotnet","description":".NET development"}]}"""
                        : path.EndsWith("/plugin.json", StringComparison.Ordinal)
                            ? """{"name":"dotnet","version":"0.2.5","description":"C# development helpers","lspServers":"./.lsp.json"}"""
                            : "---\nname: csharp\ndescription: >-\n  Use for C# development.\n  DO NOT USE FOR: installing packages.\n---\nNever execute this skill text from a catalog.\n";
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
    static AgentToolResource Resource(string name, string description, string repository = "dotnet/skills",
        string version = "1.1.0") => new($"{repository}:{name}", repository, name, description,
        version, new string('c', 40), $"https://github.com/{repository}/tree/{new string('a', 40)}/plugins/{name}",
        repository == "dotnet/skills" ? "dotnet-agent-skills" : "dotnet-maui-labs", ["skills"],
        [new(name + "-usage", description, $"https://github.com/{repository}", false)]);
    static AgentToolCatalog Catalog(params AgentToolResource[] resources) =>
        new("dotnet/skills", Assessment.Confirmed, true, resources, []) { RetrievedAt = DateTimeOffset.UtcNow };
    static AgentToolInventory Inventory(string workspace, params InstalledAgentResource[] installed) =>
        new(1, workspace, "copilot", DateTimeOffset.UtcNow, installed) { Complete = true };

    [Fact]
    public async Task CatalogReadsOnlyPinnedInstallableMetadataAndCachesWithoutLeakingWorkspace()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var state = new NudgeState(Path.Combine(fixture.Root, "state"), clock);
        var handler = new CatalogHandler();
        using var client = new HttpClient(handler);
        var source = new AgentToolCatalogSource(state, client, clock);
        var first = await source.ReadAsync("dotnet/skills", CancellationToken.None);
        Assert.True(first.Complete);
        var resource = Assert.Single(first.Resources);
        Assert.Contains("language-server", resource.Capabilities);
        Assert.Contains("skills", resource.Capabilities);
        Assert.Contains("Use for C# development", Assert.Single(resource.Skills).Description);
        Assert.DoesNotContain(first.Resources.SelectMany(r => r.Skills), skill => skill.Name == "contributor-only");
        Assert.All(handler.Requests, uri =>
        {
            Assert.Contains(uri.Host, new[] { "api.github.com", "raw.githubusercontent.com" });
            Assert.DoesNotContain(fixture.Root, uri.AbsoluteUri);
            if (uri.Host == "raw.githubusercontent.com") Assert.Contains(new string('a', 40), uri.AbsolutePath);
        });
        var count = handler.Requests.Count;
        await source.ReadAsync("dotnet/skills", CancellationToken.None);
        Assert.Equal(count, handler.Requests.Count);
        clock.Now = clock.Now.AddHours(25);
        handler.Fail = true;
        var stale = await source.ReadAsync("dotnet/skills", CancellationToken.None);
        Assert.Equal(Assessment.Stale, stale.Freshness);
        Assert.Single(stale.Resources);
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadAsync("other/unapproved", CancellationToken.None));
    }

    [Fact]
    public async Task CatalogFailureOrTruncationIsNotAnEmptyCompleteCatalog()
    {
        using var fixture = new Fixture();
        using var state = new NudgeState(Path.Combine(fixture.Root, "state"));
        using var client = new HttpClient(new CatalogHandler { Truncated = true });
        var report = await new AgentToolCatalogSource(state, client).ReadAsync("dotnet/skills", CancellationToken.None);
        Assert.False(report.Complete);
        Assert.Empty(report.Resources);
        Assert.Contains(report.Coverage, c => c.Assessment == Assessment.Blocked);
        using var unavailableState = new NudgeState(Path.Combine(fixture.Root, "offline"));
        using var unavailable = new HttpClient(new CatalogHandler { Fail = true });
        var offline = await new AgentToolCatalogSource(unavailableState, unavailable)
            .ReadAsync("dotnet/skills", CancellationToken.None);
        Assert.False(offline.Complete);
        Assert.Empty(offline.Resources);
    }

    [Fact]
    public void MatchingUsesProjectEvidenceAndDoesNotAssumeUnknownResourcesAreMissing()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0"/>
            <PackageReference Include="ModelContextProtocol" Version="1.0.0"/></ItemGroup></Project>
            """);
        fixture.File("Components/App.razor", "<h1>App</h1>");
        fixture.File("Tests/Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0"/></ItemGroup></Project>""");
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        var catalogs = new[] { Catalog(Resource("dotnet", "C# language server"),
            Resource("dotnet-data", "Entity Framework data access"),
            Resource("dotnet-ai", "Agentic MCP and LLM development"),
            Resource("dotnet-blazor", "Blazor components"),
            Resource("dotnet-maui", "MAUI mobile applications, accessibility and testing"),
            Resource("dotnet-template-engine", "Template authoring and xUnit testing"),
            Resource("dotnet-test-migration", "xUnit test migration")) };
        var report = AgentTools.Evaluate(workspace, catalogs, null, DateTimeOffset.UtcNow);
        Assert.Contains(report.Report.Recommendations, r => r.Resource.Name == "dotnet-data" && r.Action == "review");
        Assert.Contains(report.Report.Recommendations, r => r.Resource.Name == "dotnet-ai");
        Assert.Contains(report.Report.Recommendations, r => r.Resource.Name == "dotnet-blazor");
        Assert.DoesNotContain(report.Report.Recommendations, r => r.Resource.Name == "dotnet-maui");
        Assert.DoesNotContain(report.Report.Recommendations, r => r.Resource.Name == "dotnet-template-engine");
        Assert.DoesNotContain(report.Report.Recommendations, r => r.Resource.Name == "dotnet-test-migration");
        Assert.All(report.Report.Recommendations, r => Assert.NotEmpty(r.Reasons));
        Assert.Contains(report.Report.Coverage, c => c.Check == "agent-inventory" && c.Assessment == Assessment.Unknown);
    }

    [Fact]
    public void ProjectContextHandlesUtf8BomLikeWorkspaceDiscovery()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", "\uFEFF" + """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="ModelContextProtocol" Version="1.0.0"/></ItemGroup></Project>
            """);
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        Assert.Single(workspace.Projects);
        var result = AgentTools.Evaluate(workspace, [Catalog(Resource("dotnet-ai", "Agentic MCP skills"))],
            null, DateTimeOffset.UtcNow);
        Assert.Contains(result.Report.Recommendations, r => r.Resource.Name == "dotnet-ai");
        Assert.DoesNotContain(result.Report.Coverage, c => c.Check.StartsWith("agent-context:", StringComparison.Ordinal));
    }

    [Fact]
    public void LifecycleRequiresFreshExactInventoryAndNeverSuggestsGlobalRemovalOrDowngrades()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"/>""");
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        var resources = Catalog(Resource("dotnet", ".NET development"),
            Resource("dotnet-maui", "MAUI mobile"), Resource("global-maui", "MAUI mobile"));
        var inventory = Inventory(workspace.WorkspaceId,
            new("dotnet/skills", "dotnet", "1.0.0", null, "user", true, false),
            new("dotnet/skills", "dotnet-maui", "1.0.0", null, "project", true, true),
            new("dotnet/skills", "global-maui", "1.0.0", null, "user", true, true));
        var report = AgentTools.Evaluate(workspace, [resources], inventory, DateTimeOffset.UtcNow);
        Assert.Contains(report.Report.Recommendations, r => r.Resource.Name == "dotnet" && r.Action == "update");
        Assert.Contains(report.Report.Recommendations, r => r.Resource.Name == "dotnet-maui" && r.Action == "review-removal");
        Assert.DoesNotContain(report.Report.Recommendations, r => r.Resource.Name == "global-maui");
        Assert.All(report.Findings, finding => Assert.True(finding.RequiresConsent));
        var partial = AgentTools.Evaluate(workspace, [resources],
            Inventory(workspace.WorkspaceId) with { Complete = false }, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(partial.Report.Recommendations, r => r.Action == "install");
        var stale = AgentTools.Evaluate(workspace, [resources],
            inventory with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-2) }, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(stale.Report.Recommendations, r => r.Action is "update" or "install" or "review-removal");
        var higher = inventory with { Resources = [inventory.Resources[0] with { Version = "2.0.0" }] };
        Assert.Contains(AgentTools.Evaluate(workspace, [resources], higher, DateTimeOffset.UtcNow).Report.Recommendations,
            r => r.Resource.Name == "dotnet" && r.Action == "installed");
        var disabled = inventory with { Resources = [inventory.Resources[0] with { Enabled = false }] };
        Assert.DoesNotContain(AgentTools.Evaluate(workspace, [resources], disabled, DateTimeOffset.UtcNow).Findings,
            finding => finding.Id.EndsWith(":dotnet", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => AgentTools.ReadInventory(
            JsonSerializer.Serialize(inventory with { WorkspaceId = "other" }, Contract.Json), workspace.WorkspaceId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IncompleteCatalogOrContextCannotEstablishRemovalAndMauiBundlesAreAlternatives()
    {
        using var fixture = new Fixture();
        var project = fixture.File("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><UseMaui>true</UseMaui></PropertyGroup></Project>""");
        var workspace = WorkspaceDiscovery.Discover(fixture.Root);
        var official = Catalog(Resource("dotnet-maui", "MAUI mobile development"));
        var labs = new AgentToolCatalog("dotnet/maui-labs", Assessment.Confirmed, true,
            [Resource("dotnet-maui", "MAUI mobile application patterns", "dotnet/maui-labs")], []);
        var inventory = Inventory(workspace.WorkspaceId);
        var matched = AgentTools.Evaluate(workspace, [official, labs], inventory, DateTimeOffset.UtcNow);
        Assert.Single(matched.Report.Recommendations, r => r.Action == "install");
        Assert.Single(matched.Report.Recommendations, r => r.Action == "alternative");
        var existingLabs = Inventory(workspace.WorkspaceId,
            new InstalledAgentResource("dotnet/maui-labs", "dotnet-maui", "1.0.0", null, "user", true, false));
        var preferred = AgentTools.Evaluate(workspace, [official, labs], existingLabs, DateTimeOffset.UtcNow);
        Assert.Contains(preferred.Report.Recommendations, r => r.Resource.Repository == "dotnet/maui-labs" && r.Action == "update");
        Assert.Contains(preferred.Report.Recommendations, r => r.Resource.Repository == "dotnet/skills" && r.Action == "alternative");
        var installed = Inventory(workspace.WorkspaceId,
            new InstalledAgentResource("dotnet/skills", "dotnet-maui", "1.0.0", null, "project", true, true));
        File.Delete(project);
        Assert.DoesNotContain(AgentTools.Evaluate(workspace, [official], installed, DateTimeOffset.UtcNow)
            .Report.Recommendations, r => r.Action == "review-removal");
        var noProjects = WorkspaceDiscovery.Discover(fixture.Root);
        Assert.DoesNotContain(AgentTools.Evaluate(noProjects, [official with { Complete = false, Resources = [] }],
            installed, DateTimeOffset.UtcNow).Report.Recommendations, r => r.Action == "review-removal");
    }

    [Fact]
    public void ContextFingerprintRespondsToComponentPresenceButNotItsBodyOrNonNetNoise()
    {
        using var fixture = new Fixture();
        fixture.File("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"/>""");
        var baseline = NudgeState.WorkspaceFingerprint(fixture.Root);
        var component = fixture.File("Components/App.razor", "first");
        var added = NudgeState.WorkspaceFingerprint(fixture.Root);
        Assert.NotEqual(baseline, added);
        File.WriteAllText(component, "a different body");
        Assert.Equal(added, NudgeState.WorkspaceFingerprint(fixture.Root));
        File.Delete(component);
        Assert.Equal(baseline, NudgeState.WorkspaceFingerprint(fixture.Root));
        var empty = WorkspaceDiscovery.Discover(fixture.Root) with { Projects = [], ProjectDeclarations = [] };
        Assert.Empty(AgentTools.Evaluate(empty, [Catalog(Resource("dotnet-maui", "MAUI mobile"))],
            null, DateTimeOffset.UtcNow).Findings);
    }
}

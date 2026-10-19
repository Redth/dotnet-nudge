using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetNudge.Core;

namespace DotnetNudge.Integration.Tests;

public sealed class PackageDetectionTests
{
    [Fact]
    public async Task ExplicitReportChecksBeyondPassiveBudgetAndKeepsUnknownTransitiveScopes()
    {
        using var feed = new Feed();
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
                <configuration><packageSources><clear/><add key="private" value="{feed.Source}"/></packageSources>
                <packageSourceMapping><packageSource key="private"><package pattern="Acme.Parent"/></packageSource></packageSourceMapping></configuration>
                """);
            for (var i = 0; i < 3; i++)
            {
                var directory = Path.Combine(root, "App" + i);
                Directory.CreateDirectory(Path.Combine(directory, "obj"));
                var project = Path.Combine(directory, "App.csproj");
                File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"/>""");
                var assets = Path.Combine(directory, "obj", "project.assets.json");
                var graph = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                    .Replace("{CACHE}", Path.Combine(root, "cache").Replace('\\', '/'), StringComparison.Ordinal)
                    .Replace("{PROJECT}", project.Replace('\\', '/'), StringComparison.Ordinal)
                    .Replace("{OUTPUT}", Path.GetDirectoryName(assets)!.Replace('\\', '/'), StringComparison.Ordinal);
                File.WriteAllText(assets, graph);
            }
            var report = await PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(root));
            Assert.Equal(12, report.Packages.Count);
            Assert.Equal(12, report.Packages.Select(p => p.Id).Distinct().Count());
            var direct = report.Packages.Where(p => p.Direct).ToArray();
            Assert.Equal(6, direct.Length);
            Assert.All(direct, p =>
            {
                Assert.True(p.CentrallyManaged);
                Assert.Contains(p.Candidates, c => c.Version == "1.1.0" && c.Source == "private");
                Assert.Contains(p.Deprecations, d => d.Message == "Use the maintained fork.");
                Assert.DoesNotContain(root, p.Project);
            });
            Assert.All(report.Packages.Where(p => !p.Direct), p =>
            {
                Assert.Empty(p.Candidates);
                Assert.NotEmpty(p.Diagnostics);
            });
            Assert.False(report.Complete);
            Assert.Contains(report.Coverage, c => c.Assessment == Assessment.Partial);
            var again = await PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(root));
            Assert.Equal(report.Packages.Select(p => p.Revision), again.Packages.Select(p => p.Revision));
            Assert.DoesNotContain(feed.Requests, p => p.EndsWith(".nupkg", StringComparison.Ordinal));
            Assert.DoesNotContain(feed.Requests, p => p.Contains("replacement", StringComparison.OrdinalIgnoreCase));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(root), cancelled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExplicitReportDoesNotCallMissingRestoresOrEmptyWorkspacesUpToDate()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-empty-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var empty = await PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(root));
            Assert.False(empty.Complete);
            Assert.Empty(empty.Packages);
            File.WriteAllText(Path.Combine(root, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"/>""");
            var missing = await PackageIntegration.ReportAsync(WorkspaceDiscovery.Discover(root));
            Assert.False(missing.Complete);
            Assert.Contains(missing.Coverage, c => c.Check.StartsWith("package-graph:", StringComparison.Ordinal)
                && c.Assessment == Assessment.Unknown);
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    sealed class Feed : IDisposable
    {
        readonly HttpListener listener = new();
        readonly Task worker;
        public string Source { get; }
        public string Message { get; set; } = "Use the maintained fork.";
        public ConcurrentQueue<string> Requests { get; } = new();

        public Feed()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var root = $"http://localhost:{port}/";
            Source = root + "index.json";
            listener.Prefixes.Add(root);
            listener.Start();
            worker = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                    var path = context.Request.Url!.AbsolutePath;
                    Requests.Enqueue(path);
                    var deprecation = JsonSerializer.Serialize(new
                    {
                        reasons = new[] { "CriticalBugs" }, message = Message,
                        alternatePackage = new { id = "Private.Replacement", range = "[2.0.0,)" }
                    });
                    var payload = path switch
                    {
                        "/index.json" => $$"""
                            {"version":"3.0.0","resources":[
                            {"@id":"{{root}}registration/","@type":"RegistrationsBaseUrl/3.6.0"},
                            {"@id":"{{root}}audit.json","@type":"VulnerabilityInfo/6.7.0"}]}
                            """,
                        "/audit.json" => $$"""[{"@name":"base","@id":"{{root}}base.json","@updated":"2026-10-14T00:00:00Z"}]""",
                        "/base.json" => "{}",
                        "/registration/acme.parent/index.json" => $$$"""
                            {"count":1,"items":[{"count":2,"lower":"1.0.0","upper":"1.1.0","items":[
                            {"catalogEntry":{"id":"Acme.Parent","version":"1.0.0","listed":true,
                            "deprecation":{{{deprecation}}},"dependencyGroups":[{"targetFramework":"net8.0","dependencies":[]}]}},
                            {"catalogEntry":{"id":"Acme.Parent","version":"1.1.0","listed":true,
                            "dependencyGroups":[{"targetFramework":"net8.0","dependencies":[]}]}}]}]}
                            """,
                        _ => null
                    };
                    if (payload is null) context.Response.StatusCode = 404;
                    else
                    {
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    context.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
            worker.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task DeprecatedPackagesProduceDistinctScopedPersistentFindingsAlongsideUpdates()
    {
        using var feed = new Feed();
        var root = Path.Combine(AppContext.BaseDirectory, "nudge-deprecation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        try
        {
            var project = Path.Combine(root, "App.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"/>""");
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
                <configuration><packageSources><clear/><add key="private" value="{feed.Source}"/></packageSources>
                <packageSourceMapping><packageSource key="private"><package pattern="Acme.Parent"/></packageSource></packageSourceMapping></configuration>
                """);
            var assets = Path.Combine(root, "obj", "project.assets.json");
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                .Replace("{CACHE}", Path.Combine(root, "cache").Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{PROJECT}", project.Replace('\\', '/'), StringComparison.Ordinal)
                .Replace("{OUTPUT}", Path.GetDirectoryName(assets)!.Replace('\\', '/'), StringComparison.Ordinal);
            File.WriteAllText(assets, json);
            var inventory = WorkspaceDiscovery.Discover(root);
            using var state = new NudgeState(Path.Combine(root, "state"));
            var result = await PackageIntegration.ScanAsync(inventory, state, CancellationToken.None);
            var deprecated = result.Findings.Where(f => f.Kind == "package-deprecation").ToArray();
            Assert.Equal(2, deprecated.Length);
            Assert.Equal(2, deprecated.Select(f => f.Id).Distinct().Count());
            Assert.All(deprecated, f =>
            {
                Assert.Equal(Priority.Medium, f.Priority);
                Assert.True(f.RequiresConsent);
                Assert.Equal("package", f.Category);
                Assert.Equal("1.0.0", f.Current);
                Assert.Null(f.Candidate);
                Assert.Equal("App.csproj", f.Scope.Project);
                Assert.Contains(f.Evidence, e => e.Type == "package-deprecation-source" && e.Uri == feed.Source);
            });
            Assert.Contains(deprecated, f => f.Scope.Framework == "net10.0" && f.Scope.RuntimeIdentifier == "osx-arm64");
            Assert.Equal(2, result.Findings.Count(f => f.Kind == "package-update"));
            state.Save(new(Contract.Version, inventory, result.Findings, result.Coverage, DateTimeOffset.UtcNow));
            state.Decide(inventory.RepositoryId, inventory.WorkspaceId, deprecated[0].Id,
                deprecated[0].Revision, DecisionAction.Snooze, DateTimeOffset.UtcNow.AddDays(7));
            var status = state.Status(inventory.RepositoryId, inventory.WorktreeId, inventory.WorkspaceId);
            Assert.Contains(status.Findings, f => f.Finding.Id == deprecated[0].Id && !f.Eligible);
            Assert.Contains(status.Findings, f => f.Finding.Kind == "package-update" && f.Eligible);
            feed.Message = "New migration instructions.";
            var changed = await PackageIntegration.ScanAsync(inventory, state, CancellationToken.None);
            Assert.All(deprecated, previous => Assert.NotEqual(previous.Revision,
                Assert.Single(changed.Findings, f => f.Id == previous.Id).Revision));
            Assert.Equal(json, File.ReadAllText(assets));
            Assert.DoesNotContain(feed.Requests, path => path.Contains("replacement", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(feed.Requests, path => path.EndsWith(".nupkg", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

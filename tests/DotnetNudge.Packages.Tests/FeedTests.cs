using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using DotnetNudge.Packages;
using NuGet.Versioning;
using Xunit;

namespace DotnetNudge.Packages.Tests;

public sealed class FeedTests
{
    private sealed class LocalFeed : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _worker;
        public string Base { get; }
        public List<string> Requests { get; } = [];
        public string BaseAdvisories { get; set; } =
            """{"acme.child":[{"url":"https://example.invalid/a","severity":2,"versions":"[1.0.0,1.3.0)"}]}""";
        public string UpdateAdvisories { get; set; } =
            """{"acme.child":[{"url":"https://example.invalid/extension","severity":2,"versions":"[1.3.0,1.4.0)"}]}""";
        public string Revision { get; set; } = "2026-01-01T00:00:00Z";
        public bool RequireAuthentication { get; set; }
        public bool MissingVulnerabilityResource { get; set; }
        public bool IncompatibleCandidateFramework { get; set; }

        public LocalFeed()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Base);
            _listener.Start();
            _worker = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                    var path = context.Request.Url!.AbsolutePath;
                    lock (Requests) Requests.Add(path);
                    if (RequireAuthentication && path.StartsWith("/registration/", StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.Add("WWW-Authenticate", "Basic realm=\"fixture\"");
                        context.Response.Close();
                        continue;
                    }
                    var payload = path switch
                    {
                        "/index.json" => MissingVulnerabilityResource
                            ? $$"""{"version":"3.0.0","resources":[{"@id":"{{Base}}registration/","@type":"RegistrationsBaseUrl/3.6.0"}]}"""
                            : $$"""{"version":"3.0.0","resources":[{"@id":"{{Base}}registration/","@type":"RegistrationsBaseUrl/3.6.0"},{"@id":"{{Base}}vulnerability-index.json","@type":"VulnerabilityInfo/6.7.0"}]}""",
                        "/vulnerability-index.json" => $$"""[{"@name":"base","@id":"{{Base}}base.json","@updated":"{{Revision}}"},{"@name":"update","@id":"{{Base}}updates.json","@updated":"{{Revision}}"}]""",
                        "/base.json" => BaseAdvisories,
                        "/updates.json" => UpdateAdvisories,
                        "/registration/acme.child/index.json" => Registration("acme.child",
                            ["1.2.0", "1.3.0", "1.4.0", "2.0.0-beta.1"]),
                        "/registration/acme.parent/index.json" => Registration("acme.parent", ["1.0.0", "1.1.0"]),
                        "/flat/acme.child/1.3.0/acme.child.nuspec" =>
                            """<package><metadata><id>Acme.Child</id><version>1.3.0</version><authors>test</authors><description>test</description><dependencies><group targetFramework="net8.0"><dependency id="Acme.Extra" version="[1.0.0,)" /></group></dependencies></metadata></package>""",
                        "/flat/acme.child/1.4.0/acme.child.nuspec" =>
                            """<package><metadata><id>Acme.Child</id><version>1.4.0</version><authors>test</authors><description>test</description><dependencies><group targetFramework="net8.0" /></dependencies></metadata></package>""",
                        "/flat/acme.child/2.0.0-beta.1/acme.child.nuspec" =>
                            """<package><metadata><id>Acme.Child</id><version>2.0.0-beta.1</version><authors>test</authors><description>test</description><dependencies><group targetFramework="net8.0" /></dependencies></metadata></package>""",
                        _ => null
                    };
                    if (payload is null) context.Response.StatusCode = 404;
                    else
                    {
                        context.Response.ContentType = "application/json";
                        context.Response.Headers.Add("Cache-Control", "no-cache");
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    context.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _worker.GetAwaiter().GetResult();
        }

        private string Registration(string id, string[] versions)
        {
            var leaves = versions.Select(version =>
            {
                var groups = id == "acme.parent" && version == "1.1.0"
                    ? """[{"targetFramework":"net8.0","dependencies":[{"id":"Acme.Child","range":"[1.4.0,)"}]}]"""
                    : id == "acme.child" && version != "1.2.0" && IncompatibleCandidateFramework
                    ? """[{"targetFramework":"net10.0","dependencies":[]}]"""
                    : version == "1.3.0"
                    ? """[{"targetFramework":"net8.0","dependencies":[{"id":"Acme.Extra","range":"[1.0.0,)"}]}]"""
                    : """[{"targetFramework":"net8.0","dependencies":[]}]""";
                return $$"""{"@id":"{{Base}}registration/{{id}}/{{version}}.json","catalogEntry":{"id":"{{id}}","version":"{{version}}","authors":"test","description":"test","dependencyGroups":{{groups}}},"packageContent":"{{Base}}flat/{{id}}/{{version}}/{{id}}.{{version}}.nupkg"}""";
            });
            return $$"""{"count":1,"items":[{"@id":"{{Base}}registration/{{id}}/page.json","count":{{versions.Length}},"lower":"{{versions[0]}}","upper":"{{versions[^1]}}","items":[{{string.Join(",", leaves)}}]}]}""";
        }
    }

    [Fact]
    public async Task AuditOnlyCoversTransitiveAndBeyondCandidateWindowWithoutVersionQueries()
    {
        using var feed = new LocalFeed
        {
            BaseAdvisories = """
                {"acme.child":[{"url":"https://example.invalid/child","severity":2,"versions":"[1.0.0,1.3.0)"}],
                 "acme.fifth":[{"url":"https://example.invalid/fifth","severity":3,"versions":"[1.0.0,1.3.0)"}]}
                """,
            UpdateAdvisories = "{}"
        };
        var root = CreateMappedConfig(feed, "Acme.*");
        try
        {
            var nodes = new[] { "Acme.First", "Acme.Second", "Acme.Third", "Acme.Fourth",
                "Acme.Fifth", "Acme.Child" }.Select((id, index) =>
                new PackageNode("Fixture", "net8.0", null, id, NuGetVersion.Parse("1.2.0"),
                    index != 5, null, null, null, [])).ToArray();
            var graph = new PackageGraph("fixture", "hash", nodes, EvidenceStatus.Complete, null);
            var intelligence = new PackageIntelligence(new PackageSourcePolicy(root));
            var fifth = await intelligence.AuditOnlyAsync(nodes[4], graph);
            var transitive = await intelligence.AuditOnlyAsync(nodes[5], graph);
            Assert.Equal(EvidenceStatus.Complete, fifth.Status);
            Assert.Equal(EvidenceStatus.Complete, transitive.Status);
            Assert.Contains(fifth.Advisories, a => a.Url.EndsWith("/fifth", StringComparison.Ordinal));
            Assert.Contains(transitive.Advisories, a => a.Url.EndsWith("/child", StringComparison.Ordinal));
            Assert.Null(fifth.MinimalKnownFix);
            Assert.Empty(fifth.Candidates);
            Assert.DoesNotContain(feed.Requests, path => path.StartsWith("/registration/", StringComparison.Ordinal));
            Assert.Single(feed.Requests, path => path == "/vulnerability-index.json");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Mapped_private_ID_only_reaches_private_feed_and_refresh_replaces_partitions()
    {
        using var privateFeed = new LocalFeed();
        using var publicFeed = new LocalFeed();
        var root = Path.Combine(AppContext.BaseDirectory, "feeds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $$"""
                <configuration>
                  <packageSources><clear />
                    <add key="private" value="{{privateFeed.Base}}index.json" />
                    <add key="public" value="{{publicFeed.Base}}index.json" />
                    <add key="disabled" value="https://example.invalid/disabled" />
                  </packageSources>
                  <disabledPackageSources><add key="disabled" value="true" /></disabledPackageSources>
                  <auditSources><clear /><add key="audit" value="{{privateFeed.Base}}index.json" /></auditSources>
                  <packageSourceMapping>
                    <packageSource key="private"><package pattern="Acme.*" /></packageSource>
                    <packageSource key="public"><package pattern="Public.*" /></packageSource>
                  </packageSourceMapping>
                </configuration>
                """);
            var policy = new PackageSourcePolicy(root);
            Assert.Equal("private", Assert.Single(policy.Allowed("Acme.Child")).Name);
            Assert.Empty(policy.Allowed("No.Mapping"));
            Assert.Empty(policy.Allowed("Acme.Child",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public" }));
            var node = new PackageNode("Fixture", "net8.0", null, "Acme.Child",
                NuGetVersion.Parse("1.2.0"), false, null, null, null, []);
            var parent = node with { Id = "Acme.Parent", Version = NuGetVersion.Parse("1.0.0"),
                Direct = true, Dependencies = ["Acme.Child"] };
            var graph = new PackageGraph("fixture", "hash", [node, parent], EvidenceStatus.Complete, null);
            var now = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
            var intelligence = new PackageIntelligence(policy, () => now, TimeSpan.FromHours(6));
            var first = await intelligence.AssessAsync(node, graph);
            Assert.Empty(publicFeed.Requests);
            Assert.True(first.Status == EvidenceStatus.Complete,
                string.Join("; ", first.Diagnostics) + " | requests: " + string.Join(",", privateFeed.Requests));
            Assert.Single(first.Advisories);
            Assert.Equal(NuGetVersion.Parse("1.4.0"), first.MinimalKnownFix);
            Assert.Contains(first.Candidates, c => c.Version == NuGetVersion.Parse("1.3.0") &&
                c.Compatibility == EvidenceStatus.Partial);
            Assert.Equal(NuGetVersion.Parse("1.1.0"), Assert.Single(first.Paths).ParentCandidate);
            Assert.Equal("private", first.Paths[0].ParentSource);
            Assert.Equal(EvidenceStatus.Complete, first.Paths[0].Coverage);
            Assert.DoesNotContain(first.Candidates, c => c.Version.IsPrerelease);
            Assert.DoesNotContain(privateFeed.Requests, r => r.EndsWith(".nupkg"));
            var preview = await intelligence.AssessAsync(node, graph, includePrerelease: true);
            Assert.Contains(preview.Candidates, c => c.Version.IsPrerelease && c.Kind == "preview");
            privateFeed.BaseAdvisories = "{}";
            privateFeed.UpdateAdvisories =
                """{"acme.child":[{"url":"https://example.invalid/b","severity":1,"versions":"[1.0.0,1.4.0)"}]}""";
            privateFeed.Revision = "2026-01-03T00:00:00Z";
            now = now.AddHours(7);
            var second = await intelligence.AssessAsync(node, graph);
            Assert.Single(second.Advisories);
            Assert.EndsWith("/b", second.Advisories[0].Url);
            Assert.NotEqual(first.EvidenceRevision, second.EvidenceRevision);
            Assert.Equal(first.SubjectKey, second.SubjectKey);
            Assert.True(first.RequiresConsentForMutation);
            Assert.Empty(publicFeed.Requests);
            privateFeed.UpdateAdvisories =
                """{"acme.child":[{"url":"https://example.invalid/unfixed","severity":3,"versions":"[1.0.0,)"}]}""";
            privateFeed.Revision = "2026-01-04T00:00:00Z";
            now = now.AddHours(7);
            var unfixed = await intelligence.AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Complete, unfixed.Status);
            Assert.Null(unfixed.MinimalKnownFix);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Authentication_failure_is_partial_not_up_to_date()
    {
        using var feed = new LocalFeed { RequireAuthentication = true };
        var root = Path.Combine(AppContext.BaseDirectory, "feed-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), $$"""
                <configuration><packageSources><clear /><add key="private" value="{{feed.Base}}index.json" /></packageSources>
                <packageSourceMapping><packageSource key="private"><package pattern="Acme.*" /></packageSource></packageSourceMapping></configuration>
                """);
            var node = new PackageNode("Fixture", "net8.0", null, "Acme.Child",
                NuGetVersion.Parse("1.2.0"), true, null, null, null, []);
            var graph = new PackageGraph("", "", [node], EvidenceStatus.Complete, null);
            var result = await new PackageIntelligence(new PackageSourcePolicy(root)).AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Partial, result.Status);
            Assert.Null(result.MinimalKnownFix);
            Assert.Contains(result.Diagnostics, d => d.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_incomplete_advisory_partitions_are_partial_and_not_cached(bool missingResource)
    {
        using var feed = new LocalFeed { MissingVulnerabilityResource = missingResource };
        if (!missingResource) feed.BaseAdvisories = "{malformed";
        var root = CreateMappedConfig(feed, "Acme.*");
        try
        {
            var node = Node();
            var graph = new PackageGraph("", "", [node], EvidenceStatus.Complete, null);
            var now = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
            var intelligence = new PackageIntelligence(new PackageSourcePolicy(root), () => now,
                TimeSpan.FromHours(6));
            var first = await intelligence.AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Partial, first.Status);
            Assert.Null(first.MinimalKnownFix);
            Assert.Contains(first.Diagnostics, d => d.Contains("Audit source", StringComparison.Ordinal));
            if (!missingResource)
            {
                feed.BaseAdvisories =
                    """{"acme.child":[{"url":"https://example.invalid/a","severity":2,"versions":"[1.0.0,1.3.0)"}]}""";
                feed.Revision = "2026-01-03T00:00:00Z";
                now = now.AddHours(7);
                var recovered = await intelligence.AssessAsync(node, graph);
                Assert.Equal(EvidenceStatus.Complete, recovered.Status);
                Assert.Single(recovered.Advisories);
                Assert.Equal(NuGetVersion.Parse("1.4.0"), recovered.MinimalKnownFix);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Incompatible_framework_candidates_cannot_be_known_fixes()
    {
        using var feed = new LocalFeed { IncompatibleCandidateFramework = true };
        var root = CreateMappedConfig(feed, "Acme.*");
        try
        {
            var node = Node();
            var graph = new PackageGraph("", "", [node], EvidenceStatus.Complete, null);
            var result = await new PackageIntelligence(new PackageSourcePolicy(root)).AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Complete, result.Status);
            Assert.Null(result.MinimalKnownFix);
            Assert.All(result.Candidates, c => Assert.Equal(EvidenceStatus.Unknown, c.Compatibility));
            Assert.Contains(result.Candidates, c => c.Diagnostic!.Contains("No candidate dependency group"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unmapped_direct_parent_has_explicit_remediation_coverage_gap()
    {
        using var feed = new LocalFeed();
        var root = CreateMappedConfig(feed, "Acme.Child");
        try
        {
            var node = Node();
            var parent = node with { Id = "Acme.Parent", Version = NuGetVersion.Parse("1.0.0"),
                Direct = true, Dependencies = ["Acme.Child"] };
            var graph = new PackageGraph("", "", [node, parent], EvidenceStatus.Complete, null);
            var unknown = await new PackageIntelligence(new PackageSourcePolicy(root)).AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Partial, unknown.Status);
            Assert.Equal(NuGetVersion.Parse("1.4.0"), unknown.MinimalKnownFix);
            Assert.Equal(EvidenceStatus.Unknown, Assert.Single(unknown.Paths).Coverage);
            Assert.Null(unknown.Paths[0].ParentCandidate);
            Assert.Contains(unknown.Diagnostics, d => d.Contains("direct parent", StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), Config(feed, "Acme.*"));
            var complete = await new PackageIntelligence(new PackageSourcePolicy(root)).AssessAsync(node, graph);
            Assert.Equal(EvidenceStatus.Complete, complete.Status);
            Assert.Equal(NuGetVersion.Parse("1.1.0"), complete.Paths[0].ParentCandidate);
            Assert.NotEqual(unknown.EvidenceRevision, complete.EvidenceRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Evidence_revision_covers_severity_fix_paths_and_diagnostics_independent_of_order()
    {
        var node = Node();
        var advisory = new PackageAdvisory("https://example.invalid/a", 1, "[1.0.0,1.3.0)", "audit");
        var candidate = new UpdateCandidate(NuGetVersion.Parse("1.4.0"), "minor", "private",
            EvidenceStatus.Partial, "static evidence");
        var path = new RemediationPath(["Acme.Child", "Acme.Parent"], "Acme.Parent", null, null,
            EvidenceStatus.Unknown, "no source");
        var initial = new PackageAssessment(node, EvidenceStatus.Partial, [advisory], [candidate], null,
            [path], ["limited"]);
        Assert.NotEqual(initial.EvidenceRevision, (initial with
            { Advisories = [advisory with { Severity = 3 }] }).EvidenceRevision);
        Assert.NotEqual(initial.EvidenceRevision, (initial with
            { MinimalKnownFix = NuGetVersion.Parse("1.4.0") }).EvidenceRevision);
        Assert.NotEqual(initial.EvidenceRevision, (initial with
            { Paths = [path with { ParentCandidate = NuGetVersion.Parse("1.1.0"), ParentSource = "private" }] })
            .EvidenceRevision);
        Assert.NotEqual(initial.EvidenceRevision, (initial with
            { Candidates = [candidate with { Diagnostic = "changed" }] }).EvidenceRevision);
        Assert.NotEqual(initial.EvidenceRevision, (initial with
            { Paths = [path with { Coverage = EvidenceStatus.Partial }] }).EvidenceRevision);
        var second = advisory with { Url = "https://example.invalid/b" };
        Assert.Equal((initial with { Advisories = [advisory, second] }).EvidenceRevision,
            (initial with { Advisories = [second, advisory] }).EvidenceRevision);
    }

    private static PackageNode Node() => new("Fixture", "net8.0", null, "Acme.Child",
        NuGetVersion.Parse("1.2.0"), false, null, null, null, []);

    private static string CreateMappedConfig(LocalFeed feed, string pattern)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "feed-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), Config(feed, pattern));
        return root;
    }

    private static string Config(LocalFeed feed, string pattern) => $$"""
        <configuration><packageSources><clear /><add key="private" value="{{feed.Base}}index.json" /></packageSources>
        <packageSourceMapping><packageSource key="private"><package pattern="{{pattern}}" /></packageSource></packageSourceMapping></configuration>
        """;
}

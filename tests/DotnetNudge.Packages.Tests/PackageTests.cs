using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotnetNudge.Packages;
using NuGet.Versioning;
using Xunit;

namespace DotnetNudge.Packages.Tests;

public sealed class PackageTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "Fixture.csproj");
        public string Assets => Path.Combine(Root, "obj", "project.assets.json");
        public string Cache => Path.Combine(Root, "packages");

        public Fixture(int schema = 4)
        {
            Directory.CreateDirectory(Path.Combine(Root, "obj"));
            Directory.CreateDirectory(Path.Combine(Cache, "acme.parent", "1.0.0"));
            Directory.CreateDirectory(Path.Combine(Cache, "acme.child", "1.2.0"));
            File.WriteAllText(Project, "<Project />");
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graph.json"))
                .Replace("{PROJECT}", Project).Replace("{CACHE}", Cache)
                .Replace("{OUTPUT}", Path.Combine(Root, "obj"))
                .Replace("\"version\": 4", $"\"version\": {schema}", StringComparison.Ordinal);
            File.WriteAllText(Assets, fixture);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Restored_graph_tracks_CPM_multi_TFM_RID_and_transitives(int schema)
    {
        using var fixture = new Fixture(schema);
        var graph = new PackageGraphReader().Read(fixture.Assets);
        Assert.Equal(EvidenceStatus.Complete, graph.Status);
        Assert.Equal(4, graph.Packages.Count);
        Assert.All(graph.Packages.Where(p => p.Id == "Acme.Parent"),
            p => { Assert.True(p.Direct); Assert.True(p.CentrallyManaged);
                Assert.Equal("[1.0.0, )", p.RequestedRange); Assert.Equal("", p.ContentHash); });
        Assert.All(graph.Packages.Where(p => p.Id == "Acme.Child"), p => Assert.False(p.Direct));
        Assert.Contains(graph.Packages, p => p.RuntimeIdentifier == "osx-arm64" &&
            p.PackagePath == Path.Combine(fixture.Cache, "acme.child", "1.2.0"));
    }

    [Fact]
    public void Missing_stale_and_unsupported_assets_are_not_complete()
    {
        using var fixture = new Fixture();
        var reader = new PackageGraphReader();
        Assert.Equal(EvidenceStatus.Unknown, reader.Read(Path.Combine(fixture.Root, "missing")).Status);
        File.SetLastWriteTimeUtc(fixture.Project, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(EvidenceStatus.Unknown, reader.Read(fixture.Assets, [fixture.Project]).Status);
        File.WriteAllText(fixture.Assets, File.ReadAllText(fixture.Assets).Replace("\"version\": 4", "\"version\": 99"));
        Assert.Equal(EvidenceStatus.Unknown, reader.Read(fixture.Assets).Status);
        File.WriteAllText(fixture.Assets, "{");
        Assert.Equal(EvidenceStatus.Unknown, reader.Read(fixture.Assets).Status);
    }

    [Fact]
    public void Every_real_transition_has_a_new_sequence_even_when_revisited()
    {
        using var fixture = new Fixture();
        var graph = new PackageGraphReader().Read(fixture.Assets);
        var state = Path.Combine(fixture.Root, "transitions.json");
        var observer = new GraphTransitions(state);
        Assert.Empty(observer.Observe(graph));
        observer = new GraphTransitions(state);
        var current = graph.Packages.Select(p => p.Id == "Acme.Child" && p.Framework == "net8.0"
            ? p with { Version = NuGetVersion.Parse("2.0.0"), Direct = true } : p).ToArray();
        var changed = graph with { Packages = current };
        var first = observer.Observe(changed, GraphObservationOrigin.SessionEdit);
        Assert.Equal([GraphChangeKind.Upgraded, GraphChangeKind.RoleChanged], first.Select(c => c.Kind));
        Assert.All(first, c => Assert.Equal(GraphObservationOrigin.SessionEdit, c.Origin));
        var second = observer.Observe(graph, GraphObservationOrigin.EnvironmentTransition);
        Assert.Equal([GraphChangeKind.Downgraded, GraphChangeKind.RoleChanged], second.Select(c => c.Kind));
        Assert.All(second, c => Assert.Equal(GraphObservationOrigin.EnvironmentTransition, c.Origin));
        Assert.Equal(first[^1].Sequence + 1, second[0].Sequence);
        Assert.Equal(2, observer.Observe(changed).Count);
        var role = graph with { Packages = current.Select(p => p.Id == "Acme.Child" &&
            p.Framework == "net8.0" ? p with { Direct = false } : p).ToArray() };
        Assert.Equal(GraphChangeKind.RoleChanged, Assert.Single(observer.Observe(role)).Kind);
        Assert.Empty(observer.Observe(graph with { Status = EvidenceStatus.Unknown }));
        var resumed = new GraphTransitions(state);
        var resumedChange = Assert.Single(resumed.Observe(changed));
        Assert.Equal(GraphChangeKind.RoleChanged, resumedChange.Kind);
        Assert.True(resumedChange.Sequence > second[^1].Sequence);
        var withoutChild = changed with { Packages = changed.Packages.Where(p =>
            p.Id != "Acme.Child" || p.Framework != "net8.0").ToArray() };
        Assert.Equal(GraphChangeKind.Removed, Assert.Single(resumed.Observe(withoutChild)).Kind);
        Assert.Equal(GraphChangeKind.Added, Assert.Single(resumed.Observe(changed)).Kind);
    }

    [Fact]
    public void Exact_archive_skill_and_declared_readme_are_read_as_inert_bytes()
    {
        using var fixture = new Fixture();
        var archivePath = Path.Combine(fixture.Root, "sample.nupkg");
        var text = "---\nname: acme-bridge\ndescription: >\n  Upgrade bridge\n  to the next major\n---\nSee [steps](references/steps.md).";
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, "skills/acme/SKILL.md", text);
            Put(archive, "skills/acme/references/steps.md", "# migration\nSee [details](details.md).");
            Put(archive, "skills/acme/references/details.md", "# details\n");
            Put(archive, "Acme.Parent.nuspec",
                "<package><metadata><id>Acme.Parent</id><version>1.0.0</version><readme>README.md</readme></metadata></package>");
            Put(archive, "README.md", "# package");
        }
        var node = new PackageGraphReader().Read(fixture.Assets).Packages.First();
        var inventory = new PackageGuidanceReader().ReadArchive(archivePath, node);
        Assert.Equal(EvidenceStatus.Complete, inventory.Status);
        var skill = Assert.Single(inventory.Skills);
        Assert.Equal("acme-bridge", skill.Name);
        Assert.Contains("Upgrade bridge", skill.Description);
        Assert.Equal(2, skill.References.Count);
        Assert.Equal("# details\n", skill.References.Single(r => r.Path.EndsWith("details.md")).Text);
        Assert.Equal("# package", inventory.Readme!.Text);
        Assert.Contains("1.0.0", skill.Handle);
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath,
            node with { Version = NuGetVersion.Parse("2.0.0") }).Status);
        var nextArchive = Path.Combine(fixture.Root, "next.nupkg");
        using (var archive = ZipFile.Open(nextArchive, ZipArchiveMode.Create))
        {
            Put(archive, "skills/acme/SKILL.md", "---\nname: acme-bridge\ndescription: Version 2 guidance\n---\n");
            Put(archive, "Acme.Parent.nuspec",
                "<package><metadata><id>Acme.Parent</id><version>2.0.0</version></metadata></package>");
        }
        var nextSkill = Assert.Single(new PackageGuidanceReader().ReadArchive(nextArchive,
            node with { Version = NuGetVersion.Parse("2.0.0") }).Skills);
        Assert.NotEqual(skill.Handle, nextSkill.Handle);
        File.WriteAllText(Path.Combine(fixture.Cache, "Acme.Parent.nuspec"),
            "<package><metadata><id>Acme.Parent</id><version>1.0.0</version></metadata></package>");
        Assert.Equal(EvidenceStatus.Complete, new PackageGuidanceReader().ReadRestored(
            node with { PackagePath = fixture.Cache }).Status);
    }

    [Theory]
    [InlineData("../escape.md", "# bad")]
    [InlineData("skills/acme/SKILL.md", "---\nname: bad\n---\nMissing description")]
    [InlineData("skills/acme/SKILL.md", "---\nname: one\nname: two\ndescription: example\n---\n")]
    public void Rejects_unsafe_or_malformed_archives(string badPath, string content)
    {
        using var fixture = new Fixture();
        var archivePath = Path.Combine(fixture.Root, "bad.nupkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, badPath, content);
            if (badPath != "skills/acme/SKILL.md")
                Put(archive, "skills/acme/SKILL.md", "---\nname: safe\ndescription: example\n---\n");
        }
        var node = new PackageGraphReader().Read(fixture.Assets).Packages.First();
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
    }

    [Fact]
    public void Archive_rejects_collisions_and_broken_links()
    {
        using var fixture = new Fixture();
        var node = new PackageGraphReader().Read(fixture.Assets).Packages.First();
        var archivePath = Path.Combine(fixture.Root, "bad.nupkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, "skills/acme/SKILL.md", "---\nname: safe\ndescription: example\n---\n[escape](../../else.md)");
        }
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
        File.Delete(archivePath);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, "skills/one/SKILL.md", "---\nname: same\ndescription: first\n---\n");
            Put(archive, "skills/two/SKILL.md", "---\nname: same\ndescription: second\n---\n");
            Put(archive, "Acme.Parent.nuspec",
                "<package><metadata><id>Acme.Parent</id><version>1.0.0</version></metadata></package>");
        }
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
        File.Delete(archivePath);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, "skills/acme/SKILL.md", "---\nname: safe\ndescription: example\n---\n");
            Put(archive, "skills/ACME/skill.md", "collision");
        }
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
    }

    [Fact]
    public void Archive_rejects_symlink_and_size_limit_without_changing_inputs()
    {
        using var fixture = new Fixture();
        var original = SHA256.HashData(File.ReadAllBytes(fixture.Assets));
        var node = new PackageGraphReader().Read(fixture.Assets).Packages.First();
        var archivePath = Path.Combine(fixture.Root, "unsafe.nupkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Put(archive, "skills/acme/SKILL.md", "---\nname: safe\ndescription: example\n---\n");
            var link = archive.CreateEntry("skills/acme/linked.md");
            link.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(link.Open());
            writer.Write("../../outside");
        }
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
        File.Delete(archivePath);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            Put(archive, "skills/acme/SKILL.md", new string('x', 256 * 1024 + 1));
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadArchive(archivePath, node).Status);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Assets)));
    }

    [Fact]
    public void Restored_package_refuses_symlinked_skill_directories()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Cache, "skills"), outside);
        File.WriteAllText(Path.Combine(fixture.Cache, "Acme.Parent.nuspec"),
            "<package><metadata><id>Acme.Parent</id><version>1.0.0</version></metadata></package>");
        var node = new PackageGraphReader().Read(fixture.Assets).Packages.First();
        Assert.Equal(EvidenceStatus.Unknown, new PackageGuidanceReader().ReadRestored(
            node with { PackagePath = fixture.Cache }).Status);
    }

    [Fact]
    public async Task Absent_inspector_never_installs_or_restores()
    {
        var result = await new InspectorAdapter("/no/such/inspector").CheckAsync();
        Assert.Equal(EvidenceStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task Inspector_uses_returned_row_and_distinct_raw_and_JSONL_routes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var log = Path.Combine(fixture.Root, "args.log");
        var tool = Path.Combine(fixture.Root, "inspect");
        File.WriteAllText(tool, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{log}}'
            if [ "$1" = "--version" ]; then
              echo '0.26.0'
            elif [ "$1" = "project" ]; then
              case " $* " in
                *" --jsonl "*) echo '{"Row":7,"Package":"Acme.Parent","Version":"1.0.0","Path":"skills/acme/SKILL.md","Name":"acme"}' ;;
                *" --row 7 "*) echo 'selected document bytes'; if [ -f '{{Path.Combine(fixture.Root, "omit")}}' ]; then echo '[Text omitted: required containment]' >&2; fi ;;
                *) exit 2 ;;
              esac
            elif [ "$1" = "package" ]; then
              echo 'skills/acme/SKILL.md'
            else
              exit 2
            fi
            """);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var adapter = new InspectorAdapter(tool);
        var reader = new PackageGraphReader();
        var (rows, inventory) = await adapter.InventoryAsync(fixture.Project, fixture.Assets, reader);
        Assert.Equal(EvidenceStatus.Complete, inventory.Status);
        var row = Assert.Single(rows);
        Assert.Equal(7, row.Row);
        var selected = await adapter.ReadProjectSkillAsync(fixture.Project, fixture.Assets, row, reader);
        Assert.Equal("selected document bytes\n", selected.Content);
        var calls = File.ReadAllLines(log);
        Assert.Contains(calls, c => c.Contains("-S Skills --jsonl --offline"));
        Assert.Contains(calls, c => c.Contains("-S Skills --print --row 7 --raw --offline"));
        Assert.DoesNotContain(calls, c => c.Contains("--raw") && c.Contains("--jsonl"));
        File.WriteAllText(Path.Combine(fixture.Root, "omit"), "");
        Assert.Equal(EvidenceStatus.Unknown,
            (await adapter.ReadProjectSkillAsync(fixture.Project, fixture.Assets, row, reader)).Status);
        File.Delete(Path.Combine(fixture.Root, "omit"));
        File.WriteAllText(Path.Combine(fixture.Root, "NuGet.Config"), """
            <configuration><packageSources><clear /><add key="private" value="https://example.invalid/v3/index.json" /></packageSources>
            <packageSourceMapping><packageSource key="private"><package pattern="Acme.*" /></packageSource></packageSourceMapping></configuration>
            """);
        var policy = new PackageSourcePolicy(fixture.Root);
        var denied = await adapter.ListPackageSkillsAsync("Acme.Parent", "1.0.0", "private", policy, false);
        Assert.Equal(EvidenceStatus.Unknown, denied.Status);
        var listed = await adapter.ListPackageSkillsAsync("Acme.Parent", "1.0.0", "private", policy, true);
        Assert.Equal(EvidenceStatus.Complete, listed.Status);
        Assert.Contains(File.ReadAllLines(log), c => c.Contains(
            "package Acme.Parent@1.0.0 --path skills/*/SKILL.md --paths --source https://example.invalid/v3/index.json"));
    }

    private static void Put(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

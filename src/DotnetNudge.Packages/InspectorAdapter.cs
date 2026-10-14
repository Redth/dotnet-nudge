using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace DotnetNudge.Packages;

public sealed record InspectorResult(EvidenceStatus Status, string? Content, string? Diagnostic);
public sealed record InspectorSkillRow(int Row, string PackageId, string Version, string Path, string? Name);

public sealed class InspectorAdapter
{
    private readonly string _executable;
    private readonly string _expectedVersion;
    private readonly TimeSpan _timeout;

    public InspectorAdapter(string executable, string expectedVersion = "0.26.0", TimeSpan? timeout = null)
    {
        _executable = executable;
        _expectedVersion = expectedVersion;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<InspectorResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_executable))
            return new(EvidenceStatus.Unknown, null, "Pinned dotnet-inspect executable is absent; use local reader.");
        var check = await Invoke(["--version"], cancellationToken);
        return check.Status == EvidenceStatus.Complete &&
               check.Content?.Trim() == _expectedVersion
            ? check : new(EvidenceStatus.Unknown, null,
                $"Unsupported inspector version (expected {_expectedVersion}; observed {check.Content?.Trim() ?? check.Diagnostic}).");
    }

    public async Task<(IReadOnlyList<InspectorSkillRow> Rows, InspectorResult Result)> InventoryAsync(
        string projectPath, string assetsPath, PackageGraphReader reader, CancellationToken token = default)
    {
        var graph = reader.Read(assetsPath);
        if (graph.Status != EvidenceStatus.Complete)
            return ([], new(graph.Status, null, graph.Diagnostic));
        var capability = await CheckAsync(token);
        if (capability.Status != EvidenceStatus.Complete) return ([], capability);
        var result = await Invoke(["project", projectPath, "-S", "Skills", "--jsonl", "--offline"], token);
        if (result.Status != EvidenceStatus.Complete || result.Content is null)
            return ([], result);
        var rows = new List<InspectorSkillRow>();
        try
        {
            foreach (var line in result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                var obj = json.RootElement;
                if (!TryText(obj, "Package", out var id) || !TryText(obj, "Version", out var version) ||
                    !TryText(obj, "Path", out var path) || !TryNumber(obj, "Row", out var row))
                    throw new InvalidDataException("Inspector JSONL missing Package, Version, Path, or Row.");
                if (!path.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("..") || path.Contains('\\') || rows.Any(r => r.Row == row))
                    throw new InvalidDataException("Inspector returned unsafe/duplicate skill row.");
                if (!graph.Packages.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                    p.Version.ToString().Equals(version, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Inspector row is outside restored graph: {id}@{version}");
                TryText(obj, "Name", out var name);
                rows.Add(new(row, id, version, path, name));
            }
            if (reader.Read(assetsPath).Fingerprint != graph.Fingerprint)
                throw new InvalidDataException("Assets changed during inspector inventory; retry after restore settles.");
            return (rows, result);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return ([], new(EvidenceStatus.Unknown, null, ex.Message));
        }
    }

    public async Task<InspectorResult> ReadProjectSkillAsync(string projectPath, string assetsPath,
        InspectorSkillRow row, PackageGraphReader reader, CancellationToken token = default)
    {
        var (rows, inventory) = await InventoryAsync(projectPath, assetsPath, reader, token);
        if (inventory.Status != EvidenceStatus.Complete)
            return inventory;
        if (!rows.Contains(row))
            return new(EvidenceStatus.Unknown, null, "Selected row changed or is outside current inventory.");
        var fingerprint = reader.Read(assetsPath).Fingerprint;
        var content = await Invoke(["project", projectPath, "-S", "Skills", "--print", "--row",
            row.Row.ToString(System.Globalization.CultureInfo.InvariantCulture), "--raw", "--offline"], token);
        if (reader.Read(assetsPath).Fingerprint != fingerprint)
            return new(EvidenceStatus.Unknown, null, "Assets changed while reading selected skill.");
        return content.Status == EvidenceStatus.Complete && string.IsNullOrWhiteSpace(content.Content)
            ? new(EvidenceStatus.Unknown, null, "Inspector returned an empty skill document.")
            : content;
    }

    public async Task<InspectorResult> ListPackageSkillsAsync(string id, string version,
        string approvedSource, PackageSourcePolicy policy, bool acquisitionApproved,
        CancellationToken token = default)
    {
        if (!ValidIdentity(id, version))
            return new(EvidenceStatus.Unknown, null, "Invalid exact NuGet package identity.");
        var source = policy.Allowed(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { approvedSource })
            .SingleOrDefault();
        if (!acquisitionApproved || source is null)
            return new(EvidenceStatus.Unknown, null, "Explicit acquisition consent and mapped source required.");
        if ((await CheckAsync(token)).Status != EvidenceStatus.Complete)
            return new(EvidenceStatus.Unknown, null, "Pinned inspector is unavailable.");
        return await Invoke(["package", $"{id}@{version}", "--path", "skills/*/SKILL.md", "--paths",
            "--source", source.Source], token);
    }

    public async Task<InspectorResult> ReadPackageFileAsync(string id, string version, string path,
        string approvedSource, PackageSourcePolicy policy, bool acquisitionApproved,
        CancellationToken token = default)
    {
        if (!ValidIdentity(id, version) ||
            !path.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) || path.Contains("..") ||
            path.Contains('\\') || path.Contains(':'))
            return new(EvidenceStatus.Unknown, null, "Only contained skill files can be inspected.");
        var source = policy.Allowed(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { approvedSource })
            .SingleOrDefault();
        if (!acquisitionApproved || source is null)
            return new(EvidenceStatus.Unknown, null, "Explicit acquisition consent and mapped source required.");
        if ((await CheckAsync(token)).Status != EvidenceStatus.Complete)
            return new(EvidenceStatus.Unknown, null, "Pinned inspector is unavailable.");
        return await Invoke(["package", $"{id}@{version}", "--path", path, "--content", "--raw",
            "--source", source.Source], token);
    }

    private async Task<InspectorResult> Invoke(IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Inspector did not start.");
            try
            {
                var output = ReadLimited(process.StandardOutput, timeout.Token);
                var error = ReadLimited(process.StandardError, timeout.Token);
                var text = await output;
                var stderr = await error;
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0 || stderr.Contains("Text omitted:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("[Text omitted:", StringComparison.OrdinalIgnoreCase))
                    return new(EvidenceStatus.Unknown, null, $"Inspector incomplete (exit {process.ExitCode}): {stderr}");
                return new(EvidenceStatus.Complete, text, null);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(EvidenceStatus.Unknown, null, "Inspector timed out.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                   InvalidOperationException or InvalidDataException)
        {
            return new(EvidenceStatus.Unknown, null, $"Inspector unavailable: {ex.Message}");
        }
    }

    private static async Task<string> ReadLimited(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + count > 4 * 1024 * 1024)
                throw new InvalidDataException("Inspector output exceeds size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static bool ValidIdentity(string id, string version) =>
        id.Length is > 0 and <= 100 && char.IsLetterOrDigit(id[0]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
        NuGetVersion.TryParse(version, out _);

    private static bool TryText(JsonElement obj, string key, out string value)
    {
        foreach (var property in obj.EnumerateObject())
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString()!;
                return true;
            }
        value = "";
        return false;
    }

    private static bool TryNumber(JsonElement obj, string key, out int value)
    {
        foreach (var property in obj.EnumerateObject())
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out value) && value > 0)
                return true;
        value = 0;
        return false;
    }
}

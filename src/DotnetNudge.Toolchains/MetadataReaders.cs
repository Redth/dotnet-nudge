using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DotnetNudge.Toolchains;

public static class GlobalJsonReader
{
    public static AnalysisResult<GlobalJsonInfo> Parse(string json, Evidence evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<GlobalJsonInfo>("global-json.root", "global.json must be a JSON object.");
            }

            string? sdkVersion = null;
            string? workloadVersion = null;
            bool? allowPrerelease = null;
            string? rollForward = null;
            if (root.TryGetProperty("sdk", out var sdk))
            {
                if (sdk.ValueKind != JsonValueKind.Object)
                {
                    return Failure<GlobalJsonInfo>("global-json.sdk", "The sdk property must be an object.");
                }

                sdkVersion = GetString(sdk, "version");
                workloadVersion = GetString(sdk, "workloadVersion");
                rollForward = GetString(sdk, "rollForward");
                if (!HasStringOrMissing(sdk, "version")
                    || !HasStringOrMissing(sdk, "workloadVersion")
                    || !HasStringOrMissing(sdk, "rollForward"))
                {
                    return Failure<GlobalJsonInfo>(
                        "global-json.sdk-property-type",
                        "sdk.version, sdk.workloadVersion, and sdk.rollForward must be strings when present.");
                }

                if (sdkVersion is not null && !SdkVersioning.TryGetFeatureBand(sdkVersion, out _))
                {
                    return Failure<GlobalJsonInfo>(
                        "global-json.sdk-version-unknown",
                        $"SDK version '{sdkVersion}' has an unsupported format.");
                }

                if (workloadVersion is not null && !WorkloadVersioning.IsSupportedUserVersion(workloadVersion))
                {
                    return Failure<GlobalJsonInfo>(
                        "global-json.workload-version-unknown",
                        $"Workload set version '{workloadVersion}' has an unsupported user-facing format.");
                }

                if (sdk.TryGetProperty("allowPrerelease", out var prerelease))
                {
                    if (prerelease.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        allowPrerelease = prerelease.GetBoolean();
                    }
                    else
                    {
                        return Failure<GlobalJsonInfo>("global-json.allow-prerelease", "allowPrerelease must be a JSON boolean.");
                    }
                }
            }

            return new(
                new(sdkVersion, workloadVersion, allowPrerelease, rollForward),
                Assessment.Compatible,
                [evidence],
                []);
        }
        catch (JsonException ex)
        {
            return Failure<GlobalJsonInfo>("global-json.malformed", $"global.json is malformed: {ex.Message}");
        }

        AnalysisResult<T> Failure<T>(string code, string message) =>
            new(default, Assessment.Unknown, [evidence], [new(code, message, Assessment.Unknown)]);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasStringOrMissing(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.String;
}

public static class InstalledInventoryReader
{
    public static AnalysisResult<InstalledWorkloadInventory> Parse(string json, Evidence evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Installed workload inventory must be a JSON object.");
            }

            if (!root.TryGetProperty("mode", out var modeElement)
                || !root.TryGetProperty("manifests", out var manifestElement)
                || !root.TryGetProperty("workloads", out var workloadElement))
            {
                throw new JsonException("mode, manifests, and workloads are required inventory fields.");
            }

            var modeText = modeElement.GetString();
            var mode = modeText switch
            {
                "workload-set" => WorkloadInstallMode.WorkloadSet,
                "loose-manifests" => WorkloadInstallMode.LooseManifests,
                "unknown" => WorkloadInstallMode.Unknown,
                _ => throw new JsonException("mode must be workload-set, loose-manifests, or unknown.")
            };

            var manifests = new Dictionary<string, InstalledManifestInfo>(StringComparer.Ordinal);
            if (manifestElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("manifests must be an object of manifest IDs to version and SDK feature band objects.");
            }

            foreach (var entry in manifestElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException($"Manifest identity for '{entry.Name}' must be an object.");
                }

                var version = GetString(entry.Value, "version");
                var sdkBand = GetString(entry.Value, "sdkBand");
                if (version is null || !ManifestVersioning.IsSupportedManifestVersion(version)
                    || sdkBand is null || !SdkVersioning.IsCanonicalFeatureBand(sdkBand))
                {
                    throw new JsonException($"Manifest identity for '{entry.Name}' must contain a valid version and canonical SDK feature band.");
                }

                manifests.Add(entry.Name, new(version, sdkBand));
            }

            var workloads = new HashSet<string>(StringComparer.Ordinal);
            if (workloadElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("workloads must be an array.");
            }

            foreach (var item in workloadElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new JsonException("Workload IDs must be non-empty strings.");
                }

                workloads.Add(item.GetString()!);
            }

            var workloadSetVersion = GetString(root, "workloadSetVersion");
            if (mode == WorkloadInstallMode.WorkloadSet && string.IsNullOrWhiteSpace(workloadSetVersion))
            {
                throw new JsonException("workloadSetVersion is required when mode is workload-set.");
            }

            var inventory = new InstalledWorkloadInventory(mode, workloadSetVersion, manifests, workloads, evidence);
            var inventoryAssessment = mode == WorkloadInstallMode.Unknown ? Assessment.Unknown : Assessment.Compatible;
            return new(
                inventory,
                inventoryAssessment,
                [evidence],
                mode == WorkloadInstallMode.Unknown
                    ? [new Diagnostic("inventory.mode-unknown", "Installed workload update mode is unknown.", Assessment.Unknown)]
                    : []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(
                null,
                Assessment.Unknown,
                [evidence],
                [new("inventory.malformed", $"Installed workload inventory is incomplete or malformed: {ex.Message}", Assessment.Unknown)]);
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}

public static class SafeMetadataReader
{
    public const int DefaultMaximumBytes = 10 * 1024 * 1024;

    public static string ReadFile(string approvedRoot, string relativePath, int maximumBytes = DefaultMaximumBytes)
    {
        if (string.IsNullOrWhiteSpace(approvedRoot) || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A non-empty approved root and relative metadata path are required.");
        }

        var root = Path.GetFullPath(approvedRoot);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, target);
        if (relative == "." || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException("Metadata path must remain beneath the approved root.");
        }

        RejectLinkedPath(root);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0)
            {
                throw new UnauthorizedAccessException("Metadata path contains an invalid component.");
            }

            current = Path.Combine(current, component);
            RejectLink(current);
        }

        var path = current;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Metadata file was not found.", path);
        }

        if (info.Length > maximumBytes)
        {
            throw new InvalidDataException($"Metadata file exceeds the {maximumBytes}-byte limit.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBounded(stream, maximumBytes);
    }

    private static void RejectLinkedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RejectLink(current);
        }
    }

    private static void RejectLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException($"Metadata path contains a symbolic link or reparse point: '{path}'.");
        }
    }

    public static string ReadArchiveEntry(Stream archiveStream, string exactEntryName, int maximumBytes = DefaultMaximumBytes)
    {
        if (string.IsNullOrWhiteSpace(exactEntryName)
            || Path.IsPathRooted(exactEntryName)
            || exactEntryName.Contains('\\')
            || exactEntryName.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException("Archive entry must be a relative, normalized path.", nameof(exactEntryName));
        }

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries.SingleOrDefault(x => string.Equals(x.FullName, exactEntryName, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"Archive entry '{exactEntryName}' was not found.");
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Archive entry exceeds the {maximumBytes}-byte limit.");
        }

        using var entryStream = entry.Open();
        return ReadBounded(entryStream, maximumBytes);
    }

    private static string ReadBounded(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"Metadata exceeds the {maximumBytes}-byte limit.");
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }
}

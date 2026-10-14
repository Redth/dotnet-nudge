using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
        => ReadFileCore(approvedRoot, relativePath, maximumBytes, null);

    internal static string ReadFileCore(
        string approvedRoot,
        string relativePath,
        int maximumBytes,
        Action? afterPathValidation)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

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

        afterPathValidation?.Invoke();
        using var stream = OpenMetadataStream(root, relative, target);
        return ReadBounded(stream, maximumBytes);
    }

    private static FileStream OpenMetadataStream(string approvedRoot, string relativePath, string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return OpenUnixMetadataStream(approvedRoot, relativePath);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Safe metadata file opening is unavailable on this platform.");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var resolvedPath = ResolveWindowsHandlePath(stream.SafeFileHandle);
            var relative = Path.GetRelativePath(Path.GetFullPath(approvedRoot), Path.GetFullPath(resolvedPath));
            if (relative == "." || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathRooted(relative))
            {
                throw new UnauthorizedAccessException("Opened metadata file handle resolved outside the approved root.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenUnixMetadataStream(string approvedRoot, string relativePath)
    {
        var isMacOs = OperatingSystem.IsMacOS();
        var atCurrentWorkingDirectory = isMacOs ? -2 : -100;
        var noFollow = isMacOs ? 0x00000100 : 0x00020000;
        var directory = isMacOs ? 0x00100000 : 0x00010000;
        var closeOnExec = isMacOs ? 0x01000000 : 0x00080000;
        var rootDescriptor = OpenAt(
            atCurrentWorkingDirectory,
            Path.GetPathRoot(approvedRoot)!,
            directory | noFollow | closeOnExec);
        if (rootDescriptor < 0)
        {
            throw UnixOpenException("Unable to open the approved metadata root.");
        }

        using var rootHandle = new SafeFileHandle((IntPtr)rootDescriptor, ownsHandle: true);
        SafeFileHandle? currentDirectory = null;
        var currentHandle = rootHandle;
        try
        {
            var rootComponents = Path.GetFullPath(approvedRoot)
                .Trim(Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in rootComponents)
            {
                var descriptor = OpenAt(currentHandle.DangerousGetHandle().ToInt32(), component, directory | noFollow | closeOnExec);
                if (descriptor < 0)
                {
                    throw UnixOpenException("Unable to traverse the approved metadata root without following links.");
                }

                currentDirectory?.Dispose();
                currentDirectory = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                currentHandle = currentDirectory;
            }

            var components = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < components.Length - 1; i++)
            {
                var descriptor = OpenAt(currentHandle.DangerousGetHandle().ToInt32(), components[i], directory | noFollow | closeOnExec);
                if (descriptor < 0)
                {
                    throw UnixOpenException("Unable to traverse the metadata path without following links.");
                }

                currentDirectory?.Dispose();
                currentDirectory = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                currentHandle = currentDirectory;
            }

            var fileDescriptor = OpenAt(
                currentHandle.DangerousGetHandle().ToInt32(),
                components[^1],
                noFollow | closeOnExec);
            if (fileDescriptor < 0)
            {
                throw UnixOpenException("Unable to open the metadata file without following links.");
            }

            var fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            try
            {
                return new FileStream(fileHandle, FileAccess.Read);
            }
            catch
            {
                fileHandle.Dispose();
                throw;
            }
        }
        finally
        {
            currentDirectory?.Dispose();
        }
    }

    private static Exception UnixOpenException(string message)
    {
        var error = Marshal.GetLastWin32Error();
        if (error == 2)
        {
            return new FileNotFoundException(message);
        }

        if (error is 20 or 40 or 62)
        {
            return new UnauthorizedAccessException($"{message} Symbolic links and non-directory path components are not allowed.");
        }

        return new Win32Exception(error, message);
    }

    private static string ResolveWindowsHandlePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify opened metadata file path.");
        }

        if (length >= buffer.Capacity)
        {
            if (length > 32768)
            {
                throw new IOException("Opened metadata file path exceeds the supported verification length.");
            }

            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify opened metadata file path.");
            }
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenAt(int directory, string path, int flags);

    public static string ReadArchiveEntry(Stream archiveStream, string exactEntryName, int maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

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

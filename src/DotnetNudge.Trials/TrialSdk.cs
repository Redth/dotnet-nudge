using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotnetNudge.Trials;

public sealed record SdkPreparation(string Executable, string Provenance,
    string? ArchiveSha512, string ExecutableSha256);

public sealed class TrialSdk(HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient();

    public void ValidateSelection(SdkSelection sdk, bool networkAllowed)
    {
        if (sdk.Rid is not ("osx-arm64" or "osx-x64" or "linux-arm64" or "linux-x64") ||
            sdk.Rid != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
            throw new TrialException($"Unsupported or mismatched RID: {sdk.Rid}");
        if (!System.Text.RegularExpressions.Regex.IsMatch(sdk.Version, @"^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$"))
            throw new TrialException("Exact SDK version required");
        if (sdk.PreparedRoot is { } prepared)
        {
            if (!Directory.Exists(prepared)) throw new TrialException($"Prepared SDK root missing: {prepared}");
            TrialSdkLinks.InspectPreparedRoot(Path.GetFullPath(prepared));
        }
        else if (!sdk.AllowDownload || !networkAllowed)
            throw new TrialException("SDK acquisition requires explicit plan network permission");
    }

    public async Task<SdkPreparation> PrepareAsync(SdkSelection sdk, string destination, bool networkAllowed,
        CancellationToken cancellationToken, long maxBytes = 2_000_000_000)
    {
        ValidateSelection(sdk, networkAllowed);
        if (Directory.Exists(destination)) throw new TrialException("SDK destination already exists");
        if (maxBytes <= 0) throw new TrialException("SDK disk budget must be positive");
        if (sdk.PreparedRoot is not null)
        {
            var root = Path.GetFullPath(sdk.PreparedRoot);
            if (!Directory.Exists(root) || Path.GetFullPath(destination).StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                throw new TrialException("Prepared SDK root is missing or overlaps its destination");
            Directory.CreateDirectory(destination);
            try { await CopyTreeAsync(root, destination, cancellationToken, maxBytes); }
            catch { Directory.Delete(destination, recursive: true); throw; }
            var executable = Dotnet(destination);
            return new SdkPreparation(executable, $"copied prepared root: {root}", null,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))));
        }
        var channel = string.Join('.', sdk.Version.Split('.').Take(2));
        var metadata = new Uri($"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{channel}/releases.json");
        using var response = await _client.GetAsync(metadata, cancellationToken);
        if (response.RequestMessage?.RequestUri != metadata)
            throw new TrialException("Redirected release metadata is unsupported");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 10_000_000)
            throw new TrialException("Release metadata exceeds size limit");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var releases = document.RootElement.GetProperty("releases").EnumerateArray();
        var matching = releases.Select(r => r.GetProperty("sdk")).Where(s =>
            s.GetProperty("version").GetString() == sdk.Version).ToArray();
        if (matching.Length != 1) throw new TrialException("Exact SDK not found uniquely in official release metadata");
        var files = matching[0].GetProperty("files").EnumerateArray().Where(f =>
            f.GetProperty("rid").GetString() == sdk.Rid &&
            f.GetProperty("name").GetString()!.EndsWith(".tar.gz", StringComparison.Ordinal)).ToArray();
        if (files.Length != 1) throw new TrialException("Exact SDK archive not found uniquely");
        var file = files[0];
        var url = new Uri(file.GetProperty("url").GetString()!);
        if (url.Scheme != "https" || url.Host is not ("builds.dotnet.microsoft.com" or "dotnetcli.azureedge.net"))
            throw new TrialException("Unexpected SDK download host");
        var expected = file.GetProperty("hash").GetString()!;
        if (expected.Length != 128 || !expected.All(Uri.IsHexDigit))
            throw new TrialException("Missing SHA-512 archive digest");
        using var archiveResponse = await _client.GetAsync(url, cancellationToken);
        if (archiveResponse.RequestMessage?.RequestUri != url)
            throw new TrialException("Redirected SDK archive URL is unsupported");
        archiveResponse.EnsureSuccessStatusCode();
        if (archiveResponse.Content.Headers.ContentLength > maxBytes)
            throw new TrialException("SDK archive exceeds disk budget");
        Directory.CreateDirectory(destination);
        try
        {
            var archivePath = TrialPaths.Child(destination, ".sdk-download.tar.gz");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
            await using (var stream = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (archive.Length + read > maxBytes) throw new TrialException("SDK archive exceeds disk budget");
                    hash.AppendData(buffer, 0, read);
                    await archive.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new TrialException("SDK archive SHA-512 mismatch");
            await using (var archive = File.OpenRead(archivePath))
                ExtractVerifiedTar(archive, destination, maxBytes - archive.Length, cancellationToken);
            File.Delete(archivePath);
        }
        catch { Directory.Delete(destination, recursive: true); throw; }
        var dotnet = Dotnet(destination);
        return new SdkPreparation(dotnet, $"official release metadata: {metadata}; archive: {url}",
            expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dotnet))));
    }

    public static void ExtractVerifiedTar(Stream archive, string destination, long maxBytes = 2_000_000_000,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        long extracted = 0;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        while ((entry = tar.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "./") continue;
            var path = TrialPaths.Child(destination, entry.Name.TrimEnd('/'));
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
            else if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                if (File.Exists(path) || Directory.Exists(path) ||
                    string.IsNullOrWhiteSpace(entry.LinkName) ||
                    !links.TryAdd(path, entry.LinkName))
                    throw new TrialException($"Invalid or duplicate SDK archive link: {entry.Name}");
            }
            else if (entry.EntryType == TarEntryType.RegularFile || entry.EntryType == TarEntryType.V7RegularFile)
            {
                if (entry.Length > maxBytes - extracted)
                    throw new TrialException("SDK extraction exceeds disk budget");
                extracted += entry.Length;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew);
                if (entry.Length > 0)
                {
                    var input = entry.DataStream ?? throw new TrialException($"Missing SDK archive data: {entry.Name}");
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                }
                if (entry.Mode != 0 && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode.UserRead |
                    UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
            }
            else throw new TrialException($"Unsupported SDK archive entry: {entry.Name} ({entry.EntryType})");
        }
        foreach (var (path, _) in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = TrialSdkLinks.ResolveFile(destination, path, links);
            var length = new FileInfo(target).Length;
            if (length > maxBytes - extracted)
                throw new TrialException("Materialized SDK links exceed disk budget");
            extracted += length;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(target, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(target));
        }
    }

    private static string Dotnet(string root)
    {
        var path = Path.Combine(root, "dotnet");
        if (!File.Exists(path)) throw new TrialException($"Private SDK has no dotnet executable: {root}");
        return path;
    }

    private static async Task CopyTreeAsync(string source, string target, CancellationToken token, long maxBytes)
    {
        var entries = TrialSdkLinks.InspectPreparedRoot(source);
        var links = entries.Links.ToDictionary(link => link.Link, link => link.Target, StringComparer.Ordinal);
        foreach (var directory in entries.Directories)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(TrialPaths.Child(target, Path.GetRelativePath(source, directory)));
        }
        long copied = 0;
        foreach (var file in entries.Files.Concat(entries.Links.Select(link => link.Link)))
        {
            token.ThrowIfCancellationRequested();
            var resolved = links.GetValueOrDefault(file) ?? file;
            var length = new FileInfo(resolved).Length;
            if (length > maxBytes - copied) throw new TrialException("Prepared SDK exceeds disk budget");
            copied += length;
            await using var input = File.OpenRead(resolved);
            await using var output = new FileStream(TrialPaths.Child(target, Path.GetRelativePath(source, file)),
                FileMode.CreateNew, FileAccess.Write);
            await input.CopyToAsync(output, token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(output.Name, File.GetUnixFileMode(resolved));
        }
    }
}

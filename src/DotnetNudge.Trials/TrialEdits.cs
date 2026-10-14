using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotnetNudge.Trials;

public static class TrialEdits
{
    public static void UpdateSdkPin(string source, string version)
    {
        var file = Path.Combine(source, "global.json");
        if (!File.Exists(file)) return;
        var text = File.ReadAllText(file);
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
        });
        if (!json.RootElement.TryGetProperty("sdk", out var sdk) ||
            !sdk.TryGetProperty("version", out var old))
            throw new TrialException("global.json has no sdk.version");
        if (sdk.TryGetProperty("paths", out _))
            throw new TrialException("global.json SDK paths are unsupported");
        var sdkStart = Regex.Match(text, @"""sdk""\s*:\s*\{(?<body>[^{}]*)\}");
        if (!sdkStart.Success) throw new TrialException("Cannot locate global.json SDK pin");
        var section = sdkStart.Groups["body"].Value;
        var versions = Regex.Matches(section, @"""version""\s*:\s*""([^""]+)""");
        if (versions.Count != 1 || versions[0].Groups[1].Value != old.GetString())
            throw new TrialException("Ambiguous global.json SDK pin");
        var match = versions[0].Groups[1];
        var offset = sdkStart.Groups["body"].Index + match.Index;
        File.WriteAllText(file, text[..offset] + version + text[(offset + match.Length)..]);
    }

    public static void ApplyPackageEdit(string source, PackageEdit edit)
    {
        var file = TrialPaths.Child(source, edit.ProjectPath);
        var xml = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var matches = xml.Descendants().Where(e =>
            e.Name.LocalName is "PackageReference" or "PackageVersion" &&
            (string?)e.Attribute("Include") == edit.PackageId).ToArray();
        if (matches.Length != 1) throw new TrialException($"Package declaration not uniquely found: {edit.PackageId}");
        var element = matches[0];
        var attribute = element.Attribute("Version");
        var child = element.Elements().SingleOrDefault(e => e.Name.LocalName == "Version");
        if ((attribute is null) == (child is null))
            throw new TrialException($"Unsupported package version declaration: {edit.PackageId}");
        var current = attribute?.Value ?? child!.Value;
        if (current != edit.FromVersion)
            throw new TrialException($"Package version mismatch: {edit.PackageId} expected {edit.FromVersion}, got {current}");
        if (attribute is not null) attribute.Value = edit.ToVersion; else child!.Value = edit.ToVersion;
        xml.Save(file, SaveOptions.DisableFormatting);
    }
}

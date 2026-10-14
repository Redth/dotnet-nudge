using System.Text.Json;

namespace DotnetNudge.Trials;

public sealed class TrialStore(string root)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Root { get; } = Path.GetFullPath(root);

    public TrialRecord Load(string id)
    {
        var path = TrialPaths.Child(Root, Path.Combine(id, "state.json"));
        return JsonSerializer.Deserialize<TrialRecord>(File.ReadAllText(path), Options)
            ?? throw new TrialException($"Invalid trial record: {id}");
    }

    public void Save(TrialRecord record)
    {
        var directory = TrialPaths.Child(Root, record.Plan.Id);
        Directory.CreateDirectory(directory);
        var path = TrialPaths.Child(directory, "state.json");
        var temporary = TrialPaths.Child(directory, $"state-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, true);
    }

    public IDisposable Lease(string id)
    {
        var path = TrialPaths.Child(Root, Path.Combine(id, "lease.lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new TrialException($"Trial {id} is owned by another runner: {ex.Message}"); }
    }
}

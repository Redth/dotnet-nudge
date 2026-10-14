using System.Text.Json;

namespace DotnetNudge.Trials;

public sealed class TrialStore(string root)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Root { get; } = Path.GetFullPath(root);

    public string Claim(string id)
    {
        var directory = TrialPaths.Child(Root, id);
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, $"nudge-claim-{Guid.NewGuid():N}");
        if (Directory.Exists(staging) || File.Exists(staging))
            throw new TrialException("Trial staging path already exists");
        Directory.CreateDirectory(staging);
        var token = Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.EnumerateFileSystemEntries(staging).Any())
                throw new TrialException("Trial staging path was populated before ownership claim");
            using (var marker = new FileStream(Path.Combine(staging, "owner.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(marker, new Ownership(id, token));
            try { Directory.Move(staging, directory); }
            catch (IOException ex) { throw new TrialException($"Trial directory already exists: {id}: {ex.Message}"); }
            return token;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public TrialRecord Load(string id)
    {
        var path = TrialPaths.Child(Root, Path.Combine(id, "state.json"));
        var record = JsonSerializer.Deserialize<TrialRecord>(File.ReadAllText(path), Options)
            ?? throw new TrialException($"Invalid trial record: {id}");
        VerifyOwnership(id, record.OwnerToken);
        if (record.Plan.Id != id) throw new TrialException("Trial state identity mismatch");
        return record;
    }

    public void Save(TrialRecord record)
    {
        var directory = TrialPaths.Child(Root, record.Plan.Id);
        VerifyOwnership(record.Plan.Id, record.OwnerToken);
        var path = TrialPaths.Child(directory, "state.json");
        var temporary = TrialPaths.Child(directory, $"state-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, true);
    }

    public IDisposable Lease(string id)
    {
        VerifyOwnership(id, null);
        var path = TrialPaths.Child(Root, Path.Combine(id, "lease.lock"));
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new TrialException($"Trial {id} is owned by another runner: {ex.Message}"); }
    }

    public void VerifyOwnership(string id, string? token)
    {
        var markerPath = TrialPaths.Child(Root, Path.Combine(id, "owner.json"));
        if (!File.Exists(markerPath))
            throw new TrialException($"Trial ownership marker missing: {id}");
        var marker = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(markerPath))
            ?? throw new TrialException($"Trial ownership marker invalid: {id}");
        if (marker.Id != id || string.IsNullOrEmpty(marker.Token) ||
            (token is not null && marker.Token != token))
            throw new TrialException($"Trial ownership mismatch: {id}");
    }

    private sealed record Ownership(string Id, string Token);
}

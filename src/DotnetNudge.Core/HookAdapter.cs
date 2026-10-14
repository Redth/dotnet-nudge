using System.Text;
using System.Text.Json;

namespace DotnetNudge.Core;

public static class HookAdapter
{
    public const int ContextLimitBytes = 4096;

    public static string Handle(string host, string eventName, string payload, NudgeState state)
    {
        if (host is not ("copilot" or "claude"))
            throw new ArgumentException("Unsupported hook host.");
        if (eventName is not ("session-start" or "prompt-submit" or "post-tool-use" or "session-end"))
            throw new ArgumentException("Unsupported hook event.");
        if (string.IsNullOrWhiteSpace(payload)) return "{}";
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Hook payload must be an object.");
        var root = document.RootElement;
        string? String(string key) => root.TryGetProperty(key, out var property) &&
            property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        var path = String("cwd") ?? String("workspace") ?? String("workspaceRoot");
        if (path is null || !Directory.Exists(path)) return "{}";
        var (repository, worktree) = WorkspaceDiscovery.Identities(path);
        var workspace = WorkspaceDiscovery.ScopeId(path);
        var owner = host + ":" + (String("session_id") ?? String("sessionId") ?? workspace);
        if (eventName == "session-end")
        {
            state.Release(owner);
            state.Close(path, owner);
            return "{}";
        }
        state.Register(path, TimeSpan.FromMinutes(15), owner);
        if (eventName == "session-start" || eventName == "prompt-submit")
            state.QueueScan(workspace);
        if (host == "copilot" && eventName == "prompt-submit") return "{}";
        var status = state.Status(repository, worktree, workspace);
        var finding = status.Findings.Where(f => f.Eligible &&
            f.Finding.Assessment is Assessment.Confirmed or Assessment.Partial)
            .OrderBy(f => f.Finding.Priority).Select(f => f.Finding)
            .FirstOrDefault(f => state.TryLease(repository, f, owner, TimeSpan.FromMinutes(2)));
        if (finding is null) return "{}";
        var context = $"dotnet-nudge: {finding.Kind} finding {finding.Id} (revision {finding.Revision}) " +
            $"has a candidate {finding.Candidate}. Assessment: {finding.Assessment}. " +
            "Use `dotnet-nudge status --workspace PATH --json` for evidence; ask before changes.";
        if (Encoding.UTF8.GetByteCount(context) > ContextLimitBytes)
            throw new InvalidDataException("Hook context exceeds 4 KiB.");
        var output = host == "copilot"
            ? JsonSerializer.Serialize(new { additionalContext = context })
            : JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = eventName switch
                    {
                        "session-start" => "SessionStart",
                        "prompt-submit" => "UserPromptSubmit",
                        _ => "PostToolUse"
                    },
                    additionalContext = context
                }
            });
        state.MarkDelivered(repository, finding, owner);
        return output;
    }
}

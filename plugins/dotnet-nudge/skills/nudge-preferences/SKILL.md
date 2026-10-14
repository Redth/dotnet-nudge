---
name: nudge-preferences
description: Choose when .NET update reminders appear and confirm any saved changes.
---

# Nudge preferences

## Read-only assessment

- Prefer connected MCP tools. If CLI commands below need a fallback, use `node "<plugin-root>/scripts/run.cjs"` in place of `dotnet-nudge`; derive the plugin root from this installed skill's path, not a guessed engine location. The launcher can automatically install the verified engine but does not authorize preference changes.
- Explain the available reminder category, finding, and version from verified scan/status evidence. If the engine does not expose those facts, state that limitation rather than inventing settings.
- Use everyday wording such as "Remind me next week", "Don't ask about this update" or "Turn SDK reminders back on". Explain what the choice affects without internal IDs, revision tokens, assessment labels, raw JSON or ISO timestamps.
- Do not read or edit private state files to infer a preference.

## User approval

- Before asking for a reminder choice, read [Interactive questions](../nudge-maintain/references/interactive-questions.md). Call the host's native `AskUserQuestion` (Claude Code) or `ask_user` (Copilot) with one single-select question and concrete choices, not a choice menu in chat. If the tool is unavailable or the session is noninteractive, report only; never infer consent.
- Ask before changing reminder behavior. Name the category or finding and the requested duration or version boundary.
- A preference for fewer reminders is not approval to suppress security findings or mutate project/tool state.

## Structured decisions

- Keep the structured decision internally: the current finding ID/revision, category or update, exact candidate version, action and computed expiry. The user's approval is for the meaning of the reminder change, not its internal record.
- The foundation supports the structured command `dotnet-nudge decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until RFC3339] [--state-dir PATH] --json`. Run it only after approval of the described reminder change, using its current finding ID and revision. If the command is unavailable or fails, explain in plain language that the reminder change was not saved or could not be confirmed.
- `dotnet-nudge preferences --workspace PATH [--state-dir PATH] --json` lists durable category mutes even when the original finding has disappeared. With explicit approval, `--enable-category NAME` reverses an existing mute without inventing a current finding revision. MCP `nudge_preferences` supports the same `action: enable-category` plus exact `category` field.

## Supported and not yet implemented

- Decision persistence is available through CLI `decision` or MCP `nudge_decide`/`nudge_preferences` when connected. `enable-category` reverses a category disable when supported by current engine help.
- Do not claim that a snooze, skip, category setting, or cross-session preference was saved unless the decision command confirms success.
- After confirmed success, say what changed, for example "I won't bring this update up again for a week." Do not promise a timed notification: reminders become eligible at a later conversation boundary, not through a scheduled message. Do not print YAML, JSON, revision tokens or a database-style decision summary.

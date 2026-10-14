---
name: nudge-preferences
description: Capture explicit dotnet-nudge reminder choices without claiming unsupported persistence.
---

# Nudge preferences

## Read-only assessment

- Prefer connected MCP tools. If CLI commands below need a fallback, use `node "<plugin-root>/scripts/run.cjs"` in place of `dotnet-nudge`; derive the plugin root from this installed skill's path, not a guessed engine location. The launcher can automatically install the verified engine but does not authorize preference changes.
- Explain the available reminder category, finding, and version from verified scan/status evidence. If the engine does not expose those facts, state that limitation rather than inventing settings.
- Do not read or edit private state files to infer a preference.

## User approval

- Ask before changing reminder behavior. Name the category or finding and the requested duration or version boundary.
- A preference for fewer reminders is not approval to suppress security findings or mutate project/tool state.

## Structured decisions

- After the user's explicit choice, summarize:

  ```yaml
  finding_id: <verified finding ID or null>
  scope: <category, package, or finding>
  decision: <review, snooze, skip-version, disable-category, or enable-category>
  target_version: <exact version or null>
  expires: <date/duration or null>
  ```

- The foundation supports the structured command `dotnet-nudge decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until RFC3339] [--state-dir PATH] --json`. Run it only after approval of that exact record, using its current finding ID and revision. If the command is unavailable or fails, keep the summary in the conversation and report that it was not persisted.
- `dotnet-nudge preferences --workspace PATH [--state-dir PATH] --json` lists durable category mutes even when the original finding has disappeared. With explicit approval, `--enable-category NAME` reverses an existing mute without inventing a current finding revision. MCP `nudge_preferences` supports the same `action: enable-category` plus exact `category` field.

## Supported and not yet implemented

- Decision persistence is available through CLI `decision` or MCP `nudge_decide`/`nudge_preferences` when connected. `enable-category` reverses a category disable when supported by current engine help.
- Do not claim that a snooze, skip, category setting, or cross-session preference was saved unless the decision command confirms success.

---
name: nudge-preferences
description: Capture explicit dotnet-nudge reminder choices without claiming unsupported persistence.
---

# Nudge preferences

## Read-only assessment

- Explain the available reminder category, finding, and version from verified evidence. If the engine does not expose those facts, state that limitation rather than inventing settings.
- Do not read or edit private state files to infer a preference.

## User approval

- Ask before changing reminder behavior. Name the category or finding and the requested duration or version boundary.
- A preference for fewer reminders is not approval to suppress security findings or mutate project/tool state.

## Structured decisions

- After the user's explicit choice, summarize:

  ```yaml
  finding_id: <verified finding ID or null>
  scope: <category, package, or finding>
  decision: <review, snooze, skip-version, or disable-category>
  target_version: <exact version or null>
  expires: <date/duration or null>
  ```

- Keep the summary in the conversation unless an installed engine command explicitly supports preference persistence and confirms success.

## Supported and not yet implemented

- This package phase has no preference/MCP endpoint and does not persist the structured record above.
- Do not claim that a snooze, skip, category setting, or cross-session preference was saved.

---
name: nudge-maintain
description: Review .NET maintenance evidence and explain the explicitly local dotnet-nudge setup path.
---

# Nudge maintenance

## Read-only assessment

- Use a read-only maintenance command only when the installed engine's `--help` explicitly lists it. Otherwise, describe the capability as unavailable and use only evidence the user has supplied or specifically asked you to inspect.
- Keep package servicing, advisories, SDK/runtime servicing, workloads, and major framework migrations distinct. State what evidence is missing.
- Hooks never install tools, download a runtime, restore, build, test, or submit a model prompt.

## User approval

- Before a supported change, name the exact package/version or tool, files and commands involved, and whether it would download or execute anything. Ask for approval for that bounded action.
- A request to review a finding is not permission to execute a change.

## Structured decisions

- After the user selects an action, summarize it as `finding_id`, `scope`, `decision`, `target_version`, and optional `expires`. Do not write that record to disk or claim it was saved unless an advertised engine command supports persistence and confirms the write.

## Supported and not yet implemented

- No dotnet-nudge engine release is published by this repository yet. For local development, launch the host with `NUDGE_ENGINE_PATH` set to an executable supplied by the developer.
- If a trusted local release archive is supplied, use `scripts/setup-runtime.py` only with its exact published version, RID, and SHA-256. It accepts a local ZIP/tar archive; it does not download binaries or accept licenses.
- Planned release downloads and engine-driven maintenance commands are not current behavior. Do not run an assumed `dotnet-nudge scan`, MCP tool, restore, or build command.

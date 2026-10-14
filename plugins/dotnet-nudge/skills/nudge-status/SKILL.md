---
name: nudge-status
description: Check dotnet-nudge availability and report only verified .NET maintenance findings.
---

# Nudge status

## Read-only assessment

- First distinguish plugin availability from engine availability. If the hook reports that setup is required, say that no runtime assessment was performed.
- Use a read-only engine command only if the installed `dotnet-nudge --help` explicitly lists it. Do not guess a `status`, `scan`, or MCP command.
- Report the scope, evidence source, and any unavailable checks. Do not run restore, build, tests, package downloads, or SDK/workload installation as part of status.

## User approval

- Assessment is not approval to change project files, install tooling, fetch candidate packages, or start a trial.
- Before any supported mutating operation, state the exact target, files/tools affected, and execution scope, then obtain the user's approval.

## Structured decisions

- Record a decision in the conversation only after the user chooses it. Include the finding ID (if provided), scope, chosen action, exact target version, and any snooze/expiry.
- Do not claim that a decision was persisted unless an installed engine command explicitly supports that operation and confirms success.

## Supported and not yet implemented

- This package currently supports shared guidance, host-specific hook launchers, and explicit local runtime setup.
- A hook schema or a setup diagnostic is not evidence that a scan ran. No MCP endpoint or general status/scan command is promised by this package phase.

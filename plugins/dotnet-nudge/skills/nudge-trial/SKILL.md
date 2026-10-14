---
name: nudge-trial
description: Explain the proposed isolated .NET upgrade-trial boundary without claiming an unavailable trial runner.
---

# Nudge trial

## Read-only assessment

- Establish the requested project, candidate change, and evidence with `dotnet-nudge scan --workspace PATH [--state-dir PATH] --json` or `status --workspace PATH [--state-dir PATH] --json` when the installed foundation engine lists those commands in `--help`.
- Identify missing SDKs, workloads, platform tools, and build evidence as unknown or blocked; do not install or select them.

## User approval

- A trial would restore, build, test, or otherwise execute project code in an isolated environment. Do not start one without approval of the exact project, candidate, commands, environment, and budget.
- Approval for an assessment does not authorize a trial or changes to the original checkout.

## Structured decisions

- If the user defers or approves a future trial, summarize `finding_id`, `scope`, `decision`, `candidate`, and `approved_execution_boundary` in the conversation.
- Persist or execute a decision only through an installed, explicitly advertised engine capability after the user approves that specific operation.

## Supported and not yet implemented

- CLI and MCP report current findings; `nudge_trial` explicitly reports blocked coverage until the reviewed trial runner is integrated.
- Do not invoke a guessed `trial` command, simulate trial completion, or claim that schema validation proves isolated execution works.

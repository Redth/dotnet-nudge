---
name: nudge-trial
description: Review exact-approved .NET upgrade trials and their evidence and isolation limits.
---

# Nudge trial

## Read-only assessment

- Establish the requested project, candidate change, and evidence with `dotnet-nudge scan --workspace PATH [--state-dir PATH] --json` or `status --workspace PATH [--state-dir PATH] --json` when the installed foundation engine lists those commands in `--help`.
- Identify missing SDKs, workloads, platform tools, and build evidence as unknown or blocked; do not install or select them.

## User approval

- A trial restores, builds, tests, or otherwise executes trusted project code in separate Git worktrees. Git worktrees are not an OS sandbox. Do not start one without approval of the exact project, candidate, commands, environment, and budget.
- Approval for an assessment does not authorize a trial or changes to the original checkout.

## Structured decisions

- If the user defers or approves a future trial, summarize `finding_id`, `scope`, `decision`, `candidate`, and `approved_execution_boundary` in the conversation.
- Persist or execute a decision only through an installed, explicitly advertised engine capability after the user approves that specific operation.

## Supported and not yet implemented

- Obtain the engine-owned root with `dotnet-nudge trial root --workspace PATH --json` or `nudge_trial` operation `root`. Construct a complete plan matching `docs/upgrade-trials.md`, propose it, obtain explicit human approval of its exact revision, and only then execute. Status includes baseline/candidate evidence and actual test counts. Failed, missing or zero tests are not a pass.
- CLI operations are `trial propose --plan ABSOLUTE_FILE`, `approve|execute --id ID --revision N`, and `status|cancel|cleanup --id ID`, each with `--workspace PATH --json`. MCP uses `nudge_trial(workspace, operation, id?, revision?, planJson?)`. Dirty candidate worktrees refuse cleanup. A separate CLI process cannot cancel an active execution in another process; Windows trials and workload installers are unsupported.

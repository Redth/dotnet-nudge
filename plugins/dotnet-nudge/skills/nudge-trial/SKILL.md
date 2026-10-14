---
name: nudge-trial
description: Plan and explain .NET update checks in separate checkouts.
---

# Nudge trial

## Read-only assessment

- Prefer connected MCP tools. If CLI commands below need a fallback, use `node "<plugin-root>/scripts/run.cjs"` in place of `dotnet-nudge`; derive the plugin root from this installed skill's path, not a guessed engine location. Automatic verified engine setup does not authorize a trial or SDK/workload installation.
- Establish the requested project, candidate change, and evidence with `dotnet-nudge scan --workspace PATH [--state-dir PATH] --json` or `status --workspace PATH [--state-dir PATH] --json` when the installed foundation engine lists those commands in `--help`.
- Identify missing SDKs, workloads, platform tools, and build evidence as unknown or blocked; do not install or select them.
- Use plain language such as "Try it in a separate checkout" and explain what would run and what would stay unchanged. Keep internal IDs, revision tokens, assessment labels, raw JSON and ISO timestamps out of normal replies and choices. Do not hide the fact that a separate checkout is not a security sandbox.

## User approval

- Before offering trial choices, read [Interactive questions](../nudge-maintain/references/interactive-questions.md). Call the host's native `AskUserQuestion` (Claude Code) or `ask_user` (Copilot) with one single-select question and concrete choices, not a choice menu in chat. If the tool is unavailable or the session is noninteractive, report only; never infer consent. A question answer cannot replace the interactive terminal's exact trial approval.
- A trial restores, builds, tests, or otherwise executes trusted project code in separate Git worktrees. Git worktrees are not an OS sandbox. Do not start one without approval of the exact project, candidate, commands, environment, and budget.
- Approval for an assessment does not authorize a trial or changes to the original checkout.

## Structured decisions

- If the user defers or asks to try an update, explain the next step in plain language. "Help me update" or "Try it in a separate checkout" requests a plan, not execution approval. Keep finding IDs and revision tokens in tool arguments, not a structured record in the conversation; the human terminal approval must still display its required exact plan/digest.
- Persist or execute a decision only through an installed, explicitly advertised engine capability after the user approves that specific operation.

## Supported and not yet implemented

- Obtain the engine-owned root with `dotnet-nudge trial root --workspace PATH --json` or `nudge_trial` operation `root`. Construct a complete plan matching `docs/upgrade-trials.md` and propose it. MCP `approve` is refused: the human must inspect the full persisted plan and confirm its revision and SHA-256 digest in a local interactive `dotnet-nudge trial approve` terminal. Redirected/noninteractive approval fails; the agent must not impersonate the human or send the confirmation. Only then execute the already approved plan. Status includes baseline/candidate evidence and actual test counts. Failed, missing or zero tests are not a pass.
- CLI operations are `trial propose --plan ABSOLUTE_FILE`, `approve|execute --id ID --revision N`, and `status|cancel|cleanup --id ID`, each with `--workspace PATH --json`. MCP uses `nudge_trial(workspace, operation, id?, revision?, planJson?)`. Dirty candidate worktrees refuse cleanup. A separate CLI process cannot cancel an active execution in another process; Windows trials and workload installers are unsupported.

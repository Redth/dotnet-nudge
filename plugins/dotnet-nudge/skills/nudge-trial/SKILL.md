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

## Choose the experiment

.NET SDK selection and the application's target framework are separate choices. A `global.json` pin selects the SDK and its CLI/build tools; `<TargetFramework>` or `<TargetFrameworks>` selects the target API surface and runtime family. Do not describe an SDK change as a framework migration.

- **Try a newer SDK without changing the app's target** is an SDK-only experiment. Keep project TFMs and package declarations unchanged; compare the approved baseline SDK with one exact stable, RC, or preview SDK. The candidate worktree gets its own trial-local SDK pin; the original checkout stays untouched. This tests newer CLI/MSBuild/compiler behavior against the existing target. It does not prove that changing the TFM, packages, application runtime, or deployment is safe.
- **Try migrating the app to a newer .NET target** changes the TFM (for example, `net10.0` to `net11.0`) and may also need package, source, CI, container, and deployment changes. This is a framework migration, not an SDK-only trial.
- Keep an RC/preview visibly identified in the question and plan, and pin the exact SDK version. Do not substitute a stable SDK or relax the repository's SDK policy in the original checkout. The runner changes only the candidate worktree's SDK version pin and preserves the other `global.json` settings. If its explicit `allowPrerelease: false` prevents selecting the requested preview, report the trial as blocked; do not silently change the setting or claim the preview was tested.
- The current `nudge_trial` runner supports SDK-only trials and explicit, exact package-version edits. It does **not** support TFM/source migrations, workload installation, or general edits. `SdkOnly: false` means a supported package-edit trial; it does not unlock arbitrary migration commands. Do not propose or execute a framework migration through `nudge_trial`.
- If the user asks to try both, describe them as two distinct experiments with separate results. An SDK-only result can help diagnose tooling compatibility but neither completes nor automatically authorizes the framework migration.

## User approval

- Before offering trial choices, read [Interactive questions](../nudge-maintain/references/interactive-questions.md). Call the host's native `AskUserQuestion` (Claude Code) or `ask_user` (Copilot) with one single-select question and concrete choices, not a choice menu in chat. If the tool is unavailable or the session is noninteractive, report only; never infer consent. A question answer cannot replace the interactive terminal's exact trial approval.
- A trial restores, builds, tests, or otherwise executes trusted project code in separate Git worktrees. Git worktrees are not an OS sandbox. Do not start one without approval of the exact project, candidate, commands, environment, and budget.
- Approval for an assessment does not authorize a trial or changes to the original checkout.
- For an SDK-only plan, say that project TFMs and package declarations stay unchanged; name the exact baseline and candidate SDK, and say whether the candidate is a preview. Explain that restore/build/test execute project code and that results are review-only until separately adopted.
- For a requested framework migration, use the applicable version-to-version migration guidance from the official [`dotnet/skills` upgrade plugin](https://github.com/dotnet/skills/tree/main/plugins/dotnet-upgrade) when available (for example, [`migrate-dotnet10-to-dotnet11`](https://github.com/dotnet/skills/tree/main/plugins/dotnet-upgrade/skills/migrate-dotnet10-to-dotnet11) is specific to that transition). Verify that the skill matches the exact source/target versions and preview level; do not apply a mismatched guide. The current nudge trial engine cannot execute this migration. Explain that limitation and use only a separately supported, explicitly approved migration workflow; otherwise stop at a plan.

## Structured decisions

- If the user defers or asks to try an update, explain the next step in plain language. "Help me update" or "Try it in a separate checkout" requests a plan, not execution approval. Keep finding IDs and revision tokens in tool arguments, not a structured record in the conversation; the human terminal approval must still display its required exact plan/digest.
- Persist or execute a decision only through an installed, explicitly advertised engine capability after the user approves that specific operation.

## Supported and not yet implemented

- For a supported SDK-only/package trial, obtain the engine-owned root with `dotnet-nudge trial root --workspace PATH --json` or `nudge_trial` operation `root`. Construct a complete plan matching `docs/upgrade-trials.md` and propose it. MCP `approve` is refused: the human must inspect the full persisted plan and confirm its revision and SHA-256 digest in a local interactive `dotnet-nudge trial approve` terminal. Redirected/noninteractive approval fails; the agent must not impersonate the human or send the confirmation. Only then execute the already approved plan. Status includes baseline/candidate evidence and actual test counts. Failed, missing or zero tests are not a pass.
- CLI operations are `trial propose --plan ABSOLUTE_FILE`, `approve|execute --id ID --revision N`, and `status|cancel|cleanup --id ID`, each with `--workspace PATH --json`. MCP uses `nudge_trial(workspace, operation, id?, revision?, planJson?)`. Dirty candidate worktrees refuse cleanup. A separate CLI process cannot cancel an active execution in another process; Windows trials and workload installers are unsupported.

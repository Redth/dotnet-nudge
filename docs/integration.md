# Integration contract

The executable `dotnet-nudge` supports `mcp [--state-dir PATH]` as a local
stdio MCP server using the pinned official C# SDK. Connect it as a host-managed
process; its stdout is exclusively MCP protocol traffic and diagnostics go to
stderr. The same state directory is used by CLI, hooks, and MCP. Set
`NUDGE_STATE_DIR` for all three or pass `--state-dir` to each launcher.

The portable `plugins/dotnet-nudge/mcp.json` and Claude-native `.mcp.json`
both invoke the shared plugin-relative Node.js launcher. Node.js 22 or newer
must be on `PATH`; the engine does not need to be. On first connection, the
launcher automatically installs the current RID's SHA-256-verified
self-contained engine from this repository's release manifest, then runs it.
An installed engine or `NUDGE_ENGINE_PATH` override bypasses download.
`NUDGE_PLUGIN_DATA` points at the host's persistent plugin data,
matching the hook wrapper's fallback state location. `NUDGE_STATE_DIR`
overrides both when set for the host. `NUDGE_AUTO_SETUP=0` disables automatic
engine installation. A first release must exist for automatic setup to succeed;
release/download failures are stderr diagnostics, never JSON-RPC findings.
See `docs/plugin-development.md`.

The MCP tools accept an absolute `workspace` path. If the client advertises MCP
roots, the requested canonical path must be within a root. Clients without
roots can explicitly register an absolute workspace using `nudge_register`;
the server never treats its installation directory or process cwd as the
workspace. CLI and hook workspace admission also require an absolute path.
The following tools currently expose versioned JSON as text:

| Tool | Behavior |
| --- | --- |
| `nudge_register(workspace)` | Admit the exact root, keep the session active briefly, and queue a read-only scan. |
| `nudge_status(workspace)` | Read cached findings, suppressions and coverage without scanning. |
| `nudge_scan(workspace)` | Run an explicit read-only scan and persist its snapshot. |
| `nudge_context(workspace, findingId)` | Return current finding evidence without running third-party instructions. |
| `nudge_package_context(workspace, handle?)` | List exact restored-package skill handles or read one matching skill and linked references as inert data. |
| `nudge_package_assess(workspace, project, framework, packageId, approvedSources, acquisitionApproved, runtimeIdentifier?)` | After explicit approval of every named configured candidate/audit source, read metadata only for one exact restored package scope. |
| `nudge_decide(workspace, findingId, revision, action, until?)` | Record a revision-bound snooze, exact-version skip, disable-category or enable-category. |
| `nudge_preferences(workspace, findingId?, revision?, action?, category?)` | Read durable disabled categories and current suppressions; disable with current finding evidence, or re-enable a muted category by name after its finding disappears. |
| `nudge_toolchains(workspace)` | Read scoped workload/host evidence and observed local runtimes; report separate deployment-runtime coverage without installing tools. |
| `nudge_trial(workspace, operation, id?, revision?, planJson?)` | `root`, `propose`, `execute`, `status`, `cancel`, or `cleanup` an exact approved trial. `propose` takes the complete JSON plan; MCP `approve` is refused because a model tool call is not human consent. `execute` requires a previously approved revision and plan digest. |

`status`, `scan`, `package-context`, and `decision` CLI commands use the same service/state layer.
`package-assess --workspace PATH --project RELATIVE_PROJECT --framework TFM
--package ID --sources NAME[,NAME...] --approve-metadata [--rid RID] --json`
performs the corresponding explicitly approved metadata-only assessment.
`preferences --workspace PATH [--enable-category NAME] --json` reads durable
category mutes or reverses one existing mute. Re-enabling a category that is
not muted returns an error.
`toolchains --workspace PATH --json` returns the same scoped offline evidence
and observations as `nudge_toolchains`. `trial root|propose|approve|execute|status|cancel|cleanup
--workspace PATH [--plan ABSOLUTE_FILE] [--id ID] [--revision N] --json`
uses the engine-owned `<state-dir>/trials` directory. First obtain that root,
then supply a complete plan as described in `docs/upgrade-trials.md`. Proposing
is not approval: a human must use `trial approve --workspace PATH --id ID
--revision N --json` **in a local interactive terminal**. The command displays
the persisted complete plan, workspace, revision and SHA-256 digest, then
requires the exact `approve ID N DIGEST` response; redirected/noninteractive
invocations are rejected. The digest is rechecked under the trial lease before
approval. Neither MCP tools nor an agent's plan text can approve a trial.
CLI Ctrl-C cancels its own active execution; cancellation from a
separate CLI process cannot control that runner, whereas calls on one active
MCP server share its runner. Status does not expose the ownership token.
`doctor --workspace PATH --json` reports cached scope coverage without scanning
and does not infer success from an absent worker or report.
`hook` registers its host-provided workspace and queues a scan on session
start/prompt submission; session end closes the active registration and
releases delivery leases. Hooks remain cached-only and never perform scans.
The MCP process reconciles changed project/configuration/assets and local Git
metadata every minute for active registrations, leases a queued scan per exact root, and
retries read-only failures at most three times. Successful scans seed a
fingerprint of inputs captured before the scan, so repeat hook sessions do
not queue identical work; unchanged active scopes receive a bounded maintenance
refresh after six hours (including on renewed hook registration). Failed
refreshes retain bounded backoff rather than requeueing on every tick. If inputs
change during a scan, reconciliation queues the new state. It exits with its host; there
is no detached daemon. If MCP is lazily started or absent, cached status
reports pending coverage and an explicit `scan` is available. When a scan is
pending or failed, the cached snapshot is labeled stale and cannot be delivered
as a new suggestion. Worker results
cannot force a message into an otherwise idle chat.

The current scanner checks declared SDK servicing metadata and runs bounded,
read-only native `dotnet` probes for effective SDK selection per execution
scope, installed SDKs/runtimes, and local tools. Each probe records confirmed
or blocked coverage separately; an observed SDK does not turn a declared pin
into a confirmed upgrade recommendation. No restore, build, workload update,
or third-party script is performed, and at most eight execution scopes are
probed. Settled `project.assets.json` graphs are read per project/TFM/RID,
with a quiet first baseline and durable external-change deltas. Graph baselines
and scan findings commit together only after the worker's lease is accepted;
failed or cancelled scans leave changes available for the next attempt. For mapped
NuGet sources, at most four direct package scopes per scan receive metadata-only
candidate checks. Remaining resolved direct/transitive scopes receive audit-only
checks using permitted bulk vulnerability partitions (up to 512 scopes per scan);
no version query or known-fix claim is made for audit-only findings. Missing,
unsettled, unmapped, inaccessible or capped graph/advisory coverage is explicit
per project/TFM/RID/package. No metadata result proves build/runtime compatibility,
and a candidate is never labeled a known security fix without complete audit
and dependency evidence. `dotnet-nudge package-context --workspace PATH
[--handle EXACT_HANDLE] --json` exposes inert guidance from exact restored
packages; a handle must still match current assets and content before reading
it. When `NUDGE_INSPECTOR_PATH` names an already installed pinned
`dotnet-inspect` 0.26.0 executable, the selected project row is also read
offline through the inspector, retaining its original assets fingerprint.
Absent, ambiguous, or mismatched inspector results fall back to the exact
local restored reader with partial diagnostic coverage. No inspector is
installed automatically. Guidance text is untrusted data and is not
installed as a host skill.
Optional `.dotnet-nudge/toolchains.json` is an explicitly supplied, bounded
offline evidence bundle (`ToolchainEvidenceBundle`), read within the admitted
root. It can include reviewed `WorkloadAnalysisRequest`, host inventory,
target availability, suggestions, macOS workload requirements and
`RuntimeDeployment`; it does not infer host inventories, workload candidates,
or installed deployment runtimes. Invalid, absent, stale, or unmatched
evidence yields bounded blocked/unknown/stale coverage rather than a
compatibility claim. Local shared runtimes are read from `dotnet --list-runtimes`
and compared with official cached release metadata separately from explicit
container/self-contained deployment versions. No workload/SDK installer or
platform runtime smoke is included.

Package assessments include source-attributed deprecation reasons, publisher
messages and optional replacement IDs/ranges for the exact resolved version.
Scans surface independent `package-deprecation` reminders within the existing
mapped-direct-package assessment budget; audit-only transitive scopes do not
imply deprecation coverage. Replacement suggestions are untrusted metadata,
not source-query approval or verified compatibility. Deprecated/unlisted
versions are not upgrade or direct-parent remediation candidates.

Workload recommendations distinguish a newer same-band pinned-set candidate,
an absent selected workload, and an observed manifest mismatch. The current
pin is not changed. Loose-manifest inventory does not imply an installed set
comparison, and missing manifest evidence is unknown. All recommendations are
review-only, use scan-time freshness (including newer-candidate evidence), and
require the explicit offline bundle; they do not imply private-root workload
installation or host compatibility has been validated.

Official security-release SDK servicing is prioritized separately from generic
same-feature-band maintenance without claiming every CVE affects this app.
Newer major and preview SDK releases can appear as low-priority, consent-requiring
SDK-only trial proposals; they do not relax the production `allowPrerelease` pin
or imply installed SDK, workload, build or TFM compatibility.

Trials require a committed trusted repository, an exact discovered project,
prepared SDK roots or an explicitly approved download, commands and budgets
in the plan, followed by approval of its exact revision. Baseline and
candidate run in separate trial worktrees and report build/test evidence,
actual test counts and differences without changing the original checkout.
Dirty trial worktrees refuse cleanup. Git worktrees are **not an OS sandbox**:
MSBuild targets and tests may write outside them, so run only trusted code or
provide an independent sandbox. Windows trial execution and platform workload
installers are unsupported. MCP subprocess fixtures verify the protocol,
scoped scans, decisions, offline evidence, refusal of MCP approval, and
execution after a test-harness-approved exact trial; they do not
certify host conversational delivery or arbitrary untrusted build isolation.

Independent macOS headless Copilot CLI 1.0.98-2 acceptance used an isolated
`COPILOT_HOME`, the explicitly permitted `gpt-6-luna` model, a locally installed
engine on `PATH`, and a shared state path. The plugin MCP connected,
`nudge-maintain` loaded, and an actual `nudge_register`/`nudge_package_context`
roundtrip read a restored `Nudge.Acceptance` 1.1.0 package and its exact
reference bytes. A separate headless session with `NUDGE_ENGINE_PATH` and
the same state received session-start hook context for a synthetic high-priority
SDK-servicing 10.0.401-to-10.0.402 finding; `nudge_status` confirmed its
high/partial evidence without another scan. Shell, write, URL, trial, and
decision actions were denied during that acceptance. This verifies that
specific Copilot headless host path, not Claude Code, IDE hosts, other OS
conversations, or model acceptance of suggested changes.

## Native maintenance questions

Hook context names the receiving host's native user-input tool:
`ask_user` for Copilot and `AskUserQuestion` for Claude Code. MCP advice and
decision tool descriptions carry the same routing guidance, so a direct MCP
assessment need not rely on a preceding hook. All four shared skills link to
the [interactive-question contract](../plugins/dotnet-nudge/skills/nudge-maintain/references/interactive-questions.md),
including the host-specific argument shapes and action/approval boundaries.

The host agent should read current evidence, briefly explain one finding and
call its actual tool with one single-select question and 2-4 concrete choices
including `Not now`. A chat menu or JSON printed in a reply is not a picker.
The copy is conversational: "I noticed there's an update to the .NET SDK.
Would you like to hear what's new or get help updating?" Choices such as
`What's new?`, `Help me update`, `Remind me next week` and `Not now` name useful
next steps without diagnostic jargon. Keep internal IDs, revision tokens,
assessment labels, raw JSON and ISO timestamps in tool data, not in normal
explanations, questions, option descriptions or confirmations. Important
uncertainty remains explicit in everyday language, and previews remain labeled
as previews.

Questions apply to NuGet updates/advisories/deprecations, workload and host-tool
issues, and local/deployment runtime servicing, not only SDK updates. Hook
context supplies finding-specific routing, MCP descriptions reinforce it, and
the shared question reference specifies category-specific questions and choices.
A current update check can lead to one native question per actionable category:
SDK, NuGet (`package`), workload, runtime, and host tools (`toolchain`). Ask
sequentially, waiting for each answer, and group findings within a category
rather than asking once per dependency. Explicit report-only/cached-status
requests remain reports. Prioritize higher-priority categories, but do not stop
after asking about SDKs when another category has actionable evidence. An
answer applies only to its described findings, not to every category.

Deprecations and advisories without a candidate offer help finding an
alternative or fix instead of promising an update. Workload mismatches and
already-observed graph changes are not upgrades. Offline workload evidence is
not live discovery; local runtime installation is not a deployment fix.
Unknown, blocked or stale coverage alone does not authorize an update prompt.
These routes preserve subject cooldowns and durable suppressions while making
routine delivery limits category-specific: one per category per session and
one per repository/category per 24 hours.

`What's new?` is read-only. `Help me update` leads to a supported plan and
approval of its actual changes, not immediate editing or installation.
Snooze/skip/category mutations require the user's explicit reminder choice,
bound internally to its exact update/category, revision and any expiry.
No answer, dismissal, cancellation or `Not now` records a durable decision.
Trial approval still requires the
human's persisted-plan revision/digest confirmation in the interactive CLI.

This is host-agent guidance, not a new hook output schema or server-side MCP
elicitation implementation. Hooks and MCP never invoke a host tool themselves.
If the tool is unavailable, denied or the session is noninteractive, report the
evidence without inferring consent or executing an action. Existing delivery
leases are still atomic across sessions; context delivery does not prove that a question
was shown. A freshly hook-delivered ID/revision may be offered once despite its
`delivered-recently` status, which records that context delivery, not an answer.
Stale evidence and durable snooze/skip/mute decisions still prevent unsolicited
questions. Contract and protocol tests check routing and schemas, not live GUI
rendering or model compliance. The headless acceptance above does not certify
interactive question presentation.

Hook context can contain one leased finding per actionable category within the
4 KiB cap, with shared instructions to ask separate sequential questions. If
the cap leaves a category out, it is not leased or marked delivered and can be
offered on a later hook. Legacy uncategorized delivery rows retain their
previous repository-wide cooldown until it expires; new delivery rows carry
categories.

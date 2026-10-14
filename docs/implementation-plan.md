# dotnet-nudge implementation plan

Research date: October 14, 2026.

Status: the architecture below is the original research and roadmap; a scoped
prototype is implemented. At the start of this research, the repository
contained a README, license, and ignore file.

## Current prototype status

Core state/decisions, restored-package graph intelligence and inert guidance,
offline toolchain analysis, approved isolated trials, cached host hooks, a CLI,
and a local stdio MCP endpoint are implemented. Linux, macOS, and Windows CI
build and test the applicable components and self-contained engine; trial
execution remains unsupported on Windows. Independent macOS headless Copilot
CLI acceptance with an explicitly installed engine verified MCP connection,
restored package-guidance bytes, and session-start delivery of a synthetic
high-priority SDK finding using an explicitly permitted model. That is not
Claude Code, IDE, or all-platform conversational certification.

Package-source/advisory gaps, absent or stale workload/host/deployment evidence,
unsupported platform tooling and runtime smoke, and the need for local engine
installation remain explicit coverage limits. Trial worktrees are not an OS
sandbox; only trusted build/test code is appropriate without another approved
isolation boundary. Later roadmap phases and all-platform workload automation
are not marked complete. See [development](development.md) for commands and
[integration](integration.md) for public contracts, evidence, and host limits.

## 1. Recommendation

Build **one deterministic .NET maintenance engine, distributed through this
repository's plugin marketplace, with thin adapters for each agent host**.
Use hooks to observe changes and deliver compact findings. Use skills and local
MCP tools to let the user's existing agent explain findings, record decisions,
and carry out explicitly approved upgrades.

Do not make every hook run an LLM prompt, restore the solution, or query every
package feed. Most hook invocations should perform inexpensive local bookkeeping
and return nothing.

The initial product should:

1. Find actionable NuGet vulnerabilities and package updates.
2. Observe resolved dependency changes regardless of who made them, and surface
   relevant version-matched package guidance.
3. Find relevant SDK, runtime, and workload servicing updates without conflating
   them with framework migrations.
4. Offer reproducible, isolated upgrade trials with evidence and a reviewable
   diff, leaving the original working folder and machine-wide tools alone.
5. Remember the user's decisions across sessions and worktrees, with explicit
   rules for reminders and newly released versions.

Start with Copilot CLI and a Claude Code adapter. Supporting Claude Code as a
host does not require invoking Claude models from the engine. The engine must
not choose a model or hold provider credentials: it uses whichever permitted
agent/model the developer already selected.

### Product boundaries

Automatic by default, after workspace admission and local setup:

- Read workspace metadata and existing restored dependency graphs.
- Fetch approved release/package metadata on a cache-controlled schedule.
- Compare snapshots, prioritize findings, and offer suggestions.
- Read already approved package guidance as data.

Requires explicit approval or previously configured, narrowly scoped policy:

- Restore/evaluate a project, execute a build, or run tests.
- Change package references, lock files, `global.json`, or repository skills.
- Download candidate packages for guidance inspection.
- Install SDKs/workloads or create and execute an upgrade trial.
- Install or select external platform tooling.

Never implied by accepting a suggestion:

- Change the original checkout during a trial.
- Update machine-wide SDKs/workloads, switch global Xcode selection, accept
  licenses, delete user changes, push a branch, open a PR, or merge.

## 2. Verified platform constraints

These are implementation inputs, not interchangeable APIs.

| Area | Research finding | Design consequence |
| --- | --- | --- |
| Copilot marketplace | Copilot CLI accepts `.github/plugin/marketplace.json` and also discovers `.claude-plugin/marketplace.json`. [S1] | A minimal shared marketplace can initially live in `.claude-plugin/marketplace.json`. |
| Portable plugins | Agent Plugins 1.0 standardizes skills and MCP, not portable hooks or agents. Copilot reads native hooks from `com.github.copilot/hooks/hooks.json` in that format. [S2, S3] | Share the engine and skills; provide host-specific hook configuration. |
| Copilot session context | Command `sessionStart` supports top-level `additionalContext`. [S4] | Deliver cached findings at startup/resume without submitting a new user prompt. |
| Copilot prompt-submit | Config-file command/HTTP `userPromptSubmitted` output is dropped; `modifiedPrompt` is SDK-programmatic-only. [S4] | Use this event for bookkeeping, not the main context injection mechanism. |
| Copilot tool context | `postToolUse` supports top-level `additionalContext`, appended to the model-facing tool result. [S4] | Deliver newly observed package changes and queued findings at a tool boundary. |
| Copilot prompt hooks | `type: prompt` is session-start-only and fires only for new interactive sessions, not resume or `-p`. [S4] | Do not depend on prompt hooks for normal scanning or reliable delivery. |
| Copilot newer events | Current docs include `userPromptTransformed` and notification context injection. [S4] | Consider version-gated enhancements, not MVP dependencies or a reason to rewrite user prompts. |
| Claude Code hooks | `SessionStart`, `UserPromptSubmit`, and `PostToolUse` can add context using host-specific output contracts. `Stop` can force continuation. [S5] | Implement a separate serializer; never use stop-blocking to nag users. |
| Claude skill refresh | Current `SessionStart` supports `reloadSkills`; discovery normally precedes startup hooks. [S5] | Gate refresh by tested host version; mid-session file writes alone do not guarantee activation. |
| NuGet commands | JSON package-list output exists from SDK 7.0.200. .NET 10 introduced noun-first commands and automatic restore unless disabled. Outdated and vulnerable queries cannot be combined. [S6] | Probe capabilities, use separate queries, and prevent automatic restore in passive checks. |
| Feed privacy | Package Source Mapping does not currently constrain all CLI metadata queries; package IDs can be sent to every configured feed. [S7] | Apply mapping in our own queries. Do not blindly delegate a mixed-feed scan to package-list commands. |
| Package skills | NuGet archives can carry `skills/<name>/SKILL.md` and references; `dotnet-inspect` already exposes restored-project skills and package documents. The open MAUI Controls PR is a concrete packaging example. [S8, S12] | Integrate existing packaging/discovery now; distinguish reading skills from installing or activating them in an agent host. |
| SDK selection | `global.json` SDK resolution depends on working directory, ancestors, roll-forward, prerelease settings, and newer `paths` support. [S9] | Observe the effective SDK, not just a version string in the nearest file. |
| Workloads | Workload sets exist from SDK 8.0.400, have their own versions, and can be pinned with `sdk.workloadVersion`. [S10] | Compare SDKs, sets, manifests, and external dependencies separately. |

**First implementation gate:** verify these contracts with recorded payloads
and supported host releases before advertising automatic behavior. Documentation
and host versions change quickly; unsupported capabilities must degrade visibly
to manual status/skills, not silently pretend to work.

## 3. Architecture

```text
Agent host
  |
  +-- native hook adapter ---- event / small snapshot check
  |                                  |
  |                                  v
  |                         durable local job/state store
  |                                  |
  +-- local MCP process ------ bounded scan worker
  |                                  |
  |                      discovery -> inventory -> detectors
  |                                  |
  |                         evidence-backed findings
  |                                  |
  |                      policy / cooldown / delivery lease
  |                                  |
  +<-- hook context / tool result <---+
  |
  +-- existing agent + skills ---- explain / ask / record decision
                                       |
                                       v
                              approved trial workflow
```

### Components

| Component | Responsibility |
| --- | --- |
| Workspace discovery | Identify admitted repository/workspace, projects, solution entry points, SDK scopes, and configuration inputs. |
| Inventory | Normalize declared versions, restored graphs, effective SDKs, runtimes, workloads, and platform tools. |
| Detectors | Produce structured package, security, toolchain, and guidance findings. |
| State and policy | Cache metadata, compare snapshots, record decisions, coordinate sessions, and suppress repeats. |
| Hook adapters | Normalize host events and serialize only supported host outputs. |
| Local MCP/CLI | Expose explicit scans, details, decisions, and trial lifecycle operations. |
| Skills | Explain findings and guide a bounded migration using the existing agent. |
| Trial runner | Own worktree/environment preparation, execution boundaries, and reproducible evidence. |

Use one executable with `hook`, `scan`, `status`, `decision`, `trial`, `doctor`,
and `mcp` entry points over the same core library. Avoid separate algorithms in
shell scripts, separate services per detector, or a custom agent orchestration
framework.

### Implementation stack

- C# on a supported LTS .NET release.
- `System.Text.Json` for versioned contracts and bounded JSON parsing.
- Official NuGet libraries for configuration, protocol access, package/archive
  inspection, framework compatibility, and NuGet version/range semantics.
- SQLite for snapshots, decisions, jobs, and atomic leases. Start with direct
  SQL and a small repository layer, not an ORM.
- A local stdio MCP endpoint in the same executable.
- Self-contained executables so the plugin still works when the workspace SDK
  is absent, incompatible, or pinned to an older version.

Initially publish conventional self-contained builds. Investigate Native AOT
only after startup measurements and dependency compatibility checks justify it;
do not make NuGet reflection/AOT limitations a prerequisite for the MVP.

### Finding contract

Each finding needs:

```json
{
  "schemaVersion": 1,
  "id": "opaque-stable-finding-id",
  "subjectKey": "package:contoso.widgets:security",
  "kind": "package-vulnerability",
  "priority": "high",
  "current": "1.4.0",
  "candidate": "1.4.3",
  "scope": {
    "project": "src/App/App.csproj",
    "framework": "net10.0"
  },
  "assessment": "confirmed",
  "evidence": [
    {
      "type": "advisory",
      "uri": "https://example.invalid/advisories/EXAMPLE",
      "retrievedAt": "2026-10-14T09:00:00Z"
    }
  ],
  "action": "review-minimal-fix",
  "requiresConsent": true
}
```

The example is a proposed output shape, not a real advisory. Also represent
`unknown`, `partial`, `stale`, and `blocked` assessments. A failed feed, missing
assets, or unsupported SDK is not "no vulnerabilities" or "everything current".

## 4. Marketplace, plugin layout, and distribution

Use marketplace name `dotnet-nudge-marketplace` and plugin name `dotnet-nudge`.
Keep this repository as both implementation source and initial catalog.

Proposed layout:

```text
.claude-plugin/
  marketplace.json
plugins/dotnet-nudge/
  plugin.json                         # Agent Plugins manifest for portable hosts
  mcp.json                            # portable MCP configuration
  .claude-plugin/plugin.json          # Claude-native manifest
  .mcp.json                           # Claude-native MCP configuration
  com.github.copilot/hooks/hooks.json # Copilot-native events/output adapter
  hooks/hooks.json                    # Claude-native events/output adapter
  skills/
    nudge-status/SKILL.md
    nudge-maintain/SKILL.md
    nudge-trial/SKILL.md
    nudge-preferences/SKILL.md
  scripts/                            # small platform launch/setup wrappers
  runtime-manifest.json               # pinned executable release + checksums
src/
  DotnetNudge.Core/
  DotnetNudge.Cli/
tests/
  DotnetNudge.Tests/
  fixtures/
docs/
```

Validate the dual-manifest layout on both hosts in the initial spike.
Copilot's canonical root manifest takes precedence over legacy manifests;
Claude-native files must not cause duplicate MCP servers or duplicate hooks.
If a tested host cannot cleanly consume this layout, generate separate host
plugin directories from the same sources rather than forcing shared semantics.

Use one minimal catalog with only mutually supported fields. Generate a
Copilot-native catalog later if necessary; never hand-maintain divergent copies.
An installed plugin must be self-contained: no `../../src` dependencies outside
its copied plugin directory.

Planned installation, once published:

```text
copilot plugin marketplace add Redth/dotnet-nudge
copilot plugin install dotnet-nudge@dotnet-nudge-marketplace

claude plugin marketplace add Redth/dotnet-nudge
claude plugin install dotnet-nudge@dotnet-nudge-marketplace
```

These are future user instructions, not commands executed by this research.

### Executable acquisition

Do not put every platform binary in Git history. Publish versioned release
archives for macOS, Windows, and Linux, initially x64/arm64 where supported.
The plugin pins the engine version and archive digest.

An explicit first-use setup skill downloads the matching archive from this
repository's release, verifies its digest/signature or release provenance,
and installs it into writable local data. Hooks must never silently download
and execute a new binary or run `curl | sh`.

If the executable is missing, expose a single setup-required diagnostic and
leave the session functional. Use host-supported writable data directories;
do not write generated state into the installed plugin cache. Engine/plugin
contract version mismatch must produce a clear update/setup diagnostic.

## 5. Workspace discovery and passive scanning

### Scope

Discover `.sln`, `.slnx`, `.slnf`, SDK-style C#/F#/VB projects, shared props/targets,
central package files, `global.json`, NuGet configuration, and local tool manifests.
Support multiple solutions and nested SDK scopes; a repository is not necessarily
one homogeneous .NET application.

Skip generated/vendor/build directories during project discovery. Nevertheless,
read known `obj/project.assets.json` paths for discovered projects: excluding
`obj` from recursive discovery must not exclude the restored graph.

Resolve the Git common directory and worktree directory separately. Use:

- A repository identity derived locally from the canonical common Git directory.
- A worktree identity for snapshots and in-progress jobs.
- Project-relative paths plus TFM/RID for graph identities.

This allows decisions to follow a repository across its worktrees while
preventing one branch's dependency graph from being mistaken for another's.
Do not use branch names or remote URLs as the only repository identity. Separate
clones remain separate unless the user explicitly links their preferences.
For non-Git folders, use the admitted canonical workspace path.

Ancestor `global.json` and imported build/configuration files may affect behavior
outside the repository. Inspect them only within the host's allowed boundaries;
if necessary inputs are inaccessible, report incomplete scope rather than
assuming defaults or editing those external files.

### Inventory tiers

| Tier | Work | Default |
| --- | --- | --- |
| Passive | Read declared configuration, existing assets/lock files, cached package text, and approved metadata feeds. | Automatic. |
| Tool inventory | Probe effective `dotnet`/workload/tool versions with tested, bounded commands. | After workspace admission; cache and account for possible SDK side effects. |
| Evaluated | Run selected SDK's MSBuild/restore or an equivalent project evaluation. | Explicit permission/policy; not inside a latency-critical hook. |
| Trial | Install private tools, restore/build/test changed code. | Separate approval and budget. |

Static XML discovery identifies possible declarations, not authoritative evaluated
versions. Conditions, property indirection, custom imports, SDK inference, and
Central Package Management require the actual restored/evaluated graph.

Track assets schema versions and custom intermediate-output locations. Prefer
official readers; test supported v3/v4 graph shapes. Missing or apparently stale
assets yield "declared changes; restore needed to verify", not an invented
resolved version. Non-SDK `packages.config` can initially receive inventory-only
support with an explicit limitation; full legacy build support is later work.

### Efficient cadence

Proposed defaults, configurable:

| Check | Refresh/invalidation |
| --- | --- |
| Relevant file fingerprints | Startup/resume, relevant tool events, and worker reconciliation. |
| Vulnerability metadata | Every 6 hours; reuse feed partition/update information. |
| Package version metadata | Every 24 hours, or an explicit refresh. |
| .NET release metadata | Every 24 hours, or an explicit refresh. |
| Package guidance index | Only changed package identity/content hashes. |
| Installed tool inventory | Startup and explicit/local toolchain-change signals, not every tool call. |

Use conditional HTTP requests, bounded parallelism, timeouts, cancellation,
jittered retries, and per-source backoff. Cache package metadata by allowed source,
package ID, and channel; cache immutable package contents by identity and hash.
Never cache authentication failures as a successful empty result.

Fingerprint the known input set, including imported inputs when available.
Perform a low-frequency discovery reconciliation to catch new/deleted projects
and files that were not in the previous set. A watcher overflow triggers full
reconciliation, not a claim that nothing changed.

## 6. Hook lifecycle and delivery

### Event handling

| Event | Action |
| --- | --- |
| Session start/resume | Register admitted workspace, reconcile a cheap snapshot, queue stale checks, return eligible cached context. |
| User prompt submitted | Update session presence and reconcile/schedule cheap checks. Claude can deliver pending context here; Copilot command hooks cannot. |
| Relevant post-tool event | Mark changed inputs dirty; compare settled graphs; deliver concise newly available context using the host's supported contract. |
| Turn stop | Bookkeeping only. Never block completion to force a maintenance conversation. |
| Session end | Release session-owned delivery leases and worker registration; preserve durable decisions/results. |

Filter edit/write events by file path where available. Shell events are only
hints: inspect fingerprints after commands that may restore/update/switch Git
state, and reconcile periodically because arbitrary shell commands, IDE actions,
and external tools cannot be recognized reliably from command text.

Prefer returning original tool results unchanged with additional context.
Do not rewrite the user's prompt just to append a maintenance message.

For example, the adapters intentionally emit different shapes:

```json
{
  "additionalContext": "dotnet-nudge: one eligible finding is ready; use nudge_status for details."
}
```

```json
{
  "hookSpecificOutput": {
    "hookEventName": "SessionStart",
    "additionalContext": "dotnet-nudge: one eligible finding is ready; use nudge_status for details."
  }
}
```

The first is the Copilot native session output; the second is Claude native.
Their input naming, matcher semantics, timeouts, and supported event outputs
must be covered by adapter fixtures.

### Background work without fragile detached processes

Use the host-managed local MCP process as the bounded worker while the session
is alive. Hooks enqueue durable jobs; the worker reconciles queued jobs at
startup and while registered sessions remain active. Multiple host processes
use leases so only one owns a scan for a workspace.

Do not assume an MCP server is started eagerly or inherits the repository as
its current directory. Obtain roots via supported MCP roots/explicit workspace
registration and trusted hook records, canonicalize them, and respect admission.
The initial spike must verify activation timing and allowed filesystem access.

If the MCP process is lazy or unavailable, return an honest scan-pending state
and have the startup/status skill run the bounded scan through a tool.
A host-supported asynchronous command hook is an optional, tested optimization,
not an assumed portable worker lifecycle. Do not spawn orphan shell daemons
from a hook and assume the host will preserve them.

Jobs survive process exit; an expired lease makes an interrupted read-only scan
eligible for retry. Do not replay partially executed mutation jobs automatically.

### Delivery guarantees and limitations

Cached startup findings and same-turn post-tool context provide the baseline.
Fresh scan results become available at the next supported conversational/tool
boundary. No universal hook can guarantee a pop-up or a new assistant message
in an otherwise idle session.

Later, opt-in host notification capabilities can wake supported sessions, with
additional duplicate/recursion tests. Manual status must always show pending
findings. Headless/CI modes return reports and do not ask questions or spend
model credits to initiate suggestions.

Target cached/no-op hook p95 below 150 ms on a defined reference machine,
a 500 ms local hard deadline, and no network/MSBuild work on that critical path.
If the first managed executable misses this budget, benchmark a smaller launcher
or AOT before adding a service. Suggested context has a hard 4 KiB UTF-8 budget,
normally one actionable question plus a details handle.

## 7. NuGet updates and vulnerabilities

### Sources of truth

Use resolved package identities from the existing graph and lock files.
Preserve project, TFM/RID, direct/transitive role, requested range, central
declaration location, and known source provenance.

Use official NuGet configuration/protocol/versioning libraries to:

- Respect configuration hierarchy, disabled sources, source mapping, and
  available credential providers.
- Query only explicitly allowed sources for each package.
- Load vulnerability resources from configured audit sources and match ranges
  locally. Keep audit-data sources distinct from package download sources.
- Read candidate dependency groups/framework compatibility before recommending
  an upgrade as compatible.

NuGet's partitioned `VulnerabilityInfo` API avoids one advisory network lookup
per dependency. [S11] Refresh complete applicable partitions correctly; changed
or deleted advisories cannot be handled by endlessly appending old cache data.

Credential-provider acquisition must be an explicit implementation/test concern:
using `NuGet.Protocol` alone does not guarantee every IDE credential setup works.
Do not launch interactive authentication from background hooks. Report
authentication-required and let an explicit scan perform approved authentication.

### CLI fallback and verification

Where feed disclosure is permitted, supported CLI commands can verify output:

```text
# .NET 10+, separate queries; do not restore implicitly:
dotnet package list --project <target> --outdated --include-transitive --no-restore --format json --output-version 1
dotnet package list --project <target> --vulnerable --include-transitive --no-restore --format json --output-version 1

# Older supported SDKs use verb-first syntax:
dotnet list <target> package --outdated --include-transitive --format json --output-version 1
dotnet list <target> package --vulnerable --include-transitive --format json --output-version 1
```

Probe flags against the selected SDK, including `--no-restore`; the older command
requires preexisting assets. Parse the supported JSON contract, not localized
table output. A mixed private/public feed configuration needs our mapped-source
implementation or explicitly filtered queries, not merely these blanket examples.

### Candidate selection

Distinguish:

- Minimal known security fix.
- Same-major/minor patch update.
- Same-major minor update.
- Major-version upgrade.
- Preview/RC update.
- Deprecated or unsupported dependency.

A "latest" version is not necessarily compatible with the project's target
framework, other package constraints, or runtime behavior. Treat static
compatibility as candidate evidence; only a trial can establish the project's
actual restore/build/test result.

For a vulnerable transitive dependency, identify the nearest direct dependency
that can lift it to a fixed version. Prefer that upgrade to blindly inserting
a new direct reference. A temporary direct pin can be proposed explicitly,
with its reason and removal condition.

For grouped families such as EF Core or MAUI, propose aligned sets based on
actual dependency constraints and workload requirements, not a universal rule
that all Microsoft packages share the TFM's major version.

If no compatible fixed version is available, report that fact and alternatives;
do not present the latest vulnerable version as a security fix. Existing audit
suppression is evidence to explain, not permission to change/suppress warnings.

## 8. Detecting package changes and using package context

This is a separate feature from checking whether updates are available.

### Reliable transition detection

Persist a per-worktree resolved graph snapshot:

```text
(project, framework, runtime identifier, package ID, resolved version,
 direct/transitive role, content identity where available)
```

Compare it after a settled restore and on startup/reconciliation. Record:
added, removed, upgraded, downgraded, and role-changed packages.
Use a separate declaration snapshot to identify pending version edits.

Do not classify a `.csproj` edit as a completed upgrade before restore resolves
the new graph. Debounce partially written assets, check file identity/size/hash
before and after reading, and retry an unstable snapshot.

Create an initial baseline without inventing a mass-upgrade event. On first
install, relevant existing guidance can be offered as an inventory finding.
After branch switches, distinguish environment/branch transitions from edits
performed during the current session.

A restore returning to an already seen graph can still be a real downgrade or
upgrade transition. Deduplicate by transition sequence and session delivery,
not by permanently suppressing an `(old, new)` pair.

### Package guidance discovery

NuGet-carried skills are a concrete integration target, not a hypothetical
future format. Use the existing `skills/<skill-name>/SKILL.md` package layout
and supporting references demonstrated by the Controls PR and supported by
`dotnet-inspect`; do not require package authors to adopt a new dotnet-nudge
manifest. [S8, S12]

Inspect only package identities actually used by the affected graph, locating
them through assets/configuration rather than assuming `~/.nuget/packages`.
Do not scan the developer's entire shared package cache.

Discovery order:

1. `skills/<skill-name>/SKILL.md` and bounded supporting text below that directory.
2. The package's declared README and explicitly identified migration material.
3. Known package-specific context conventions supported by a tested adapter.
4. Opt-in, pinned external mappings to official migration skills/docs.

There is no verified universal NuGet "AI context manifest" that we should invent
and then assume publishers already implement. This does not diminish the
existing package-skill layout/tooling; it means other context formats remain
best-effort evidence, labeled as such.

Keep these operations distinct:

| Operation | Mechanism |
| --- | --- |
| Package the skill | Publisher includes `SKILL.md` and references in the NuGet archive. |
| Discover/read the skill | Inspect the exact restored package or candidate archive; `dotnet-inspect` already supports this. |
| Use the guidance | The existing agent reads matching text and references through nudge's scoped context workflow. |
| Install as a host-discoverable skill | Optional copy/registration and refresh into a supported agent skill location, with separate ownership and approval rules. |

An agent can use the skill by reading it without installing a repository-wide
copy. NuGet restore supplies the package files, not an instruction to execute
them or a guarantee of host skill activation.

The Controls prototype is especially relevant: its skill is a **pre-upgrade
bridge** to MAUI 11, not merely guidance describing the installed major. [S8]
Distinguish carrying package version from the target versions described by the
skill. Index frontmatter name/description and preserve provenance; ask the agent
to determine applicability rather than guessing from a filename alone.

### Before and after an upgrade

Before: read bridge guidance from the current package. With approval, inspect
the exact candidate archive from its permitted source without restoring or
executing package build assets. This solves the chicken-and-egg problem where
the new version's migration instructions are needed before selecting it.

After: compare guidance identities/content hashes in old and new packages.
Provide a concise digest of changed skills and references, and offer the next
applicable migration/feature task. Do not automatically enable every new feature
or rewrite functioning code because a package README suggests modernization.

Default to metadata/index delivery; load the full matching skill only during
an approved maintenance task. Keep package version, source, file hashes, and
project scopes attached to every context item.

### Guidance trust and host activation

Package text is untrusted third-party content. A signed package or a trusted
publisher does not make every instruction safe.

- No hooks, scripts, MCP servers, binaries, or tool-permission grants are
  activated just because they occur inside a NuGet package.
- Archive paths and linked files must stay inside the intended skill/package
  root after canonical resolution; reject traversal, unsafe links, collisions,
  oversized/decompression-bomb archives, and malformed metadata.
- Scope trust to source/package and, where appropriate, content identity.
  Privilege requests and external destinations still need separate consent.
- Never let package text override the host's instructions or user permissions.
- Start with direct package guidance; expose transitive guidance only when it
  is relevant to a finding or explicitly requested.

A package can be referenced at different versions by different projects.
Keep distinct versioned context handles; never silently install one repository-wide
copy and imply it matches both.

Prefer on-demand reading through `nudge_context` to copying package text into
host skill discovery directories. If the user wants installed skills, maintain
ownership/content hashes, preserve local edits, preview conflicts/removals,
and follow tested host reload behavior. Never overwrite unrelated skills.

### Concrete `dotnet-inspect` integration

Use a pinned, capability-checked `dotnet-inspect` adapter as the preferred reuse
path for explicit package-guidance inspection and trial preparation. It already
supports discovery and reading; dotnet-nudge adds change observation, applicability
context, decision memory, and host delivery rather than rebuilding the inspector.

The current CLI reference documents restored-project skill inventory, machine
output, selected-document reading, and explicit package-file inspection. [S12]
The MAUI PR also records these capabilities tested with `dotnet-inspect` 0.26.0;
its section-based package examples are version-specific. [S8]

Planned adapter flow:

1. Detect a settled package-graph transition and identify the affected
   project/package/version scopes.
2. Inventory package skills using machine-readable output:

   ```text
   dotnet-inspect project MyApp.csproj -S Skills --jsonl
   ```

3. Match returned entries to the relevant package/version and inspect their
   names/descriptions. Select the returned row from that inventory, not a
   hardcoded assumption that row 1 is always the Controls skill.
4. Read the selected skill document:

   ```text
   dotnet-inspect project MyApp.csproj -S Skills --print --row <returned-row> --raw
   ```

5. For explicit pre-upgrade candidate inspection, list and read exact package
   files with the documented package-file route:

   ```text
   dotnet-inspect package Contoso.Widgets@1.4.3 --path "skills/*/SKILL.md" --paths
   dotnet-inspect package Contoso.Widgets@1.4.3 --path skills/widgets-upgrade/SKILL.md --content --raw
   ```

6. Resolve referenced documents from the same package identity and allowed
   skill subtree, retaining hashes/source provenance. Return a version-scoped
   context handle and let the host agent load only matching guidance.

The package/name/version/path examples are illustrative, not claims that these
example packages exist. Gate syntax and output parsing on the installed tool
version. Record the inventory snapshot and reject/retry selection if assets
change between enumeration and reading.

The restored-project route uses existing `project.assets.json` and cached
packages; it does not restore, build, or acquire missing packages. Explicit
package inspection may acquire package data, so nudge must enforce approved
source/network scope. Honor inspector containment/omitted-text diagnostics;
do not treat omitted guidance as successfully read or bypass those safeguards.

Do not parse rendered tables or assume an exit code alone proves a complete
skill read. Keep raw document output separate from JSONL inventory output.
Reading a skill makes its instructions available to the existing agent; the
inspector does not itself perform the migration or guarantee skill activation.

Keep a small direct package-reader fallback for passive hooks, offline local
archives, and bootstrapping where the inspector's .NET 10+ requirement cannot
be satisfied. This avoids making a second SDK/tool installation mandatory for
basic nudges, while reusing the inspector wherever its capabilities fit.
Never run unpinned `dnx` downloads or install the inspector silently from hooks.

### Optional package-skill installation

Evaluate `dotnet-package-skills` separately as a pinned installer integration
with stable-name ownership tracking. [S13] It is not required to list/read/use
packaged skills through the inspector workflow.

Do not assume its current public tool-package availability. The researched
installer can overwrite tracked local edits, has explicit stale cleanup, and
expects one package version per target. Those are integration constraints,
not behavior we should expose without guards.

## 9. SDK, runtime, workload, and platform checks

### SDKs and `global.json`

Inventory the executable path, architecture, installed SDKs, effective SDK
for each execution scope, and the applicable `global.json`.
Keep `sdk.version`, `rollForward`, `allowPrerelease`, `paths`,
`sdk.workloadVersion`, `msbuild-sdks`, and unrelated fields intact.

Fetch the official .NET releases index and applicable channel metadata. [S14]
Use the complete `sdks` collections to find servicing updates in the selected
feature band; the channel's single `latest-sdk` may refer to a different band.

For `x.y.znn`, the stable feature band is the hundreds group of the third numeric
component: `10.0.101` and `10.0.113` belong to `10.0.100`, whereas `10.0.402`
belongs to `10.0.400`. Prerelease ordering and eligibility need proper version
parsing, not string comparison.

If an older feature band no longer receives servicing, say that no same-band
fix is available and propose the supported band as a separate compatibility
trial. Do not imply that every band receives every channel's security release.

Classify separately:

- Same-feature-band servicing.
- Different feature band in the same major/minor.
- Major-version tooling-only trial.
- Major-version target-framework migration.
- Preview/RC trial.
- End-of-support or unsupported operating system.

Check **installed**, **effective**, **pinned**, and **available** separately.
An outdated exact pin is different from a policy that already selects an
installed patched SDK. Missing SDKs are environment findings; a missing pin
must not prevent the self-contained engine from explaining the remedy.

Do not automatically relax roll-forward or enable previews. Patch `global.json`
with comments, formatting, and unrelated settings preserved. Only change it
when that change is actually required for the approved outcome.

Security release metadata establishes that a release includes security fixes;
it does not prove every CVE affects every project/component. Show relevant
component/CVE evidence when available; otherwise label it "security servicing
available", not an invented exploitable-vulnerability diagnosis.

### Runtimes and deployed applications

An SDK update is not the same as a runtime/deployment update.

Inventory shared `Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, and
Windows Desktop runtimes, target frameworks, explicit runtime pins, and obvious
deployment/container declarations. Differentiate:

- Local build SDK remediation.
- Local framework-dependent application runtime remediation.
- Self-contained applications that need republishing with patched runtime assets.
- Container/CI/runtime images that need their own upgrade and redeployment.

The workstation's installed runtimes do not establish what production runs.
Report deployment unknown when not observable. Never claim that updating
`global.json` alone fixes a deployed runtime vulnerability.

### Workload sets and loose manifests

Inventory the effective SDK band, current update mode, workload-set version,
installed workloads/manifests, and required project workloads.
Use the pinned set if present; do not always compare with the globally newest set.

Prefer supported workload version discovery commands after capability probing.
Where unavailable, inspect the matching `Microsoft.NET.Workloads.<band>` package
and version-specific manifest data. Distinguish user-facing set versions from
NuGet package version encoding; preview/RC mappings need fixtures, not a blanket
string substitution.

`dotnet workload update` mutates the environment. Even
`--advertising-manifests-only` writes manifest data. [S15] Neither belongs in an
unqualified read-only hook. SDK commands can also trigger advertising downloads;
use documented controls where applicable and validate side effects.

A set update and a loose-manifest update are different operations. Missing or
unsupported dependency metadata must be marked unknown, not treated as a
compatible complete toolchain.

### MAUI, Xcode, Android, and other external tools

Derive requirements from the **selected** workload set/manifests and available
`WorkloadDependencies.json`, corroborated by version-specific platform release
notes. The official MAUI doctor skill provides useful discovery prior art. [S16]
Do not reuse a helper that always chooses the newest global SDK if the finding
concerns a specific installed/pinned band.

Model a tuple:

```text
SDK + workload set/manifests + MAUI package versions + target frameworks
+ platform API levels + Xcode/JDK/Android/Windows SDK requirements
```

Keep Android minimum supported OS, compile/target API, build tools, and store
requirements distinct. Raising the minimum supported platform is a behavioral
change, not a routine consequence of having a newer Android SDK installed.
MAUI's support lifecycle also differs from the base .NET lifecycle. [S17]

For trials:

- Use `DEVELOPER_DIR` per subprocess to select an already installed compatible
  Xcode; never use a global `xcode-select` switch.
- Install Android tools only into a dedicated approved root, explicitly bind
  build properties/environment to that root, and isolate Java selection.
- Do not accept licenses, download Xcode, or install system tools silently.
- Report unavailable Windows/Apple/device targets as blocked or unvalidated.
- Treat some toolchain requirements as host-level limits; an isolated .NET
  folder cannot provide a compatible macOS/Xcode/Windows installation by itself.

## 10. Chat experience and no-nag memory

### Presentation

The engine decides eligibility and supplies facts. The host agent explains them
and asks one focused question at a natural conversational boundary.

Default ordering:

1. Newly discovered high/critical package advisories and relevant security
   servicing/end-of-support issues.
2. Guidance tied to a just-observed package transition.
3. Routine servicing maintenance.
4. Major-version and preview experiments.

Show one actionable suggestion, not a maintenance checklist at every startup.
Group related updates; the full report remains available through status.
Routine suggestions should not derail an ongoing focused task. Package-transition
guidance can be immediate because it is directly relevant to the current work.

Example question intents:

> A direct dependency has a known advisory, with a candidate patch fix. Review
> the proposed change now, or snooze this advisory?

> The package upgrade completed. Its version-matched guidance describes a
> migration step and an optional feature. Review the migration guidance?

> A new release candidate is available. Try a tooling-only build in an isolated
> worktree, leaving the main workspace unchanged?

Use the host's native question tool where possible. Choice sets can include
review/try, snooze, skip this version, and disable this category. Details such as
downloads, build execution, and a TFM migration require their own explicit scope;
"review" is not consent to execute.

### Durable state

Store personal state outside Git in an OS-appropriate application-data directory.
Allow an explicit shared `NUDGE_STATE_DIR` so Copilot and Claude adapters use the
same decisions. Host plugin-data directories are fallbacks when sandbox grants
do not allow the shared location; `doctor` must disclose that cross-host memory
is then siloed. Do not bypass host filesystem restrictions.

Suggested SQLite entities:

| Entity | Important fields |
| --- | --- |
| Repository/workspace | Canonical identities, admitted roots, registered sessions. |
| Snapshot | Worktree, execution scope, declared/resolved hashes, observations. |
| Finding | Stable subject, target/advisory revision, evidence, assessment, lifecycle. |
| Decision | Scope, decision kind, exact target or category, expiry, timestamp. |
| Delivery | Session, finding revision, lease, delivered/presented/answered status. |
| Metadata cache | Source identity, ETag/expiry, content hash, last successful refresh. |
| Job/trial | State, ownership lease, checkpoint, execution/evidence paths. |
| Installed guidance | Optional owned paths, source identity, original/local hashes. |

A repository policy file, such as `.dotnet-nudge.json`, can define check categories,
channels, allowed sources, and approved scan/build scopes. Personal declines do
not belong in a committed file. Organization/host restrictions cannot be
weakened by a repository policy or package-provided text.

### Decision semantics

Separate these decisions:

| Decision | Meaning |
| --- | --- |
| Snooze | Suppress this subject until a specific time. |
| Skip this version | Suppress this exact candidate; a newer candidate can become eligible. |
| Not now / no answer | Apply a reminder interval without recording acceptance. |
| Disable previews/category | Suppress the category across versions until explicitly enabled. |
| Reviewed/accepted risk | Acknowledge an advisory with an explicit expiry/scope. Not a NuGet audit suppression. |
| Accepted action | Approved a specific maintenance/trial plan; remains open until verified. |

Stable `subjectKey` identifies the concern; candidate/advisory/content revisions
identify materially new evidence. Include repository, SDK scope/band or package
identity, and finding kind. Scope normal package declines repository-wide by
default, with optional project overrides; snapshots stay worktree-specific.

Proposed reminder defaults:

- Routine updates and major/preview suggestions: 30 days for the same evidence.
- A skipped exact version: do not ask again for that version.
- A newer preview/candidate: eligible after a 7-day subject-level question
  cooldown, unless explicitly snoozed or category-disabled.
- Acknowledged unresolved critical findings: 72 hours; high: 7 days;
  lower-severity security findings: 14 days, unless the user chose another period.
- A delivered suggestion without an answer: 7 days for the same subject.
- At most one routine question per session and one per repository per 24 hours.

New advisories, meaningful severity escalation, or a newly available fix can
invalidate a **version-specific** skip and produce one new eligible finding,
subject to throttling. An explicit mute or time-based snooze still wins.
Continue scanning while muted; never erase the underlying finding.

The exact defaults are tunable product policy, not a reason to infer a decline
from any incidental use of the word "no" in a transcript.

### Reliable decision capture and duplicate control

Expose a `nudge_decide` tool/CLI operation and require the maintenance skill to
record a structured decision after the user answers. Do not rely on host chat
transcript scraping as the source of truth.

Acquire an atomic delivery lease for `(repository, subject, evidence revision)`.
Hooks can mark context delivered, but cannot claim a user actually saw a question.
The agent records presentation and the answer separately. Recover abandoned
leases without suppressing the finding forever.

Reject decisions/actions against superseded finding revisions and re-present
the current facts. A new version arriving mid-question must not silently inherit
approval for the old version.

Preferences should be inspectable and reversible: status with suppression
reasons, explicit unmute/reset, export, and targeted state cleanup.

## 11. Isolated upgrade trials

A trial is a staged experiment, not an automatic production upgrade.

### Trial types

| Type | Changes |
| --- | --- |
| SDK-only | Change SDK selection while leaving TFMs and packages unchanged. |
| Security/package patch | Select an explicit minimal package remediation. |
| Package-major | Update an approved package/family and apply required migrations. |
| Framework-major | Change TFMs and aligned packages/toolchain where required. |
| MAUI/toolchain | Trial a validated SDK/workload/package/platform tuple. |

Keep the choice explicit. "Try the new SDK" does not imply raising every TFM.

### Lifecycle

```text
proposed -> approved -> preparing -> baseline -> upgrading -> validating
         -> ready-for-review / blocked / failed / cancelled
         -> adopted / archived
```

Persist checkpoints and artifact paths. Cancellation terminates only the owned
process tree. Retry read-only preparation safely; do not blindly replay edits,
installs, or external actions after an uncertain failure.

### Workflow

1. **Define the approved experiment.** Record exact target versions, source
   commit, project scopes, file changes, allowed commands, downloads, disk/time
   budgets, and validation expectations. Preflight package/toolchain feasibility.
2. **Choose a source snapshot.** Default to the current checkout's committed
   `HEAD`, not an assumed `main`. Show when local changes are excluded. If the
   user needs their current uncommitted state, explicitly snapshot selected
   tracked/untracked files into the trial without committing/stashing the
   original tree or copying ignored secrets.
3. **Create an owned worktree.** Use a unique trial branch and directory under
   the approved trial root. Handle sparse checkout, submodules, Git LFS, symlinks,
   external imports, and worktree-specific configuration as capability checks.
   Missing support is a blocker, not a partial clone represented as equivalent.
4. **Prepare private tool roots.** Install exact baseline/candidate SDKs into
   trial-owned roots with verified downloads. Official `dotnet-install` supports
   non-admin exact-version installs and suits this disposable scenario. [S18]
5. **Bind every command.** Execute the exact private `dotnet` path with an
   explicit working directory and process-local environment, including
   `DOTNET_ROOT`, architecture-appropriate root settings, `PATH`,
   `DOTNET_CLI_HOME`, and explicit NuGet cache locations where required. Do not
   assume environment state persists between agent shell calls.
6. **Prove effective selection.** Record executable identity, `--info`, selected
   SDK, runtime/workload inventories, and configuration. Inspect `global.json`
   `paths` and MSBuild resolver overrides: `PATH` alone is not sufficient.
7. **Establish the baseline.** Build and run approved tests against the source
   snapshot with original tool versions before changing it. Prefer private
   baseline installations; do not mutate the developer's shared SDK. Record
   preexisting failures, test counts, and unavailable targets.
8. **Apply one staged upgrade.** Edit the real declaration owner: central
   packages, props, project, lock file, SDK pin, workloads, CI, or container
   config as appropriate. Load relevant version-matched package/official
   guidance and fix required code changes without unrelated modernization.
9. **Validate incrementally.** Restore, build selected projects/TFMs, run the
   existing test runner, then expand to the affected matrix. SDK-only and
   package-only stages separate root causes before a combined migration.
10. **Review the evidence.** Produce a diff and report comparing baseline and
    upgraded results, changed warnings, package graphs/advisories, actual tool
    identities, and platform/runtime coverage.
11. **Adopt separately.** Let the user choose a patch/cherry-pick or reviewed
    branch workflow. Revalidate against the destination if it advanced.
    Never automatically apply, push, open a PR, or merge.
12. **Clean up deliberately.** Offer removal of clean trial-owned worktrees and
    private environments. Preserve dirty trials and reports; never force-remove
    changed worktrees or delete a shared cache used by another session.

### Isolation details that must be proven

Git worktrees isolate source files, not the whole machine. Builds/tests and
MSBuild targets can execute arbitrary code and write outside the worktree.
Use host/OS sandbox restrictions when available and explicitly disclose limits.
Validate behavior in fixtures that try to write to original/shared paths.

Private SDK roots must include the intended workload packs/manifests and runtime
assets. Workload commands can use shared installer/global state on some
platforms, particularly Windows/Visual Studio installations. Prove private-root
behavior per supported platform; block unsupported combinations or use an
approved container/VM/remote runner instead of claiming complete isolation.

Cache immutable verified downloads where safe. Initially prefer trial-private
package/HTTP caches for predictability; shared read-through caching is an
optimization only after access, locking, provenance, and poisoning tests.
Never share mutable SDK/workload roots between concurrent trials.

Use process-local Xcode/JDK/Android selection. A container is useful for some
server builds but is not a replacement for a macOS Apple-platform toolchain.

Register trial roots with the engine and propagate `NUDGE_TRIAL_ID`. Suppress
ordinary upsell suggestions and recursive trial creation inside trials while
retaining diagnostics relevant to that experiment.

### Evidence and success criteria

Each trial report records:

- Source commit/snapshot hash and destination assumptions.
- Exact SDK/runtime/workload/package/toolchain versions and provenance.
- Commands, working directories, relevant non-secret environment settings,
  exit status, logs, durations, and cancellation/failure state.
- Baseline and upgraded restore/build/test results and actual test counts.
- Diff, required migration steps, remaining warnings/advisories, and gaps.
- Whether the original checkout and shared toolchain state remained unchanged.

Use independent result dimensions: restore, compilation, automated tests,
runtime smoke, and platform matrix. Zero discovered tests means "not exercised",
not test success. A compile-only trial is labeled compile-only; unavailable
Apple/Windows/device validation is never summarized as a complete migration.

## 12. User-facing tools and skills

Keep the public surface small:

| Tool | Purpose |
| --- | --- |
| `nudge_status` | Cached findings, scope/freshness, suppressions, and running jobs. |
| `nudge_scan` | Explicit scoped refresh; declares whether restore/evaluation is allowed. |
| `nudge_context` | Read an exact package/skill context handle with provenance. |
| `nudge_decide` | Record a decision against a current finding revision. |
| `nudge_trial` | Prepare/start/status/cancel/report a specifically approved trial plan. |
| `nudge_preferences` | Inspect/change explicit category, reminder, trust, and source settings. |

Mutation requests must name a workspace, current finding/plan revision, and
allowed action scope. The MCP protocol is not itself a user-consent guarantee;
the host's approval mechanisms and the skill's explicit question remain required.
Passive hooks cannot mint mutation permission.

Skills should explain workflows, not reimplement detectors:

- `nudge-status`: summarize actionable facts with freshness/coverage limits.
- `nudge-maintain`: review a finding, ask for the next bounded action, record the
  answer, and use trusted matching package/official guidance.
- `nudge-trial`: prepare and validate an approved isolated experiment.
- `nudge-preferences`: change snooze/category/trust policy explicitly.

Use official `dotnet/skills` migration and toolchain guidance where applicable,
with inspected version/source provenance. [S19] Do not install other plugins
automatically or obey a reused skill's more permissive installation/commit
instructions when they conflict with nudge's approval/isolation contract.

## 13. Implementation sequence and acceptance gates

Deliver vertical slices rather than implementing every detector before users
can see any useful behavior.

| Phase | Deliverable | Acceptance gate |
| --- | --- | --- |
| 0: Contracts and distribution | Shared catalog, dual host packaging spike, launcher, hook fixtures, MCP activation/state-path experiments, pinned inspector adapter spike. | Both hosts load the plugin without duplicate hooks/servers; exact context output works; inspector skill inventory/document reads work against an actual package archive; resume/headless behavior and missing-runtime fallback are documented. |
| 1: Read-only maintenance MVP | Inventory, source-safe NuGet/security lookup, SDK same-band and local-runtime servicing, state/cooldowns, status/decision skill. | Security, SDK, and runtime findings can be surfaced, snoozed, and suppressed across sessions/worktrees without confusing SDK selection with deployment remediation; no restore or tracked-file changes occur automatically. |
| 2: Package-transition guidance | Settled graph deltas, `dotnet-inspect` skill inventory/reading, text-only package skills/README discovery, provenance/trust, on-demand context. | IDE/manual/agent updates are all detected; initial baseline is quiet; actual Controls-style package skills and references are read and delivered without requiring repository installation; exact old/new guidance is scoped correctly; unsafe archives and multi-version ambiguities are handled. |
| 3: Basic trials | Worktree lifecycle, exact private SDK selection, package patch/SDK-only stages, baseline/build/test reports. | A real fixture upgrade succeeds with evidence; a failing upgrade is reported honestly; original checkout/shared SDK state is unchanged; cancellation/resume/cleanup are safe. |
| 4: Major/platform trials | Framework/package families, workload sets, MAUI dependency tuples, Xcode/Android selection, CI/container recommendations. | Supported OS/architecture combinations pass isolation tests; incompatible toolchains block before mutation; unavailable targets are explicitly unvalidated. |
| 5: Additional hosts/automation | Version-gated IDE/Codex adapters, optional background notifications, headless reports and scheduled checks. | Each host has its own documented capability/permission matrix; no claim of portable hook behavior or unattended model prompting. |

Phases 1-3 are the first useful product; do not hold basic security nudges for
complete MAUI/Xcode automation. Treat phase 4 as a distinct reliability effort.
Avoid schedule promises until phase 0 resolves packaging and private-workload
installation uncertainties.

### Concrete first development backlog

1. Build and test one hook normalizer/renderer for each primary host.
2. Validate catalog and installed plugin containment on both hosts.
3. Create the executable, local data layout, explicit setup, and `doctor`.
4. Implement workspace/project/global.json discovery with passive coverage labels.
5. Read restored NuGet graphs and preserve direct/transitive/TFM scopes.
6. Add mapped-source metadata, cached vulnerability lookup, and minimal candidates.
7. Add releases metadata with same-feature-band SDK comparisons.
8. Implement state transactions, delivery leases, revisioned decisions, and cooldowns.
9. Connect local MCP/status/maintenance skills to hook delivery.
10. Wire pinned inspector inventory/selected-document reads and test them with
    actual Controls-style NuGet archives and externally triggered upgrade fixtures.
11. Add trial plan approval, private environments, baseline/upgrade comparison.
12. Expand to workload/platform tuples only after private-root isolation is proved.

## 14. Validation and operational quality

### Deterministic tests

- NuGet normalized IDs, version ranges, prereleases, vulnerable ranges, and
  minimal fixes with/without a compatible candidate.
- Central packages, conditional imports, floating versions, multi-targeting,
  multiple resolved versions, and restored graph schema changes.
- Source mapping, audit-source separation, private feed authentication,
  unavailable/malformed feeds, and partial coverage.
- All `global.json` roll-forward policies, missing pins, ancestor scopes,
  preview eligibility, architecture, `paths`, and multiple SDK feature bands.
- Workload-set user/NuGet version mapping and external requirement fixtures.
- Package guidance archives, frontmatter/references, text limits, collisions,
  malicious instructions, traversal, removed/modified/local-edited skills.
- Fake-clock snooze/skip/mute rules, new versions/advisories, expired leases,
  multi-session races, state migrations, and corrupted state.
- Exact host payload/output fixtures, stdout cleanliness, deadlines, and recursion.

### Integration fixtures

Use small real solutions: a library/test pair, ASP.NET application, CPM/multi-TFM
solution, nested SDK scopes, private mapped-feed fixture, and a MAUI sample.

Exercise:

- A no-change session performs no unnecessary feed calls or build work.
- An advisory refresh finds a vulnerability without package edits.
- A manual/IDE-like package edit then restore triggers one resolved delta.
- The package bridge/candidate guidance is discoverable before upgrading.
- Inspector discovery, selected skill text, and linked references from an
  actual Controls-style archive reach the agent without copying/installing
  skills into the repository; unavailable/omitted text remains a coverage gap.
- Declining one preview suppresses it, while a newer preview becomes eligible
  after cooldown; a category mute suppresses both.
- Concurrent Copilot/Claude/worktree sessions do not duplicate a question.
- An interrupted worker recovers a read-only job without replaying mutation.
- Original source files, Git status, SDK/workload roots, and global Xcode
  selection remain unchanged through successful and failed trials.

### Host and trial end-to-end evidence

Schema validation is necessary but does not prove chat delivery or skill use.
Run real, isolated host sessions to verify that context reaches the model,
the suggestion is presented, the answer is recorded, and the selected guidance
is actually loaded. Separate host infrastructure failures from engine failures.

Measure no-op hook p50/p95, requests and bytes per source, scan duration by
project/package count, context bytes, suggestion repetition, and coverage.
Use local diagnostics by default; telemetry is off unless explicitly enabled,
and must never include code, credentials, private package identifiers, or raw
chat transcripts.

Performance experiments should use fixed fixtures and repeated runs. Do not
claim token/time savings because one small pilot happened to be faster.

### Error handling and support

Reserve stdout for hook/MCP contracts; diagnostics go to stderr/local logs.
Surface degraded checks once per changed failure state, with status/details
available, instead of repeatedly interrupting the chat.

Support offline/cache-only mode. Last successful results remain visible with
their age; failed authentication, unavailable SDKs, unsupported graph formats,
and network policy restrictions are actionable coverage gaps.

Log source and command failures with secrets/redirection tokens redacted.
SQLite corruption must be reported and recoverable without silently deleting
the user's decisions. Version state and public contracts; test migrations.

## 15. Deliberately deferred areas

Useful later extensions include local tool updates, deprecated/EOL packages,
CI SDK/container drift, lock-file/audit configuration hygiene, and deployment
runtime inventory. Add them behind the same finding/policy/consent model.

Do not initially build:

- A hosted service, account system, or centralized user-memory backend.
- An autonomous agent that buys/downloads/selects every platform tool.
- A universal package instruction execution mechanism.
- Always-on daemons or per-keystroke full scans.
- Automatic production/main-branch upgrades.
- A claim that successful compilation proves runtime compatibility.

## 16. Research sources

Sources were inspected for this plan. Prototype/open-PR status and mutable
documentation are called out explicitly; future implementation must revalidate
capabilities against supported versions.

- **S1:** [Copilot CLI marketplace creation](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/plugins-marketplace).
- **S2:** [Copilot CLI plugin reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-plugin-reference).
- **S3:** [Agent Plugins 1.0 specification](https://agent-plugins.org/specification).
- **S4:** [Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference), including session, prompt-submit, post-tool, notification, and stop contracts.
- **S5:** [Claude Code hooks reference](https://code.claude.com/docs/en/hooks), [plugin manifest reference](https://code.claude.com/docs/en/plugins-reference), and [marketplace creation](https://code.claude.com/docs/en/plugin-marketplaces).
- **S6:** [dotnet package list](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list).
- **S7:** [NuGet Package Source Mapping, including metadata-query limitations](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping).
- **S8:** Open prototype [dotnet/maui#39272](https://github.com/dotnet/maui/pull/39272), with [package-skill documentation pinned to the inspected commit](https://github.com/dotnet/maui/blob/2e8645adb0244a72680228db0b00a313fb794287/docs/NuGetPackageSkills.md). This is not a claim that these skills are published in released packages.
- **S9:** [global.json overview](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).
- **S10:** [.NET workload sets](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-sets) and [workload search](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-search).
- **S11:** [NuGet VulnerabilityInfo API](https://learn.microsoft.com/en-us/nuget/api/vulnerability-info) and [NuGet package auditing](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).
- **S12:** [dotnet-inspect](https://github.com/richlander/dotnet-inspect) and its [current CLI reference](https://github.com/richlander/dotnet-inspect/blob/main/docs/cli-reference.md), including project skill inventory, JSONL, selected-document reading, and package-file routes; version-specific tested examples are also documented in S8.
- **S13:** [dotnet-package-skills README at the inspected prototype commit](https://github.com/kartheekp-ms/dotnet-package-skills/blob/59d3bc0d4fc80188d33bcc257d2a83c99a4cbfa3/README.md). Public source documentation is not evidence that a tool package can currently be installed.
- **S14:** [Official .NET release metadata overview](https://github.com/dotnet/core/blob/main/release-notes/README.md), [schemas](https://github.com/dotnet/core/blob/main/release-notes/schemas/README.md), [release index](https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json), and [GitHub mirror inspected during research](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json).
- **S15:** [dotnet workload update](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-update).
- **S16:** [Official MAUI doctor skill](https://github.com/dotnet/skills/blob/main/plugins/dotnet-maui/skills/dotnet-maui-doctor/SKILL.md) and [workload dependency discovery reference](https://github.com/dotnet/skills/blob/main/plugins/dotnet-maui/skills/dotnet-maui-doctor/references/workload-dependencies-discovery.md). Reuse patterns, not every permission/default in a skill.
- **S17:** [MAUI .NET 10 overview and support-policy distinction](https://learn.microsoft.com/en-us/dotnet/maui/whats-new/dotnet-10).
- **S18:** [Official dotnet-install scripts](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script).
- **S19:** [Official dotnet/skills marketplace](https://github.com/dotnet/skills), including upgrade, NuGet, and MAUI plugins.
- **S20:** [NuGet MCP server](https://learn.microsoft.com/en-us/nuget/concepts/nuget-mcp-server). It offers existing package remediation capabilities, but is not by itself the session observation, reminder, or trial-isolation system described here.

**Bottom line:** ship useful read-only security/servicing nudges first, make
package-transition context a first-class feature, and then add evidenced trials.
Keep detection deterministic, host behavior explicit, memory local, and every
mutation bounded by the user's actual approval.

# Plugin development

This document covers marketplace packaging, hook contracts, and local
validation, automatic runtime bootstrap and release packaging. Publishing code
does not imply that an engine release has already been published.

## Layout and host loading

The marketplace is `.claude-plugin/marketplace.json` and its plugin source is
`plugins/dotnet-nudge/`. That plugin has one Agent Plugins 1.0 root
`plugin.json` for portable skills, plus host-native manifests/configuration:

| Host | Manifest | Hooks |
| --- | --- | --- |
| Copilot CLI | `plugin.json` | `com.github.copilot/hooks/hooks.json` |
| Claude Code | `.claude-plugin/plugin.json` | `hooks/hooks.json` |

Both hosts load the same four immediate child skill directories under `skills/`.
The optional [.NET-branded maintenance canvas](dashboard.md) is a separate
Copilot extension. Its distributable source lives in `canvas/`; the repository's
`.github/extensions/nudge-dashboard/` entry point loads it for local development.
Marketplace installation does not itself register a canvas. Use the documented
explicit-scope `scripts/install-canvas.cjs` installer only after approval, and
reload/restart a canvas-capable host. Claude and other hosts retain native
questions/reports.
Hooks are configured once per host; there is no portable hook schema. Copilot
uses the Agent Plugins 1.0 `mcp.json`, and Claude Code uses native `.mcp.json`.
They declare the same server name, not two simultaneous server instances.
Do not add both a native and portable hook entry for the same event.

The marketplace uses the common name `dotnet-nudge-marketplace`, and the plugin
is named `dotnet-nudge` in every manifest. The relative marketplace `source`
is resolved from the repository root and must stay within it.

## Hook launch contract

Each host maps four lifecycle events to exactly one launcher call:

| Host event | Engine event |
| --- | --- |
| Copilot `sessionStart` / Claude `SessionStart` | `session-start` |
| Copilot `userPromptSubmitted` / Claude `UserPromptSubmit` | `prompt-submit` |
| Copilot `postToolUse` / Claude `PostToolUse` | `post-tool-use` |
| Copilot `sessionEnd` / Claude `SessionEnd` | `session-end` |

The wrapper invokes only:

```text
dotnet-nudge hook --host copilot|claude --event session-start|prompt-submit|post-tool-use|session-end
```

Hook JSON is passed through stdin. With an engine installed, each wrapper
forwards stdin, stdout, stderr, and the engine's exit status without parsing or
rewriting the payload. The engine is responsible for its host-specific output:
Copilot uses top-level `additionalContext` for supported events, while Claude
uses `hookSpecificOutput`. Copilot drops command-hook output from
`userPromptSubmitted`, so that event must not be relied on for context
injection. Neither adapter invokes a model, starts restore/build, installs
tools, or blocks turn completion.

### Diagnosing a quiet session

Treat discovery, command execution, scanning, context delivery and the agent's
response as separate gates. A tool catalog entry proves only tool discovery.
Likewise, a successful `hook.start` / `hook.end` pair can describe the SDK
callback while no plugin command ran. An empty `{}` is also a valid Nudge
response when there is no current, eligible finding.

| Event | Nudge behavior | Expected visible result |
| --- | --- | --- |
| `sessionStart` | Register the exact workspace/session, queue a read-only scan, inspect cached findings. | Context only if an eligible snapshot already exists. A fresh workspace normally returns `{}`. |
| `userPromptSubmitted` | Renew registration and queue a due scan. | No Copilot context is emitted, for compatibility with runtimes that discard this event's output. |
| `postToolUse` | Renew registration and inspect current findings. | Deliver newly eligible findings once the background scan is complete; respect cooldowns and decisions. |
| `sessionEnd` | Release undelivered leases and remove this session's active registration. | `{}`; no maintenance question. |

The command hook does not perform the scan. The MCP server must be running in
the **same state directory**; its worker checks the durable queue every five
seconds and allows up to 90 seconds per scan. A short first turn can finish
before a snapshot exists. Completing a scan does not itself wake an idle agent:
delivery needs a later supported lifecycle event. No running worker, a failed
scan, stale evidence, or a cooldown must not be mistaken for a clean assessment.
Check launcher stderr and the workspace's registration, scan-job, snapshot and
delivery records before forcing a scan.

#### Standalone runtime versus CLI wrapper

The October 2026 investigation reproduced a host-integration difference using
only a local deterministic model endpoint and isolated configuration:

* CLI `1.0.100-1` loaded installed command hooks, including with SDK callbacks
  registered and repository file hooks disabled. Installing into a runtime that
  was already started could leave its first new session without plugin command
  hooks; a later session loaded them.
* The app's raw `copilot-runtime` `1.0.99-2` reproduced the quiet-session symptom
  even in a cold process: SDK callbacks fired, but installed plugin commands did
  not. Discovery still advertised the hooks as enabled.
* On that raw runtime, explicitly handing off the session's installed plugin
  catalog **and then reloading its plugin hooks** restored all four commands.
  Updating the catalog alone or reloading alone did not. The real Nudge engine
  then registered the workspace, its MCP worker scanned it, and `postToolUse`
  delivered the finding into the next model request.

For SDK hosts with this integration gap, use the supported host APIs before the
first prompt, preserving the complete effective plugin catalog and enabled
states:

```js
await session.rpc.options.update({ installedPlugins: effectiveInstalledPlugins });
await client.rpc.sessions.reloadPluginHooks({
  sessionId: session.sessionId,
  deferRepoHooks: !repositoryHooksTrusted
});
```

This is host wiring, not something the plugin can repair by editing a hook
manifest or repeatedly opening a canvas. Do not change repository trust or
disable policy checks to compensate. Repository-origin hook admission is
distinct from installed-plugin hook loading; `repository_hooks_trusted=false`
alone is not evidence that an installed plugin should be suppressed. Keep
runtime versions in reproduction notes rather than claiming all SDK hosts
behave identically.

The offline .NET integration test
`HookQueuedScanRunsInMcpWorkerWithoutCallingAMaintenanceTool` covers the real
engine/worker path without calling a maintenance MCP tool. An optional native
runtime contract exercises actual command hooks, SDK callbacks and model-facing
context together, without external AI requests or changing installed plugins:

```sh
NUDGE_COPILOT_SDK_ROOT="/absolute/copilot-sdk" \
NUDGE_COPILOT_RUNTIME="/absolute/copilot-runtime-or-cli" \
node --test tests/plugin-contracts/copilot-hooks.test.mjs
```

It uses an isolated Copilot home and cleans up its sessions and temporary files.
Without those explicit paths the native-runtime test is skipped.

Finding context explicitly directs the host agent to its native question tool,
`ask_user` for Copilot or `AskUserQuestion` for Claude Code. Shared skills and
MCP tool descriptions reinforce that guidance; hooks themselves cannot render
the picker. See [native maintenance questions](integration.md#native-maintenance-questions)
for noninteractive behavior, approval boundaries and validation limits.

The Bash and PowerShell wrappers call the same Node.js launcher as MCP and CLI
commands, `scripts/run.cjs`. Node.js 22 or newer must be on the host's `PATH`.
The launcher resolves `NUDGE_ENGINE_PATH`, plugin `bin/`, the selected state
directory's engine, the shared platform user-data engine, host plugin-data
engines, then `dotnet-nudge` on `PATH`, in that order. An invalid explicit
override fails rather than silently selecting another executable. Versioned
install pointers must select the current RID; links and traversal are rejected.
Paths with spaces remain single arguments, and engine streams are inherited.

If no compatible managed engine is found, the hook wrapper emits one bounded, host-correct setup
diagnostic and records its once-only marker in the writable state directory,
not the plugin cache. `NUDGE_STATE_DIR` overrides the platform data location
and is shared by the two adapters when set. If state storage is unavailable or
invalid, the wrapper emits an explicit error to stderr and omits the chat
diagnostic rather than writing into the plugin cache.

## Automatic runtime setup

The checked-in `runtime-manifest.json` describes a release channel, not
fabricated artifact digests. It also declares the plugin's exact stable
`minimumVersion`. On MCP/CLI use without a compatible managed engine,
the launcher reads this repository's latest stable release manifest. A bundled
published manifest can instead pin an exact release. Only exact version/RID
asset URLs under `Redth/dotnet-nudge` are accepted. The downloaded gzip asset
and decompressed executable must both match the manifest's SHA-256 digests
before any executable is selected or run. Downloads and expanded payloads are
bounded; failures leave no selected partial engine and go to stderr only.
SHA-256 verifies content against the repository-hosted manifest; it is not a
separate publisher signature.

The engine is a self-contained single-file publish, including native SQLite
libraries. Installation uses a staged version/RID directory and an atomic
`engine/current` selection in shared platform user data, or `NUDGE_STATE_DIR`
when specified. Simultaneous MCP starts coordinate through a setup lock.
Normal starts reuse an engine at or above `minimumVersion` without checking
for updates or needing network access. Updating the plugin can raise that
minimum; the next MCP/CLI start upgrades an older managed installation under
the same setup lock. A release below the required version fails explicitly,
and an unsuccessful update leaves the previous selection intact without
running that outdated engine. Old version directories are retained, and
newer managed engines are never downgraded. Explicit `NUDGE_ENGINE_PATH`,
bundled and `PATH` engines are developer/user selections and are not replaced.
Restart an already-running MCP connection to load the new engine.
A crashed setup's remaining lock produces a bounded error;
remove only that named lock after confirming no setup process is running.
Set `NUDGE_AUTO_SETUP=0` to disable automatic downloads. Hooks never initiate
setup; they do not run an outdated managed engine, and their setup/update
notice remains bounded and once-only.

If no public engine release is available, automatic setup fails explicitly;
do not claim that the workspace was assessed.
For development, supply a built engine through `NUDGE_ENGINE_PATH`. That
override works identically for hooks, MCP and the CLI launcher, without changing
`PATH`. For example:

```sh
dotnet publish src/DotnetNudge/DotnetNudge.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o /your/engine/directory
export NUDGE_ENGINE_PATH=/your/engine/directory/dotnet-nudge
node plugins/dotnet-nudge/scripts/run.cjs version
```

Use your actual RID and path; on Windows PowerShell, set
`$env:NUDGE_ENGINE_PATH` to the full `.exe` path. Both MCP configurations invoke
`node` with their host's plugin-root placeholder. The MCP config supplies
`NUDGE_PLUGIN_DATA` from its
host's writable plugin data directory; the hook wrapper uses the matching
host plugin-data path. To override both, set `NUDGE_STATE_DIR` in the host
environment before starting it. Never put state in the read-only plugin
cache. The MCP process reads the workspace only from client roots or an
explicit `nudge_register(workspace)` call, never from its own cwd.

For an explicitly supplied local archive, the optional Python 3.10+ setup
helper still accepts a ZIP or tar archive containing one
RID-appropriate self-contained publish tree with `dotnet-nudge` (or
`dotnet-nudge.exe` on Windows) at the archive root:

On macOS, create tar archives with `COPYFILE_DISABLE=1 tar ...` (or another
metadata-free archiver). The default tar behavior may add AppleDouble `._*`
members, which the installer's strict archive validation correctly rejects.

```sh
python3 plugins/dotnet-nudge/scripts/setup-runtime.py \
  --archive /path/to/dotnet-nudge-<version>-<rid>.zip \
  --version <exact-published-version> \
  --rid <exact-published-rid> \
  --sha256 <exact-published-64-character-digest>
```

The helper performs no network requests and does not run the executable. It
checks the exact archive digest, current-machine RID, every member's safe
relative path and regular-file/directory type, executable identity, and
resolved install containment. All published files and relative subdirectories
are retained (including native SQLite dependencies); links and special files
are rejected. The versioned payload is installed under the platform user-data directory or the
explicit `NUDGE_STATE_DIR`; use `--state-dir` for a one-off explicit location
and `--replace` only when intentionally replacing an installed engine. Only
the six declared `linux|osx|win` x64/arm64 RIDs are accepted. The Node launcher
discovers this installer selection too, so local archive setup needs no `PATH`
change.

## Publishing engines

For plugin-only changes, update both plugin manifests and the marketplace
version, publish the commit to the marketplace's default branch, and use a
`plugin-v<version>` tag for a source-only plugin release. Create that release
with `--latest=false`: the repository's latest release must remain an engine
release with `runtime-manifest.json`, because automatic engine setup uses its
latest-release download URL. Plugin-only releases do not need a new engine
build; set `minimumVersion` to the published engine required by the plugin.

`.github/workflows/runtime-release.yml` builds native single-file engines on
all six advertised RIDs, checks native SQLite and MCP startup, and packages
each executable as a deterministic gzip asset. `scripts/release-runtime.py`
derives archive and executable hashes from the actual published bytes and
refuses a publish directory containing unbundled dependencies. The release
job requires all six assets and revalidates their digests before generating
the published `runtime-manifest.json` and creating the GitHub release.

Publishing a `v<version>` tag triggers release creation; pull requests only
exercise builds and packaging. Version reporting comes from the engine's
assembly metadata, set from the tag, rather than a hardcoded CLI string.
Creating or pushing a version tag is a separate, explicitly authorized action,
not something the plugin or tests perform.

## Local engine commands

The foundation executable project is `src/DotnetNudge/DotnetNudge.csproj`.
These development commands apply when that source is available in the checkout;
they are not a plugin-install-time build step:

```sh
dotnet build src/DotnetNudge/DotnetNudge.csproj
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- version
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- scan --workspace "$PWD" --json
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- status --workspace "$PWD" --json
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- doctor --workspace "$PWD" --json
```

The executable CLI contract is:

```text
version
scan --workspace PATH [--state-dir PATH] --json
status --workspace PATH [--state-dir PATH] --json
toolchains --workspace PATH [--state-dir PATH] --json
package-report --workspace PATH [--state-dir PATH] --json
trial root|propose|approve|execute|status|cancel|cleanup --workspace PATH [--plan ABSOLUTE_FILE] [--id ID] [--revision N] [--state-dir PATH] --json
doctor [--workspace PATH] [--state-dir PATH] --json
decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until RFC3339] [--state-dir PATH] --json
```

`scan` and `status` are read-only assessments. `decision` persists an explicit
preference and must not run until the user approves its exact workspace,
finding ID, revision, action, and optional expiry. `mcp [--state-dir PATH]`
serves the local stdio endpoint; see [integration](integration.md). Trials
require an exact approved plan confirmed by a human in a local interactive CLI
(`nudge_trial` cannot approve), and trusted project code, not an OS sandbox;
see [trial limits](upgrade-trials.md). None of these
commands are executed by the hook launchers.

## Offline validation

The plugin-contract suite uses only Python's standard library and fixture
engines; it does not depend on files from the engine implementation:

```sh
python -m unittest discover -s tests/plugin-contracts -v
```

CI also runs the offline Node bootstrap suite:

```sh
node --test tests/plugin-contracts/runtime.test.cjs
```

CI runs these suites on Linux, macOS, and Windows. They validate manifests,
host-event mappings, safe relative paths, launcher argument/stream behavior,
once-only setup notices, automatic download verification, concurrent first use,
offline reuse, failed-install cleanup, recovery, and unsafe installation
rejection. Set `NUDGE_INTEGRATION_ENGINE_PATH` to a built self-contained engine
to additionally initialize a real MCP process through the launcher.

When host CLIs are available, validate in isolation:

```sh
claude plugin validate ./plugins/dotnet-nudge --strict
claude plugin validate . --strict
```

Claude's validator only reads the local package and does not install it. For
Copilot CLI, isolate any marketplace registration and installation from the
user's global setup:

```sh
export COPILOT_HOME="$(mktemp -d)"
copilot plugin marketplace add .
copilot plugin marketplace browse dotnet-nudge-marketplace
copilot plugin install dotnet-nudge@dotnet-nudge-marketplace
copilot plugin list --json
```

On PowerShell, use `$env:COPILOT_HOME = Join-Path $env:TEMP
"dotnet-nudge-copilot-test"` (create a fresh empty directory first). Do not
copy the temporary configuration into the global plugin setup. CLI load
validation still does not establish real chat/context delivery; test that only
in a deliberately isolated host session when the engine and release contract
are available.

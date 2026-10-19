# Maintenance dashboard

Nudge's first dashboard adapter targets Copilot's experimental extension canvas
API. Its tab/document title is **dotNudge**. It keeps .NET purple (`#512bd4`)
for primary actions and selection accents, with Copilot's documented semantic
surface, text, font, focus and type-scale tokens. The table, filters, details and selection bar share the
same visual language across maintenance areas.

Native questions remain primary. NuGet planning offers all packages,
vulnerable packages only, a report to choose from, and no change. A separate
presentation choice offers the dashboard only if the actual `nudge-dashboard`
canvas and open/action tools are available. No browser-only imitation,
terminal picker, Codex canvas or Claude canvas adapter is substituted.

## Default view and progressive disclosure

The default view answers three questions: which package, which version change,
and what needs attention? Rows show a compact, unambiguous project/framework
label and critical signals rather than every path, source and diagnostic.
There is no separate Status column. The custom target picker highlights changed
version components: major changes have the strongest .NET accent, minor changes
use neutral emphasis, and patch changes use a quieter weight. Prerelease and
build metadata remain intact. Options retain exact version/source bindings;
duplicate versions from different sources display their source.
The picker supports arrow keys, Home/End, Enter/Space to commit and Escape to
cancel. Tab or clicking outside dismisses it without committing a new target.
Clicking a package expands its review inline in the table. Critical advisories, major updates,
deprecations, missing targets and incomplete checks remain visible; details
are not replaced with an optimistic success state.

One compact chrome row contains category tabs and the Refresh icon action.
The content does not repeat the host's icon, title or workspace subheading.
Tabs use neutral selected pills; toolbar actions are flat, with 28px desktop
control height, 16px icons and visible keyboard focus. Coarse-pointer targets
expand to 44px. .NET branding does not turn every control purple.
Search and the package table start directly below the category tabs, without
a repeated heading or summary block. Grouping, filtering, sorting, counts and
report coverage live under **View options**; an inline indicator on that button
flags incomplete checks without adding a separate row. Bulk presets live under
**Select**. Table checkboxes support
select-visible and mixed selections without opening either panel. Full project
paths, central/transitive impact, dependency paths, source names, release notes,
publisher links and diagnostics are revealed intentionally in the review
row's native disclosure controls. Those work with keyboard and touch, not
hover alone. Mobile rows keep the version change and critical signals visible
without requiring horizontal scrolling.

Selection status and one planning action sit below the search row, with a
single approval reminder. Tabs, search/view controls and the action row stay
at the top while the main content scrolls underneath. The NuGet table header
also stays pinned inside that scroll region, including grouped views. Other categories keep
their agent-review action at the top too. Expanded options have their own
bounded scroll area so they do not consume the entire panel.
Hidden selections are still called out. Empty activity is
omitted; request history is collapsed, current progress stays visible, and a
failure expands its explanation. No selection, evidence, grouping or approval
functionality was removed to achieve the quieter default.

## Installation and discovery

This repository declares a project provider at
`.github/extensions/nudge-dashboard/extension.mjs`, importing the distributable
provider in `plugins/dotnet-nudge/canvas/`. Reload extensions or restart a
canvas-capable Copilot host after adding it. The SDK is supplied by the host;
do not install `@github/copilot-sdk` into this repository.

Marketplace plugin installation alone does **not** register an extension
canvas. After the user approves the installation and exact scope, run either:

```sh
node "<plugin-root>/scripts/install-canvas.cjs" --user
node "<plugin-root>/scripts/install-canvas.cjs" --project "/absolute/project/root"
node "<plugin-root>/scripts/install-canvas.cjs" --session "/absolute/active/sdk/session/workspace"
```

Choose one scope. User scope uses `$COPILOT_HOME/extensions/` (default
`~/.copilot/extensions/`); project scope uses `.github/extensions/` in the named
project; session scope uses `extensions/` under the **actual SDK session
workspace**. A project's app session alias is not necessarily its active CLI
session ID; use the active SDK workspace rather than constructing a path from
the alias. The installer copies the self-contained provider and assets, refuses
existing destinations, and performs no network access or engine execution.
Updates require explicitly reviewing/replacing the existing files, not a silent
overwrite. A project extension shadows a same-named user extension.

The engine must advertise `nudge_package_report` / `package-report` in its
actual tools/help. For source development, build the engine and use
`NUDGE_ENGINE_PATH`; a previously published engine does not acquire new commands
just because this source exists. A future release must package the updated
engine before this feature can be used through the release bootstrap.

### Isolated local acceptance

Build a development engine outside the plugin cache and stage a distinct
native Copilot plugin, without installing or replacing the production plugin:

```sh
dotnet publish src/DotnetNudge/DotnetNudge.csproj -o "/absolute/session/files/dev-engine"
node plugins/dotnet-nudge/scripts/stage-dev-plugin.cjs \
  --output "/absolute/session/files/dotnet-nudge-dev" \
  --engine "/absolute/session/files/dev-engine/dotnet-nudge"
copilot --plugin-dir "/absolute/session/files/dotnet-nudge-dev" -C "/absolute/test/workspace"
```

The stage command refuses existing output. The dev copy uses the supported
native manifest's `extensions` component path, an explicit
`extensions/nudge-dashboard/extension.mjs` entry point, and an isolated
`dotnet-nudge-dev` MCP server. Its engine override is exact, state is local to
the dev directory, and automatic runtime downloads are disabled. The staged launcher
binds this environment for hooks as well as MCP, and the legacy-native manifest
explicitly selects the Copilot hook configuration instead of the Claude file.
No global
plugin registration or application dependency edit is needed. This native
dev manifest is intentionally separate from the portable marketplace manifest;
it does not claim portable Agent Plugins defines extension canvases.

For app acceptance, an extension entry point may instead be placed in the
test session's own `extensions/nudge-dashboard-dev/` directory before that
session starts. It imports the staged native provider, limiting its lifetime
and visibility to that test session. Keep real workspace reports distinct
from illustrative fixtures and preserve source-control status while testing.
If a provider is added to an already running session, use a supported extension
reload or restart only that test session. Creating a wrapper does not prove
that the active runtime has discovered it: verify the exact canvas catalog and
native open/action calls. Repository extension execution must also be admitted
by the host. A launch with `request_extensions=false` does not bootstrap the
provider; no amount of reopening its canvas can register it. Request the host's
supported user approval rather than bypassing trust settings or repeatedly
creating sessions. Updated static page assets do not prove that a running
provider's declaration and actions have reloaded.

`plugin list` recognizes a `--plugin-dir` mount without starting a conversation.
Plugin-owned MCP startup happens with the actual session; an earlier `mcp list`
can omit it. Likewise, a normal noninteractive terminal session need not expose
canvas tools at all. Neither result substitutes for testing canvas discovery,
open, action routing and `session.send` inside a canvas-capable app host.

## Data and approval contract

For a current dashboard, the agent runs the read-only `nudge_scan`, then reads
a real package report and `nudge_status`, and opens the canvas with
`{workspace, report, status}`. An explicitly cached-only request stays cached;
cached status alone does not perform an SDK assessment. It passes objects rather
than serialized JSON strings. For large reports, it can save the actual tool
output under the SDK session workspace's `files/` directory and open with
`{workspace, reportPath: "files/packages.json", statusPath: "files/status.json"}`.
Paths are session-relative, bounded to 8 MiB per JSON file, and resolved inside
that session's `files/` directory (including symlink containment checks).
Exactly one report object or report path is required; `set_report` accepts
the same forms. This avoids retyping a package graph into canvas tool arguments.
Reports are exact-workspace, project, framework
and runtime scoped. The explicit report checks up to 64 projects and 512
package scopes within 90 seconds, with at most 10,000 displayed restored scopes,
distinct from the small passive reminder
budget. Direct scopes are checked first within each project. Newest targets
per patch/minor/major kind and source, plus minimal known fixes, are retained.
Descriptions are bounded to 2,000 characters and are not release notes.
Omitted/missing graphs, unmapped feeds, failures and capped scopes remain
visible as incomplete coverage. No report runs restore or downloads a package.

Unmapped metadata retains its separate exact-scope/source approval. The
dashboard never acquires arbitrary publisher pages by itself: the agent
investigates verified release/repository information on a specific request.
Details bind to evidence revision **and** target version/source. URLs and text
are untrusted data. Only HTTP(S) links without embedded credentials can open.

Each checkbox represents a project/TFM/RID scope, not just a package name.
Workspace, project and package grouping never merges version/source bindings.
“Solution-wide (workspace)” means the admitted workspace, not inferred
membership of a selected solution file. Filters do not drop hidden selections;
their count remains explicit. Unchecking persists a deferred choice for this
conversation, not a snooze or category preference.

“Plan selected updates” submits exactly the selected current/target/source
bindings to the current agent session. It grants no file edit, download,
install, command, trial, push or PR permission. Transitive remediation requires
a direct-parent plan. Central versions may affect unchecked projects; the agent
must disclose those and obtain approval before broadening the scope.
SDK/workload/runtime/host-tool review requests preserve their own categories and
approval boundaries. Workload evidence is still supplied offline inventory,
not live discovery or an implemented workload installer.

### SDK information and advice

The SDK tab uses compact full-width selection rows and expandable advice, not
verbose cards. It shows the requested `global.json` SDK separately from the effective
SDK observed at the workspace root. Installed SDKs, resolution in other
execution scopes, selection policy and coverage are revealed on demand.
Root resolution is always included within the eight-scope probe budget.
Scan and cached-status reports persist this overview even when no update
finding exists; old snapshots without it explicitly request a fresh check.

SDK servicing recommendations and separate major/preview SDK-only trial
suggestions both appear in the tab, without conflating trial advice with a
production upgrade. Missing local SDKs, failed resolution and unavailable or
stale release metadata remain visible, not an up-to-date result. The agent
check button is usable with no findings. Its request performs a read-only scan
and refreshes the status snapshot, rather than rereading the same empty cache.
NuGet selection actions are hidden while reviewing SDKs or other categories.
Status input must belong to the same workspace as the package report.
Workload, runtime and host-tool tabs use the same expandable evidence rows and
honest empty states, with a check action available even without findings.
Category-only refreshes can retain the existing package report and selections.
Local runtime and deployment advice remain distinct.

### Skills and tools

The [helper recommendations](agent-tools.md) tab shows project-fit evidence,
bundled capabilities, source/usage metadata and exact-revision review actions.
Compact recommendation rows reveal capabilities, full fit evidence, source
links and the agent-review action when expanded. Open disclosures survive
model refreshes and switching between categories.
Installed inventory and source freshness are separate from relevance. Review
buttons request an agent plan, never approval to install, enable or remove.

## State and bidirectional actions

Domain state is stored under the SDK session workspace's
`files/nudge-dashboard/<workspace-hash>.json`, not in the repository or keyed by
panel ID. Panels for the same session/workspace share a model. Closing a panel
closes its loopback server, not its saved choices. Reopening or restarting the
provider reloads the saved state. New evidence invalidates changed selections
and details instead of transferring old consent to a new version.

The provider declares these agent actions:

| Action | Purpose |
| --- | --- |
| `get_model` | Read current report, selection, requests and optimistic version; `summary: true` omits the full package graph. |
| `set_report` | Replace evidence with a fresh report and optional cached status. |
| `set_selection` | Change exact revision/version/source selections; not approval. |
| `set_details` | Attach verified plain text and links for an exact revision/target/source. |
| `set_request_status` | Mark a known user request working/completed/failed with a result. |

All writes require the current model version. Conflicts are explicit; reconcile
live state before retrying. Iframe controls use same-origin JSON POSTs to a
random-token loopback URL. No SDK bridge or model credentials enter the page.
SSE invalidates views when agent actions or another panel change state.
Servers bind only to `127.0.0.1` on ephemeral ports and close with their panels.
The CSP permits inline **styles** so the host can inject its theme contract;
inline scripts, remote scripts and remote network requests are not permitted.

A UI request is persisted before `session.send` and acknowledged separately
from completion. The agent must mark its outcome with `set_request_status`.
Only one request is outstanding at a time; selections can still be adjusted
for a later plan without changing the submitted request. Failed/unconfirmed
delivery is explicit and never automatically resent. After interruption,
inspect chat and reconcile the pending request status before sending another;
the earlier message may already have reached the agent.

The canvas API is experimental. The offline provider/HTTP/state and MCP tests
do not, by themselves, certify the host's iframe presentation, native approval
picker, or an end-to-end package upgrade. Reload/scaffold tools are not
available in every host session; restart is the supported fallback for loading
checked-in providers.

## Branding assets

`dotnet-logo.svg` is the unmodified official asset from
[dotnet/brand](https://github.com/dotnet/brand/blob/main/logo/dotnet-logo.svg).
The logo is copyright of the .NET authors and used to represent .NET-related
content under the [.NET brand guidelines](https://github.com/dotnet/brand).
Keep its proportions, colors and clear space; do not recolor the mark for dark
mode or imply that this experiment is an endorsed Microsoft product.

`OpenSans.ttf` is the upright variable Open Sans typeface from
[googlefonts/opensans](https://github.com/googlefonts/opensans/tree/bd7e37632246368c60fdcbd374dbf9bad11969b6/fonts/variable),
the typeface recommended by the official .NET brand guide. The asset is retained
under `canvas/OFL.txt` (SIL Open Font License 1.1), but the dashboard uses the
host/system font stack to match Copilot's interface. No font CDN or runtime
network request is needed.

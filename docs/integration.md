# Integration contract

The executable `dotnet-nudge` supports `mcp [--state-dir PATH]` as a local
stdio MCP server using the pinned official C# SDK. Connect it as a host-managed
process; its stdout is exclusively MCP protocol traffic and diagnostics go to
stderr. The same state directory is used by CLI, hooks, and MCP. Set
`NUDGE_STATE_DIR` for all three or pass `--state-dir` to each launcher.

The portable `plugins/dotnet-nudge/mcp.json` and Claude-native `.mcp.json`
both require a previously installed executable named `dotnet-nudge` on the
host's `PATH`. `NUDGE_PLUGIN_DATA` points at the host's persistent plugin data,
matching the hook wrapper's fallback state location. `NUDGE_STATE_DIR`
overrides both when set for the host. Plugin installation alone cannot start
the MCP server before an engine has been supplied locally; see
`docs/plugin-development.md`.

The MCP tools accept an absolute `workspace` path. If the client advertises MCP
roots, the requested canonical path must be within a root. Clients without
roots can explicitly register an absolute workspace using `nudge_register`;
the server never treats its installation directory or process cwd as the
workspace. The following tools currently expose versioned JSON as text:

| Tool | Behavior |
| --- | --- |
| `nudge_register(workspace)` | Admit the exact root, keep the session active briefly, and queue a read-only scan. |
| `nudge_status(workspace)` | Read cached findings, suppressions and coverage without scanning. |
| `nudge_scan(workspace)` | Run an explicit read-only scan and persist its snapshot. |
| `nudge_context(workspace, findingId)` | Return current finding evidence without running third-party instructions. |
| `nudge_package_context(workspace, handle?)` | List exact restored-package skill handles or read one matching skill and linked references as inert data. |
| `nudge_decide(workspace, findingId, revision, action, until?)` | Record a revision-bound snooze, exact-version skip, disable-category or enable-category. |
| `nudge_preferences(workspace, findingId?, revision?, action?)` | Read current suppressions or change a category with current evidence. |
| `nudge_trial()` | Returns blocked coverage until the reviewed trial module is integrated; no trial is run. |

`status`, `scan`, `package-context`, and `decision` CLI commands use the same service/state layer.
`doctor --workspace PATH --json` reports cached scope coverage without scanning
and does not infer success from an absent worker or report.
`hook` registers its host-provided workspace and queues a scan on session
start/prompt submission; session end closes the active registration and
releases delivery leases. Hooks remain cached-only and never perform scans.
The MCP process reconciles changed project/configuration/assets and local Git
metadata every minute for active registrations, leases a queued scan per exact root, and
retries read-only failures at most three times. It exits with its host; there
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
with a quiet first baseline and durable external-change deltas. For mapped
NuGet sources, at most four direct packages per scan receive metadata-only
candidate/advisory checks; missing, unsettled, unmapped, or unassessed graph
coverage is explicit. No metadata result proves build/runtime compatibility,
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
Workload analysis and approved trials are not integrated yet. MCP protocol
child-process tests verify initialization, tool listing, scoped scan, graph
changes, exact guidance references, decisions and root rejection. Host
configuration and live host activation are not certified by that fixture.

# Integration contract

The executable `dotnet-nudge` supports `mcp [--state-dir PATH]` as a local
stdio MCP server using the pinned official C# SDK. Connect it as a host-managed
process; its stdout is exclusively MCP protocol traffic and diagnostics go to
stderr. The same state directory is used by CLI, hooks, and MCP. Set
`NUDGE_STATE_DIR` for all three or pass `--state-dir` to each launcher.

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
| `nudge_decide(workspace, findingId, revision, action, until?)` | Record a revision-bound snooze, exact-version skip, disable-category or enable-category. |
| `nudge_preferences(workspace, findingId?, revision?, action?)` | Read current suppressions or change a category with current evidence. |
| `nudge_trial()` | Returns blocked coverage until the reviewed trial module is integrated; no trial is run. |

`status`, `scan`, and `decision` CLI commands use the same service/state layer.
`hook` registers its host-provided workspace and queues a scan on session
start/prompt submission; session end closes the active registration and
releases delivery leases. Hooks remain cached-only and never perform scans.
The MCP process reconciles changed project/configuration/assets metadata every
minute for active registrations, leases a queued scan per exact root, and
retries read-only failures at most three times. It exits with its host; there
is no detached daemon. If MCP is lazily started or absent, cached status
reports pending coverage and an explicit `scan` is available. Worker results
cannot force a message into an otherwise idle chat.

The current scanner checks declared SDK servicing metadata only. It does not
yet claim resolved package, runtime, workload or trial coverage. Package
guidance reading and actual approved trials require their respective reviewed
modules; reporting a blocked operation rather than synthesizing success is
intentional. An MCP protocol child-process test verifies initialization,
tool listing, scan, context, decision suppression and root rejection. Host
configuration and live host activation are not certified by that fixture.

# Context-aware agent helpers

`agent-tools --workspace PATH --json` and `nudge_agent_tools(workspace)`
recommend installable plugin bundles from **dotnet/skills** and
**dotnet/maui-labs**. Bundles can include skills, language servers and MCP
helpers. This is discovery and planning, not an installer.

## How fit is established

The engine reads bounded SDK-style project/props declarations and component or
template file indicators, without evaluating MSBuild or executing project code.
Signals include MAUI, ASP.NET Core, Blazor, data-access dependencies, AI/MCP,
test frameworks, Windows desktop UI, platform bindings and .NET 11 targets.
An older target framework than the declared SDK can identify optional migration
help; it is not a claim that the target is unsupported.

Matching uses source plugin identities/descriptions and sampled SKILL.md
frontmatter. Domain gates prevent a MAUI pack from being suggested merely
because its description mentions testing, or template-authoring helpers from
matching every xUnit project. Descriptions after “DO NOT USE FOR” are not
positive usage matches. Legacy-labeled skills are not featured as current
examples. Overlapping broad MAUI bundles are shown as alternatives, preferring
an already installed/chosen bundle instead of recommending duplicate packs.

Every recommendation includes concrete workspace evidence and a pinned source
URL/revision. Relevance is **not** a trust, safety or compatibility score.
Conditional/computed features, unreadable files and limits stay explicit.
Repository-internal contributor skills are not treated as marketplace products.
The initial maintenance unit is the plugin bundle, not arbitrary copying or
deletion of individual skill files.

## Catalogs and periodic checks

Only these fixed public sources are contacted:

| Repository | Marketplace |
| --- | --- |
| `dotnet/skills` | `.claude-plugin/marketplace.json` |
| `dotnet/maui-labs` | `.github/plugin/marketplace.json` |

The client resolves an exact commit, reads only the relevant plugin subtrees,
and samples at most four skill descriptions per plugin. Limits are 32 plugins
per source, 4 MiB per metadata response and 16 KiB per skill prefix. A dedicated
30-second budget prevents catalog discovery from running indefinitely.
Contributor code and skill bodies are never activated or executed.

Public metadata is cached for 24 hours with ETag revalidation. Offline cached
metadata is labeled stale; unavailable/truncated sources do not establish that
a helper disappeared. Only fixed catalog URLs are sent to GitHub: project
files, package names, prompts and installed inventory are not uploaded.

Normal scans include helper assessment. The existing active-workspace worker
reassesses at its six-hour maintenance refresh and when project/context
fingerprints change. Razor/template **presence** changes count; editing their
body alone does not trigger a context-only rescan. There is no new daemon,
scheduled prompt or background installation. Existing category delivery limits,
snoozes, skips and mutes apply to `agent-tools`.

For an explicitly supplied offline catalog snapshot, set
`NUDGE_AGENT_CATALOG_FILE` to a JSON array of `AgentToolCatalog` records. Missing
sources/timestamps remain unknown, and old timestamps are stale. This is
reviewed evidence, not publisher authentication or installation authority.
The same mechanism supports deterministic tests without contacting GitHub.

## Installed inventory and lifecycle

Unknown installation state produces **review**, not “missing.” The agent
obtains inventory through the current host's real tools, such as Copilot's
plugin and marketplace listing, then supplies only the two configured
source-qualified resources. Do not identify a repository from a display name
alone or transfer one host's inventory to another.

Use `nudge_agent_tools(workspace, inventoryJson)` or:

```text
agent-tools --workspace PATH --inventory ABSOLUTE_JSON_FILE --json
```

The inventory is a bounded, explicit snapshot:

```json
{
  "schemaVersion": 1,
  "workspaceId": "<exact workspace ID>",
  "host": "copilot",
  "observedAt": "<current RFC3339 time>",
  "complete": true,
  "resources": [
    {
      "repository": "dotnet/skills",
      "name": "dotnet",
      "version": "<actually observed installed version>",
      "revision": null,
      "scope": "user",
      "enabled": true,
      "managedByNudge": false
    }
  ]
}
```

Set `complete` only when enumeration covers the supported sources and relevant
host scopes. A partial list cannot prove absence. Do not fabricate a source
revision; include a tree SHA only when the installed artifact can be verified.
Inventories older than 24 hours cannot establish an install/update/removal.
Validated typed snapshots are cached in Nudge state, not written into the
project or sent to catalog services.

| Result | Evidence and handling |
| --- | --- |
| Review | Project match, but installation is unknown/incomplete. Inspect the host setup. |
| Install suggestion | Fresh complete inventory establishes absence. Still needs approval. |
| Update suggestion | A lower installed version, or a verified same-version subtree change. Never downgrade. |
| Installed / disabled | Preserve the current state; do not automatically enable disabled helpers. Presence is not proof of being up to date. |
| Alternative | Another matching pack, not a recommendation to install both. |
| Review fit/removal | Only a fresh, enabled, Nudge-managed **project-scoped** record with complete context and a fresh complete source catalog. Investigate actual usage first. |

`managedByNudge` is a host-supplied management claim, not uninstall permission.
Never remove global/user-scoped or unmanaged resources merely because the
current workspace no longer matches. A catalog disappearance or missing
context is not proof that a helper is unused or unsafe.

The report does not mutate host installations. Before any installation,
marketplace registration, update, activation or removal, the agent must explain
the exact source/resource/version, target host/scope and bundled code that may
start (including LSP/MCP servers), then obtain native user approval. It must use
only the host's advertised supported installation operations, revalidate the
source revision, and verify the result. A repository's SKILL.md command is
never permission to run it. Re-read host inventory afterward.

## Reports, reminders and dashboard

Scans/status expose the `agentTools` report even when there is no actionable
reminder. `nudge_agent_tools` can refresh discovery and inventory independently;
run a scan before recording revision-bound reminder decisions.
Hooks offer a focused native choice such as “Show recommendations” or “Review
my setup.” The **Skills & tools** canvas tab shows a short list with reasons,
capabilities, source/usage details and exact-revision agent review buttons.
Those buttons request an investigation/plan only, not installation approval.

# Development: engine foundation

Build with a stable .NET 10 SDK. `global.json` requests at least 10.0.100 and
`latestFeature` within .NET 10; it does not select .NET 11 RC. Run:

```sh
dotnet build DotnetNudge.sln
dotnet test tests/DotnetNudge.Tests/DotnetNudge.Tests.csproj
dotnet test tests/DotnetNudge.Integration.Tests/DotnetNudge.Integration.Tests.csproj
NUDGE_TEST_ROOT=/path/to/disposable/artifacts dotnet run --project tests/DotnetNudge.Trials.Tests/DotnetNudge.Trials.Tests.csproj
dotnet publish src/DotnetNudge/DotnetNudge.csproj -c Release -r osx-arm64 --self-contained true -o publish
./publish/dotnet-nudge version
```

Replace `osx-arm64` with your machine's runtime identifier. The executable is
`dotnet-nudge` (`dotnet-nudge.exe` on Windows). Core references the reviewed Packages library and pins NuGet.Versioning
6.14.3 alongside Microsoft.Data.Sqlite. Data is stored in
`<OS application data>/dotnet-nudge/state.sqlite`, overridden by
`NUDGE_STATE_DIR` or `--state-dir PATH`. Do not place personal decisions in Git.
SQLite state schema v3 migrates v1/v2 snapshots, queued scans, and version skips in
place. An unsupported schema version raises an error without deleting state.

## CLI and versioned JSON

All non-hook operations except `doctor` require `--json`; `doctor` always emits
JSON. Success exits 0. Invalid input, source/IO/state failures exit 2 with a
versioned `{ "schemaVersion": 1, "error": "..." }` JSON object when `--json`
was supplied, plus stderr diagnostics. `help`/`--help` and `version` are text.

```text
scan --workspace PATH [--state-dir PATH] --json
status --workspace PATH [--state-dir PATH] --json
package-context --workspace PATH [--handle EXACT_HANDLE] [--state-dir PATH] --json
package-assess --workspace PATH --project RELATIVE_PROJECT --framework TFM --package ID --sources NAME[,NAME...] --approve-metadata [--rid RID] [--state-dir PATH] --json
toolchains --workspace PATH [--state-dir PATH] --json
trial root|propose|approve|execute|status|cancel|cleanup --workspace PATH [--plan ABSOLUTE_FILE] [--id ID] [--revision N] [--state-dir PATH] --json
preferences --workspace PATH [--enable-category NAME] [--state-dir PATH] --json
decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until RFC3339] [--state-dir PATH] --json
doctor [--workspace PATH] [--state-dir PATH] --json
hook --host copilot|claude --event session-start|prompt-submit|post-tool-use|session-end [--state-dir PATH]
version
```

`scan` reads workspace declarations, official .NET release metadata, settled
NuGet graphs and bounded mapped-source candidate/advisory metadata; it runs
read-only native `dotnet` probes for effective SDK selection, installed
SDKs/runtimes and local tools. It does not restore/build/install, evaluate
MSBuild, or prove candidate runtime compatibility. Package guidance is read
only from exact restored paths on an explicit `package-context` request.
`status` reads only SQLite; an unscanned worktree
reports unknown coverage, not a clean report. `decision` rejects a finding
absent from the current worktree snapshot or an outdated revision. Snooze
requires a future `--until`, skip-version binds the exact candidate and
revision, and disable/enable-category affects all findings in that category
for the repository. Git identities hash the canonical Git common directory
for decisions and the worktree root for worktree identity; snapshots and jobs
use a separate `workspaceId` hash of the exact admitted root. Scanning a Git
subdirectory never widens its scope or overwrites a sibling's snapshot.
SDK finding subjects use the Git-root-relative global.json path so distinct
pins within one worktree do not collide, while a pin keeps its identity across
worktrees. Non-Git folders hash their canonical path. Source fixture tests can
set `NUDGE_RELEASES_DIR` to a directory
containing `releases-index.json` and `<channel>/releases.json`.
Routine delivery is lease-atomic across sessions: one per session, no more
than one per repository per 24 hours, seven days between questions on the
same subject, with an expiring two-minute claim. A context delivery is not
evidence that the user saw or accepted the suggestion.

Core contract `Contract.Version = 1` uses camelCase property names and
kebab-case enums. Public types in `DotnetNudge.Core`:

```text
Evidence(Type, Uri, RetrievedAt)
FindingScope(Project?, Framework?, SdkScope?, RuntimeIdentifier?, Source?)
Finding(SchemaVersion, Id, SubjectKey, Revision, Kind, Priority,
        Current?, Candidate?, Scope, Assessment, Evidence[], Action,
        RequiresConsent, Category)
Coverage(Check, Assessment, Detail)
SdkDeclaration(Scope, Version?, RollForward?, AllowPrerelease?,
               Paths[], WorkloadVersion?, RawJson)
ProjectDeclaration(Path, Sdk?, TargetFrameworkExpressions[])
WorkspaceInventory(SchemaVersion, RepositoryId, WorktreeId, WorkspaceId, Root,
                   Projects[], ProjectDeclarations[], Solutions[], ConfigurationFiles[],
                   SdkDeclarations[], Coverage[])
ScanReport(SchemaVersion, Workspace, Findings[], Coverage[], ScannedAt)
FindingStatus(Finding, Eligible, Suppression?)
StatusReport(SchemaVersion, RepositoryId, WorktreeId, WorkspaceId, ScannedAt?,
             Findings[], Coverage[])
DecisionResult(SchemaVersion, FindingId, Revision, Action, RecordedAt, Until?)
CategoryPreference(Category, RecordedAt)
PreferencesReport(SchemaVersion, RepositoryId, WorkspaceId,
                  DisabledCategories[], SuppressedFindings[])
DoctorReport(SchemaVersion, StateDirectory, Workspace?, RepositoryId?,
             WorktreeId?, WorkspaceId?, ReleaseMetadataSource, Note, Coverage?)
```

`Assessment` is confirmed, unknown, partial, stale, or blocked. SDK candidate
findings are *partial* because a global.json declaration is not an observed
SDK selection. Same-feature-band comparisons use NuGet.Versioning and every
`sdks` entry in official channel metadata; they do not promote a different
band as a servicing patch. HTTP metadata is cached for 24 hours with ETags.
If a source fails, stale cached metadata is labeled stale; with no usable
cache the check is blocked. No empty success is synthesized.

## Hook boundary

Hooks read JSON from stdin (up to 1 MiB) and write JSON on stdout; errors go
to stderr. Blank or payloads without an existing `cwd`, `workspace`, or
`workspaceRoot` are no-ops (`{}`). Supported host/event pairs use the exact
`--host` and `--event` values above. Copilot `session-start` and
`post-tool-use` emit top-level `additionalContext`; `prompt-submit` never
injects context for Copilot config hooks. Claude emits
`hookSpecificOutput: { hookEventName, additionalContext }` at supported
context events. Session-end releases pending session leases, never blocks
completion. Context is limited to 4 KiB UTF-8. Hooks use cached state only:
they do not call the network or MSBuild, and queue durable scan-job records.
The host-managed `mcp` stdio process runs a bounded worker for active,
registered scopes. Queued records remain visible as partial coverage if the
MCP process is lazy/unavailable, and explicit `scan` completes them. See
`docs/integration.md` for the MCP tool contract. No host has been end-to-end
certified by the protocol fixture alone.

Offline workload/host analysis uses optional scoped `.dotnet-nudge/toolchains.json`
evidence; `toolchains --workspace PATH --json` inspects it and locally observed
runtimes. `trial root|propose|approve|execute|status|cancel|cleanup --workspace PATH
[--plan ABSOLUTE_FILE] [--id ID] [--revision N] --json` manages approved
baseline/candidate worktrees. `trial approve` requires direct human terminal
review and exact digest confirmation; MCP approval and redirected CLI
approval fail. See `docs/integration.md` and
`docs/upgrade-trials.md` for approval, isolation and platform limits. Package
feed checks remain partial without mapped source and complete audit evidence;
restored graphs are observed without changing projects. XML project/config inventory is declared
data only and does not represent evaluated MSBuild properties.

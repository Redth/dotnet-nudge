# Development: engine foundation

Build with a stable .NET 10 SDK. `global.json` requests at least 10.0.100 and
`latestFeature` within .NET 10; it does not select .NET 11 RC. Run:

```sh
dotnet build DotnetNudge.sln
dotnet test tests/DotnetNudge.Tests/DotnetNudge.Tests.csproj
dotnet publish src/DotnetNudge/DotnetNudge.csproj -c Release -r osx-arm64 --self-contained true -o publish
./publish/dotnet-nudge version
```

Replace `osx-arm64` with your machine's runtime identifier. The executable is
`dotnet-nudge` (`dotnet-nudge.exe` on Windows). Dependencies in Core are
Microsoft.Data.Sqlite and NuGet.Versioning. Data is stored in
`<OS application data>/dotnet-nudge/state.sqlite`, overridden by
`NUDGE_STATE_DIR` or `--state-dir PATH`. Do not place personal decisions in Git.
SQLite state schema v2 migrates v1 snapshots, queued scans, and version skips in
place. An unsupported schema version raises an error without deleting state.

## CLI and versioned JSON

All non-hook operations except `doctor` require `--json`; `doctor` always emits
JSON. Success exits 0. Invalid input, source/IO/state failures exit 2 with a
versioned `{ "schemaVersion": 1, "error": "..." }` JSON object when `--json`
was supplied, plus stderr diagnostics. `help`/`--help` and `version` are text.

```text
scan --workspace PATH [--state-dir PATH] --json
status --workspace PATH [--state-dir PATH] --json
decision --workspace PATH --finding ID --revision REV --action snooze|skip-version|disable-category|enable-category [--until RFC3339] [--state-dir PATH] --json
doctor [--workspace PATH] [--state-dir PATH] --json
hook --host copilot|claude --event session-start|prompt-submit|post-tool-use|session-end [--state-dir PATH]
version
```

`scan` reads workspace declarations and official .NET releases metadata, but
does not restore/build/install, evaluate MSBuild, resolve an effective SDK, or
scan a resolved NuGet graph. `status` reads only SQLite; an unscanned worktree
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
FindingScope(Project?, Framework?, SdkScope?)
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
DoctorReport(SchemaVersion, StateDirectory, Workspace?, RepositoryId?,
             WorktreeId?, WorkspaceId?, ReleaseMetadataSource, Note)
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
There is **no MCP worker yet**; queued records are visible as partial coverage
and an explicit `scan` completes them. No host has been end-to-end certified
by this foundation alone.

Not implemented here: package/security feed checks and resolved graph
inspection, runtime/workload/MAUI servicing, background worker/MCP, automatic
trials, or host plugin packaging. XML project/config inventory is declared
data only and does not represent evaluated MSBuild properties.

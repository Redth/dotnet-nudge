# Isolated upgrade trials (prototype)

`DotnetNudge.Trials` is a standalone `net10.0` library. It does not modify the
source checkout, the user's SDK, host settings, or Core's state schema. An
integration must explicitly create a `TrialPlan`, call `Propose`, obtain a
human approval of that **exact revision** through its own UI, call `Approve`,
and only then call `ExecuteAsync(id, approvedRevision)`. Passive hooks must
never call `Approve`. `Revise(id, nextPlan)` replaces only a still-proposed
plan with the next revision; approval persists a plan digest, and execution
rejects an altered plan even if its revision number is unchanged. The runner
neither publishes nor adopts a result.

```csharp
var store = new TrialStore(approvedAbsoluteTrialRoot);
var runner = new TrialRunner(store);
runner.Propose(plan);
runner.Approve(plan.Id, plan.Revision, TrialRunner.Digest(plan)); // only after a trusted human decision
var result = await runner.ExecuteAsync(plan.Id, plan.Revision, cancellationToken);
var report = runner.Report(plan.Id);
```

The CLI/MCP integration does not treat a tool call as a human decision:
`nudge_trial` refuses `approve`. The human reviews the complete persisted
plan, workspace, revision and digest in a local interactive
`dotnet-nudge trial approve` terminal and types the exact confirmation;
redirected/noninteractive CLI invocations cannot approve. The runner checks
the expected persisted plan digest while holding its approval lease. Library
callers remain responsible for their own equivalent trusted approval boundary.

`TrialPlan` pins the repository's committed `HEAD` SHA, approved project scopes,
exact SDK versions and RID, SDK-only versus explicit package edits, and a
restore/build/test argument-vector allowlist. It carries disk, overall time and
per-command budgets and explicit `TrialArtifactPaths` under the trial-owned
`artifacts/` directory. A candidate SDK alone does **not** change TFMs. A
`PackageEdit` names one exact declaration (`PackageReference` or
`PackageVersion`) and its expected/current and target versions; source
migrations and workload installation are deliberately not implemented.
For offline fixtures `SdkSelection.PreparedRoot` points to a prepared SDK,
which the runner **copies** into a new trial-owned root. SDK-internal links to
contained regular files are materialized as regular files, including the
assembly links in .NET 11; external, dangling, cyclic and directory links are
rejected. Repository symlinks remain unsupported. Otherwise
`AllowNetwork` on the plan and `AllowDownload` on each SDK are both required:
the runner reads official release metadata for the pinned version/RID,
verifies the SHA-512 archive hash, rejects redirects and unsupported tar entries,
then extracts into an owned directory, materializing only contained regular-file
links. Both preparations share the remaining aggregate trial disk allowance,
including the downloaded archive while it is being extracted. Only matching
macOS/Linux x64/arm64 RIDs
are supported. Windows, workloads, Xcode, Android and other platform installers
are blocked rather than represented as validated.

The trial directory is exclusively claimed when proposed; an existing
`<trialRoot>/<id>` is never adopted. Ownership is checked before mutations and
cleanup, and the repository and trial root must not overlap in either direction.
Baseline and candidate use **separate** worktrees/branches,
`<trialRoot>/<id>/source` (`nudge-trial-<id>`) and
`<trialRoot>/<id>/candidate-source` (`nudge-trial-<id>-candidate`), each from
the approved committed HEAD. Original tracked and untracked changes are
excluded and reported; there is no stash, reset or original commit. HEAD,
branch, index bytes, tracked/untracked file contents, and Git status are
fingerprinted before and after execution; a changed original blocks the
comparison, even when porcelain status is unchanged. Sparse checkout,
submodules, symlinks, LFS, uncommitted/escaping/indeterminate imports and
project references, and external ancestor MSBuild config currently block.
Committed literal imports/references are followed transitively through tracked
XML files of any extension, with cycles visited once; the original checkout's
dirty copies cannot hide a committed nested escape.
Trial commands may target only approved project paths. Built assemblies must
exist in each stage's local `bin/`, are hashed, and each `test --no-build`
requires a preceding verified build of the same project with an unchanged
assembly hash. This proves artifact identity, not isolation from arbitrary
project build logic.
Commands use the exact private `dotnet` executable with explicit cwd and
process-local `DOTNET_ROOT`, architecture root, `PATH`, `DOTNET_CLI_HOME`,
private NuGet roots and `NUDGE_TRIAL_ID`. An inherited environment is not
forwarded. `--version` must prove the selected SDK in both stages. The
trial-only `global.json` pin is changed without rewriting comments or unrelated
properties. Resolver `paths` and external ancestor `global.json` block.

`TrialStore` persists `state.json` checkpoints, a unique ownership token/marker
and phase under the trial root; a per-trial exclusive file lease rejects
concurrent executions.
`Status` and `Report` only read state and never replay an uncertain mutation.
`Cancel(id)` signals a currently running process in the **same runner
instance**, or cancels an unstarted proposal/approval; an interrupted runner's
checkpoint must be reviewed manually. The execution token can also cancel
the owned process tree. `CleanupAsync` refuses a dirty worktree, a busy lease,
or mismatched ownership metadata; clean worktrees are removed through Git and
the exact owned branch refs are deleted at their expected base commit before
trial-private SDK/cache roots are deleted. Each step is checkpointed so a
partial removal can be retried without deleting another checkout's branch.
Reports and logs remain.
`MarkAdopted` is bookkeeping allowed only after review; integration must
perform and validate any adoption separately. There is no automatic target
checkout change, push, PR or merge.

The report contains baseline and candidate command evidence, stdout/stderr
paths, exit codes, elapsed time, non-secret environment identity, stage-local
build artifact paths/hashes, private SDK
executable SHA-256 and acquisition provenance (official archive SHA-512 for
downloads), test counts, dimension summaries and a Git diff. Missing/zero tests are *not exercised*,
never a test pass; a no-test plan is explicitly compile-only. Failed test runs
retain discovered counts and exit evidence. A failed command or a baseline
that changes tracked source blocks the trial with its logs retained. SDK
acquisition/copy/extraction writes are budgeted as one aggregate, while
arbitrary project subprocess writes are checked after each command, not
intercepted in real time. Warning lines are compared across
stages. Package graph/advisory comparison, runtime smoke and platform matrix
are not exercised by this library.

**Isolation limit:** Git worktrees and private .NET roots are not a sandbox.
Project MSBuild targets, test code, and external tools can still write outside
the worktree. Only run trusted project code or use an independently approved
OS sandbox/VM. `PATH` includes the host path after the private SDK for ordinary
tools. This prototype does not prove arbitrary build code cannot mutate the
original checkout or shared state, nor impose a hard disk ceiling on arbitrary
project subprocess writes.

Run independently (no shared solution or Core dependency):

```sh
dotnet build tests/DotnetNudge.Trials.Tests/DotnetNudge.Trials.Tests.csproj
NUDGE_TEST_ROOT=/path/to/disposable/artifacts \
  dotnet run --project tests/DotnetNudge.Trials.Tests/DotnetNudge.Trials.Tests.csproj --no-build
```

An optional real .NET 10 smoke also sets `NUDGE_REAL_REPO` to a disposable
uncommitted xUnit fixture directory and `NUDGE_REAL_SDK_ROOT` to an already
prepared private .NET 10.0.401 root; it runs real restore/build/test in a
worktree. Set `NUDGE_ORIGINAL_DOTNET` to the developer's installed `dotnet`
binary to compare its executable hash and SDK inventory before and after.
Set `NUDGE_REAL_DOWNLOAD=1` to additionally test official
metadata/download/digest/extraction of 10.0.401 (network and disk intensive);
this test does not perform a project build using that downloaded root.
`NUDGE_MAJOR_REPO`, `NUDGE_REAL_SDK_ROOT` and `NUDGE_MAJOR_SDK_ROOT` opt into a
real .NET 10.0.401 to .NET 11 SDK-only worktree trial (still targeting
`net10.0`). `NUDGE_MAJOR_DOWNLOAD=1` additionally probes official .NET 11
metadata, digest, archive link materialization and private SDK version. The
offline fixtures do not prove remote download behavior.
`.github/workflows/trials-ci.yml` runs only the offline direct-project
build/fixture tests on macOS.

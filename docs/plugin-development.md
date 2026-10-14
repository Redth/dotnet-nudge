# Plugin development

This document covers marketplace packaging, hook contracts, and local
validation. The .NET engine and MCP/publishing work are separate phases; do not
infer their completion from this plugin package.

## Layout and host loading

The marketplace is `.claude-plugin/marketplace.json` and its plugin source is
`plugins/dotnet-nudge/`. That plugin has one Agent Plugins 1.0 root
`plugin.json` for portable skills, plus host-native manifests/configuration:

| Host | Manifest | Hooks |
| --- | --- | --- |
| Copilot CLI | `plugin.json` | `com.github.copilot/hooks/hooks.json` |
| Claude Code | `.claude-plugin/plugin.json` | `hooks/hooks.json` |

Both hosts load the same four immediate child skill directories under `skills/`.
Hooks are configured once per host; there is no portable hook schema and no
MCP configuration in this package. Do not add both a native and portable hook
entry for the same event. Do not add an enabled MCP server until the engine
implements that server.

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

The Bash and PowerShell wrappers resolve an explicit `NUDGE_ENGINE_PATH`
override first, then an engine in the plugin's `bin/` or the writable user-data
engine location. Host plugin-data locations are fallback candidates. Paths are
quoted as single arguments, including paths containing spaces. The wrappers
use no `jq` or Node.js.

If no engine is found, the wrapper emits one bounded, host-correct setup
diagnostic and records its once-only marker in the writable state directory,
not the plugin cache. `NUDGE_STATE_DIR` overrides the platform data location
and is shared by the two adapters when set. If state storage is unavailable,
the wrapper suppresses repeated setup messages rather than writing into the
plugin cache.

## Runtime setup boundary

There is no published engine release or trusted release digest in this
repository yet. `runtime-manifest.json` therefore declares an unpublished
status and an empty artifact list; do not invent a release URL, version pin, or
checksum.

For local development, build or obtain an engine executable separately and
set `NUDGE_ENGINE_PATH` before launching the host. When a trusted archive is
provided locally, the setup helper accepts a ZIP or tar archive containing one
RID-appropriate self-contained publish tree with `dotnet-nudge` (or
`dotnet-nudge.exe` on Windows) at the archive root:

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
the six declared `linux|osx|win` x64/arm64 RIDs are accepted. Release downloads
are planned future behavior, not implemented behavior.

## Offline validation

The plugin-contract suite uses only Python's standard library and fixture
engines; it does not depend on files from the engine implementation:

```sh
python -m unittest discover -s tests/plugin-contracts -v
```

CI runs this suite on Linux, macOS, and Windows. It validates manifests,
host-event mappings, safe relative paths, launcher argument/stream behavior,
once-only setup notices, and installer checksum/traversal rejection.

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

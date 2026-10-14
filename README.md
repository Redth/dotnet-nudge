# dotnet-nudge

A .NET maintenance plugin marketplace with shared agent skills and separate
Copilot CLI and Claude Code hook adapters.

## Current status

The plugin uses one launcher for MCP, hooks and CLI commands. On first MCP or
CLI use, it automatically downloads the matching self-contained engine from
this repository's latest stable release, verifies both the compressed archive
and executable SHA-256 digests, and installs it under writable user data.
Subsequent starts reuse that engine offline; no engine `PATH` modification or
.NET SDK installation is required. **Node.js 22 or newer must be on `PATH`.**
Hooks remain network-free and never bootstrap an engine themselves.

The release workflow packages Linux, macOS and Windows x64/arm64 engines and
publishes their generated manifest only when all six assets are ready. A first
engine release must be available for bootstrap: otherwise, automatic setup
reports an explicit download failure, not a successful assessment. For local source
development, set `NUDGE_ENGINE_PATH` to a built executable; this override now
works for both hooks and MCP. `NUDGE_AUTO_SETUP=0` disables automatic downloads.

The engine exposes `version`, read-only `scan`/`status`/`package-context`/
`toolchains`, `doctor`, structured `decision`, approved isolated `trial`
operations, and a local stdio `mcp` command. The plugin declares that server
in portable `mcp.json` for Copilot CLI and native `.mcp.json` for Claude Code.
Both MCP configurations use the plugin-relative launcher rather than an engine
executable on `PATH`. Restored package graph changes, bounded
mapped-source metadata, offline toolchain analysis, inert package guidance
and exact-approved worktree trials are integrated. Trials run trusted build
code without an OS sandbox; hook/schema validation does not prove
end-to-end model delivery.

For local source development, once the foundation source is present:

```sh
dotnet build src/DotnetNudge/DotnetNudge.csproj
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- version
dotnet run --project src/DotnetNudge/DotnetNudge.csproj -- status --workspace "$PWD" --json
```

## Host support

- **Copilot CLI:** installs from the shared `.claude-plugin/marketplace.json`.
  Session-start and post-tool hooks can supply `additionalContext`.
  `userPromptSubmitted` output is not delivered as context; it is bookkeeping
  only. The wrappers do not submit prompts or block turn completion.
- **Claude Code:** uses its own hook output serializer and never blocks `Stop`.
  The checked-in command launcher uses Bash; on Windows, Claude Code hook use
  requires a compatible Bash shell. The PowerShell launcher is selected by
  Copilot CLI on Windows.
- Hooks preserve the engine's stdout, stderr, stdin, and exit status. A missing
  engine produces one short setup message at most once in the writable state
  directory, without writing to the installed plugin cache. An unusable state
  directory produces an explicit stderr error instead of a chat message.

See [plugin development](docs/plugin-development.md) for isolated validation,
hook details, setup constraints, and compatibility limitations, and the
[integration contract](docs/integration.md) for MCP and worker behavior, and the
[implementation plan](docs/implementation-plan.md) for the broader roadmap.

Independent macOS headless Copilot CLI acceptance confirmed MCP connection,
package-guidance reading, and cached session-start hook context with a
synthetic SDK finding using an explicitly installed local engine. It does not
certify Claude Code, IDE hosts, other operating systems' conversations, or
execution of suggested changes; see the [qualified evidence](docs/integration.md).

## Installation

After this marketplace entry is available from the repository's published
revision, add and install it with:

```sh
copilot plugin marketplace add Redth/dotnet-nudge
copilot plugin install dotnet-nudge@dotnet-nudge-marketplace

claude plugin marketplace add Redth/dotnet-nudge
claude plugin install dotnet-nudge@dotnet-nudge-marketplace
```

These commands install the plugin files. The MCP launcher installs the verified
engine on first connection once an engine release is available. Restart the
host after updating an already installed plugin so it reloads the MCP config.
The engine download does not authorize SDK/workload installation, package
changes, restores, builds, or trials.

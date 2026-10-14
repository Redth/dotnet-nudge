# dotnet-nudge

A .NET maintenance plugin marketplace with shared agent skills and separate
Copilot CLI and Claude Code hook adapters.

## Current status

The plugin packaging and hook contracts are implemented, but there is no
published `dotnet-nudge` engine release yet. The hook wrappers do not download
or install a binary. To exercise a locally built engine, set
`NUDGE_ENGINE_PATH` to its executable before starting the host. An explicitly
provided local release archive can be installed with its exact published
version, runtime identifier (RID), and SHA-256 digest using
`plugins/dotnet-nudge/scripts/setup-runtime.py`; the installer verifies the
digest and installs the complete self-contained publish tree, including native
dependencies, under writable user data.
`runtime-manifest.json` intentionally has no release artifacts until a real
release is published.

The engine exposes `version`, read-only `scan`/`status`/`package-context`, `doctor`, structured
`decision`, and a local stdio `mcp` command. The plugin declares that server
in portable `mcp.json` for Copilot CLI and native `.mcp.json` for Claude Code.
It starts only after the self-contained engine is installed and its directory
is on the host's `PATH`; plugin installation alone still cannot start the
engine. No release is published yet. Restored package graph changes, bounded
mapped-source metadata and inert package guidance are integrated; the trial
module is not integrated, and
hook/schema validation does not prove end-to-end model delivery.

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

## Installation

After this marketplace entry is available from the repository's published
revision, add and install it with:

```sh
copilot plugin marketplace add Redth/dotnet-nudge
copilot plugin install dotnet-nudge@dotnet-nudge-marketplace

claude plugin marketplace add Redth/dotnet-nudge
claude plugin install dotnet-nudge@dotnet-nudge-marketplace
```

These commands install the plugin files only. They do not install the engine;
follow `nudge-maintain` for the explicitly local runtime setup path.

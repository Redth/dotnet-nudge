#!/usr/bin/env bash
set -u

script_dir="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)" || exit 2
if ! command -v node >/dev/null 2>&1; then
  printf '%s\n' "dotnet-nudge: Node.js 22 or newer is required by the plugin launcher." >&2
  exit 1
fi
exec node "$script_dir/run.cjs" hook --host "${1:-}" --event "${2:-}"

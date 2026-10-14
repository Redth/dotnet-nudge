#!/usr/bin/env bash
set -u

host="${1:-}"
event="${2:-}"

case "$host:$event" in
  copilot:session-start|copilot:prompt-submit|copilot:post-tool-use|copilot:session-end|claude:session-start|claude:prompt-submit|claude:post-tool-use|claude:session-end)
    ;;
  *)
    printf '%s\n' "dotnet-nudge: invalid hook host or event." >&2
    exit 2
    ;;
esac

script_dir="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)" || exit 2
plugin_root="$(CDPATH= cd -- "$script_dir/.." && pwd -P)" || exit 2

if [[ -n "${NUDGE_STATE_DIR:-}" ]]; then
  data_root="$NUDGE_STATE_DIR"
elif [[ -n "${COPILOT_PLUGIN_DATA:-}" ]]; then
  data_root="$COPILOT_PLUGIN_DATA"
elif [[ -n "${CLAUDE_PLUGIN_DATA:-}" ]]; then
  data_root="$CLAUDE_PLUGIN_DATA"
elif [[ -n "${XDG_DATA_HOME:-}" ]]; then
  data_root="$XDG_DATA_HOME/dotnet-nudge"
elif [[ "$(uname -s 2>/dev/null || true)" == Darwin ]]; then
  data_root="${HOME:-.}/Library/Application Support/dotnet-nudge"
else
  data_root="${HOME:-.}/.local/share/dotnet-nudge"
fi

if [[ -n "${XDG_DATA_HOME:-}" ]]; then
  default_engine_root="$XDG_DATA_HOME/dotnet-nudge"
elif [[ "$(uname -s 2>/dev/null || true)" == Darwin ]]; then
  default_engine_root="${HOME:-.}/Library/Application Support/dotnet-nudge"
else
  default_engine_root="${HOME:-.}/.local/share/dotnet-nudge"
fi

resolve_installed_engine() {
  local root="$1"
  local relative
  local version
  local rid
  local candidate
  local engine_root
  local version_dir
  local rid_dir
  local candidate_dir
  local resolved_root

  engine_root="$root/engine"
  [[ -d "$engine_root" && ! -L "$engine_root" ]] || return 1
  [[ -r "$engine_root/current" && ! -L "$engine_root/current" ]] || return 1
  IFS= read -r relative < "$engine_root/current" || return 1
  [[ "$relative" =~ ^([0-9][0-9A-Za-z.+-]*)/((linux|osx|win)-(x64|arm64))$ ]] || return 1
  version="${BASH_REMATCH[1]}"
  rid="${BASH_REMATCH[2]}"
  version_dir="$engine_root/$version"
  rid_dir="$version_dir/$rid"
  candidate="$rid_dir/dotnet-nudge"
  [[ -d "$version_dir" && ! -L "$version_dir" ]] || return 1
  [[ -d "$rid_dir" && ! -L "$rid_dir" ]] || return 1
  [[ -f "$candidate" && -x "$candidate" && ! -L "$candidate" ]] || return 1
  resolved_root="$(CDPATH= cd -- "$engine_root" && pwd -P)" || return 1
  candidate_dir="$(CDPATH= cd -- "$rid_dir" && pwd -P)" || return 1
  case "$candidate_dir/" in
    "$resolved_root"/*)
      ENGINE_CANDIDATE="$candidate"
      return 0
      ;;
  esac
  return 1
}

engine_path=""
if [[ -n "${NUDGE_ENGINE_PATH:-}" ]]; then
  engine_path="$NUDGE_ENGINE_PATH"
elif [[ -x "$plugin_root/bin/dotnet-nudge" ]]; then
  engine_path="$plugin_root/bin/dotnet-nudge"
elif resolve_installed_engine "$data_root"; then
  engine_path="$ENGINE_CANDIDATE"
elif resolve_installed_engine "$default_engine_root"; then
  engine_path="$ENGINE_CANDIDATE"
elif [[ -n "${COPILOT_PLUGIN_DATA:-}" ]] && resolve_installed_engine "$COPILOT_PLUGIN_DATA"; then
  engine_path="$ENGINE_CANDIDATE"
elif [[ -n "${CLAUDE_PLUGIN_DATA:-}" ]] && resolve_installed_engine "$CLAUDE_PLUGIN_DATA"; then
  engine_path="$ENGINE_CANDIDATE"
fi

if [[ -n "$engine_path" && -f "$engine_path" && -x "$engine_path" ]]; then
  export NUDGE_STATE_DIR="$data_root"
  exec "$engine_path" hook --host "$host" --event "$event"
fi

diagnostic=""
case "$host:$event" in
  copilot:session-start|copilot:post-tool-use)
    diagnostic='{"additionalContext":"dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; no runtime was downloaded or run."}'
    ;;
  claude:session-start)
    diagnostic='{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; no runtime was downloaded or run."}}'
    ;;
  claude:prompt-submit)
    diagnostic='{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; no runtime was downloaded or run."}}'
    ;;
  claude:post-tool-use)
    diagnostic='{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; no runtime was downloaded or run."}}'
    ;;
esac

if [[ -z "$diagnostic" ]]; then
  exit 0
fi

write_state_error() {
  printf '%s\n' "dotnet-nudge: unable to persist the one-time setup notice in '$data_root'; set NUDGE_STATE_DIR to writable storage." >&2
}

if ! mkdir -p -- "$data_root" 2>/dev/null; then
  write_state_error
  exit 0
fi

marker="$data_root/.setup-required-notice-shown"
if [[ -e "$marker" || -L "$marker" ]]; then
  if [[ -f "$marker" && ! -L "$marker" ]]; then
    exit 0
  fi
  write_state_error
  exit 0
fi

if (set -o noclobber; : > "$marker") 2>/dev/null; then
  printf '%s\n' "$diagnostic"
elif [[ -f "$marker" && ! -L "$marker" ]]; then
  exit 0
else
  write_state_error
fi

exit 0

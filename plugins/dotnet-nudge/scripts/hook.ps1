param(
    [Parameter(Position = 0)]
    [ValidateSet("copilot", "claude")]
    [string] $HostName,

    [Parameter(Position = 1)]
    [ValidateSet("session-start", "prompt-submit", "post-tool-use", "session-end")]
    [string] $EventName
)

$ErrorActionPreference = "Stop"
$script:Diagnostics = "dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; no runtime was downloaded or run."

$scriptDir = $PSScriptRoot
$pluginRoot = Split-Path -Parent $scriptDir

if ($env:NUDGE_STATE_DIR) {
    $dataRoot = $env:NUDGE_STATE_DIR
} elseif ($env:COPILOT_PLUGIN_DATA) {
    $dataRoot = $env:COPILOT_PLUGIN_DATA
} elseif ($env:CLAUDE_PLUGIN_DATA) {
    $dataRoot = $env:CLAUDE_PLUGIN_DATA
} elseif ($env:LOCALAPPDATA) {
    $dataRoot = Join-Path $env:LOCALAPPDATA "dotnet-nudge"
} elseif ($env:XDG_DATA_HOME) {
    $dataRoot = Join-Path $env:XDG_DATA_HOME "dotnet-nudge"
} elseif ($IsMacOS) {
    $dataRoot = Join-Path $HOME "Library/Application Support/dotnet-nudge"
} else {
    $dataRoot = Join-Path $HOME ".local/share/dotnet-nudge"
}

$defaultEngineRoot = if ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA "dotnet-nudge"
} elseif ($env:XDG_DATA_HOME) {
    Join-Path $env:XDG_DATA_HOME "dotnet-nudge"
} elseif ($IsMacOS) {
    Join-Path $HOME "Library/Application Support/dotnet-nudge"
} else {
    Join-Path $HOME ".local/share/dotnet-nudge"
}

function Get-InstalledEngine([string] $DataRoot) {
    $engineRoot = Join-Path $DataRoot "engine"
    $pointerPath = Join-Path $engineRoot "current"
    if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) {
        return $null
    }

    $relative = (Get-Content -LiteralPath $pointerPath -TotalCount 1).Trim()
    if ($relative -notmatch '^[0-9][0-9A-Za-z.+-]*/(linux|osx|win)-(x64|arm64)$') {
        return $null
    }

    $candidate = Join-Path (Join-Path $engineRoot $relative) "dotnet-nudge.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        return $null
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($engineRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $resolvedCandidate = [System.IO.Path]::GetFullPath($candidate)
    if (-not $resolvedCandidate.StartsWith(
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        return $null
    }
    return $resolvedCandidate
}

$enginePath = $null
if ($env:NUDGE_ENGINE_PATH) {
    $enginePath = $env:NUDGE_ENGINE_PATH
} else {
    $pluginEngine = Join-Path $pluginRoot "bin/dotnet-nudge.exe"
    if (Test-Path -LiteralPath $pluginEngine -PathType Leaf) {
        $enginePath = $pluginEngine
    } else {
        $dataRoots = @($dataRoot, $defaultEngineRoot, $env:COPILOT_PLUGIN_DATA, $env:CLAUDE_PLUGIN_DATA) |
            Where-Object { $_ } |
            Select-Object -Unique
        foreach ($root in $dataRoots) {
            $enginePath = Get-InstalledEngine $root
            if ($enginePath) {
                break
            }
        }
    }
}

if ($enginePath -and (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
    & $enginePath hook --host $HostName --event $EventName
    exit $LASTEXITCODE
}

$diagnostic = switch ("$HostName`:$EventName") {
    "copilot:session-start" { '{"additionalContext":"' + $script:Diagnostics + '"}' }
    "copilot:post-tool-use" { '{"additionalContext":"' + $script:Diagnostics + '"}' }
    "claude:session-start" { '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"' + $script:Diagnostics + '"}}' }
    "claude:prompt-submit" { '{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"' + $script:Diagnostics + '"}}' }
    "claude:post-tool-use" { '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"' + $script:Diagnostics + '"}}' }
    default { $null }
}

if (-not $diagnostic) {
    exit 0
}

try {
    [System.IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $markerPath = Join-Path $dataRoot ".setup-required-notice-shown"
    $stream = [System.IO.File]::Open(
        $markerPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    $stream.Dispose()
    [Console]::Out.WriteLine($diagnostic)
} catch [System.IO.IOException] {
    exit 0
} catch [System.UnauthorizedAccessException] {
    exit 0
}

exit 0

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
    if (-not (Test-Path -LiteralPath $engineRoot -PathType Container) -or
        (Test-ReparsePoint $engineRoot) -or
        -not (Test-Path -LiteralPath $pointerPath -PathType Leaf) -or
        (Test-ReparsePoint $pointerPath)) {
        return $null
    }

    $relative = (Get-Content -LiteralPath $pointerPath -TotalCount 1).Trim()
    if ($relative -notmatch '^(?<version>[0-9][0-9A-Za-z.+-]*)/(?<rid>(linux|osx|win)-(x64|arm64))$') {
        return $null
    }

    $versionDirectory = Join-Path $engineRoot $Matches.version
    $ridDirectory = Join-Path $versionDirectory $Matches.rid
    $candidate = Join-Path $ridDirectory "dotnet-nudge.exe"
    if (-not (Test-Path -LiteralPath $versionDirectory -PathType Container) -or
        (Test-ReparsePoint $versionDirectory) -or
        -not (Test-Path -LiteralPath $ridDirectory -PathType Container) -or
        (Test-ReparsePoint $ridDirectory) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
        (Test-ReparsePoint $candidate)) {
        return $null
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $engineRoot).ProviderPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $resolvedCandidate = (Resolve-Path -LiteralPath $candidate).ProviderPath
    if (-not $resolvedCandidate.StartsWith(
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        return $null
    }
    return $resolvedCandidate
}

function Test-ReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return $null -ne $item -and
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Test-ExistingSetupMarker([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return $item -is [System.IO.FileInfo] -and
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)
}

function Write-SetupStateError([string] $Reason) {
    [Console]::Error.WriteLine(
        "dotnet-nudge: unable to persist the one-time setup notice in '$dataRoot': $Reason. Set NUDGE_STATE_DIR to writable storage."
    )
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
    $env:NUDGE_STATE_DIR = $dataRoot
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
} catch [System.IO.IOException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.UnauthorizedAccessException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.ArgumentException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.NotSupportedException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
}

$markerPath = Join-Path $dataRoot ".setup-required-notice-shown"
$markerItem = Get-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
if ($null -ne $markerItem) {
    if (Test-ExistingSetupMarker $markerPath) {
        exit 0
    }
    Write-SetupStateError "the setup marker path is not a regular file"
    exit 0
}

try {
    $stream = [System.IO.File]::Open(
        $markerPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    $stream.Dispose()
    [Console]::Out.WriteLine($diagnostic)
} catch [System.IO.IOException] {
    if (Test-ExistingSetupMarker $markerPath) {
        exit 0
    }
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.UnauthorizedAccessException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.ArgumentException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
} catch [System.NotSupportedException] {
    Write-SetupStateError $_.Exception.Message
    exit 0
}

exit 0

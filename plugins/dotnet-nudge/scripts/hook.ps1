param(
    [Parameter(Position = 0)]
    [ValidateSet("copilot", "claude")]
    [string] $HostName,

    [Parameter(Position = 1)]
    [ValidateSet("session-start", "prompt-submit", "post-tool-use", "session-end")]
    [string] $EventName
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine("dotnet-nudge: Node.js 22 or newer is required by the plugin launcher.")
    exit 1
}
& node (Join-Path $PSScriptRoot "run.cjs") hook --host $HostName --event $EventName
exit $LASTEXITCODE

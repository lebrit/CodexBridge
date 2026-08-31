[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publish = [IO.Path]::GetFullPath($PublishDirectory)
$app = Join-Path $publish 'CodexBridge.App.exe'
$agent = Join-Path $publish 'CodexBridge.Agent.exe'
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $agent)) {
    throw 'Published App or Agent executable is missing.'
}

$process = $null
try {
    $process = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) {
        throw "CodexBridge.App exited during smoke test with code $($process.ExitCode)."
    }
    Write-Host "GUI_SMOKE_OK=$($process.Id)"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit(5000) | Out-Null
    }
}

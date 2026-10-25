[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,
    [string]$ReportDirectory = ''
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
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\ui-smoke-' + [Guid]::NewGuid().ToString('N'))
}
$report = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $report) { throw 'Smoke report directory must be new.' }
New-Item -ItemType Directory -Path $report | Out-Null
try {
    $process = Start-Process -FilePath $app -ArgumentList "--smoke-test `"$report`"" -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        throw 'GUI smoke test timed out.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $report 'success.txt'))) {
        $failure = Join-Path $report 'failure.txt'
        if (Test-Path -LiteralPath $failure) { Get-Content -LiteralPath $failure | Write-Host }
        throw "GUI smoke test failed with code $($process.ExitCode). Report: $report"
    }
    $checks = @(Get-Content -LiteralPath (Join-Path $report 'success.txt'))
    if ($checks.Count -ne 36) { throw "Incomplete GUI report: $($checks.Count) checks." }
    Write-Host "GUI_SMOKE_OK=36 checks; $report"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit(5000) | Out-Null
    }
}

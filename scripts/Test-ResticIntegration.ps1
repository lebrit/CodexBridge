[CmdletBinding()]
param(
    [string]$ResticExecutable = 'restic'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$restic = (Get-Command $ResticExecutable -ErrorAction Stop).Source
$oldMigrationLab = $env:CODEXBRIDGE_RUN_RESTIC_INTEGRATION
$oldResticExecutable = $env:CODEXBRIDGE_RESTIC_EXECUTABLE

try {
    $env:CODEXBRIDGE_RUN_RESTIC_INTEGRATION = '1'
    $env:CODEXBRIDGE_RESTIC_EXECUTABLE = $restic
    Push-Location $repoRoot
    try {
        dotnet test .\CodexBridge.sln -c Release --nologo `
            --filter 'FullyQualifiedName~Migration_lab_restores_real_snapshot_idempotently'
        if ($LASTEXITCODE -ne 0) {
            throw 'Migration lab failed.'
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:CODEXBRIDGE_RUN_RESTIC_INTEGRATION = $oldMigrationLab
    $env:CODEXBRIDGE_RESTIC_EXECUTABLE = $oldResticExecutable
}

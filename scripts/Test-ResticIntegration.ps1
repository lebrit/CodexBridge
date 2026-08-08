[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = Join-Path $tempBase ("CodexBridge-Restic-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $testRoot 'source'
$repository = Join-Path $testRoot 'repository'
$restore = Join-Path $testRoot 'restore'
$oldPassword = $env:RESTIC_PASSWORD

try {
    [IO.Directory]::CreateDirectory($source) | Out-Null
    [IO.Directory]::CreateDirectory($repository) | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'sample.txt'), 'codexbridge-restic-ok')
    $passwordBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($passwordBytes) } finally { $random.Dispose() }
    $env:RESTIC_PASSWORD = [Convert]::ToBase64String($passwordBytes)

    restic -r $repository init | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'restic init failed.' }
    restic -r $repository backup $source --tag codexbridge --json
    if ($LASTEXITCODE -ne 0) { throw 'restic backup failed.' }
    restic -r $repository check | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'restic check failed.' }
    restic -r $repository restore latest --target $restore
    if ($LASTEXITCODE -ne 0) { throw 'restic restore failed.' }

    $sample = Get-ChildItem -LiteralPath $restore -Recurse -Force | Where-Object { -not $_.PSIsContainer -and $_.Name -eq 'sample.txt' } | Select-Object -First 1
    if ($null -eq $sample) {
        restic -r $repository ls latest
        Get-ChildItem -LiteralPath $restore -Recurse -Force | ForEach-Object { Write-Host "RESTORED=$($_.FullName)" }
        throw 'Restored sample was not found.'
    }
    if ([IO.File]::ReadAllText($sample.FullName) -ne 'codexbridge-restic-ok') { throw 'Restored sample content differs.' }

    Write-Host "RESTIC_INTEGRATION_OK=$($sample.FullName.Substring($restore.Length).TrimStart('\'))"
}
finally {
    $env:RESTIC_PASSWORD = $oldPassword
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

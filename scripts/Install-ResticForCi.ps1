[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$command = Get-Command restic -ErrorAction SilentlyContinue
if (-not $command) {
    winget install --exact --id restic.restic --scope User `
        --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw 'winget could not install restic.'
    }

    $executable = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet" `
        -Filter restic.exe -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $executable) {
        throw 'restic.exe was not found after installation.'
    }

    $directory = Split-Path -Parent $executable.FullName
    if ($env:GITHUB_PATH) {
        $directory | Add-Content -LiteralPath $env:GITHUB_PATH
    }
    $resticPath = $executable.FullName
}
else {
    $resticPath = $command.Source
}

& $resticPath version
if ($LASTEXITCODE -ne 0) {
    throw 'restic could not be started.'
}


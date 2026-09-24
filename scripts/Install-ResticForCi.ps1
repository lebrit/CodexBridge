[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$command = Get-Command restic -ErrorAction SilentlyContinue
if (-not $command) {
    # windows-2022 does not include WinGet. Use a pinned official release and hash.
    $version = '0.19.1'
    $expectedHash = 'da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d'
    $archiveName = "restic_${version}_windows_amd64.zip"
    $installDirectory = Join-Path $env:RUNNER_TEMP "CodexBridge-restic-$version"
    $archive = Join-Path $env:RUNNER_TEMP $archiveName
    if (Test-Path -LiteralPath $installDirectory) {
        throw "Pinned restic directory already exists: $installDirectory"
    }
    Invoke-WebRequest `
        -Uri "https://github.com/restic/restic/releases/download/v$version/$archiveName" `
        -OutFile $archive
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'Pinned restic archive checksum mismatch.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $installDirectory
    $downloaded = Get-ChildItem -LiteralPath $installDirectory -Filter 'restic*.exe' -File |
        Select-Object -First 1
    if (-not $downloaded) {
        throw 'The pinned restic archive did not contain an executable.'
    }
    $resticPath = Join-Path $installDirectory 'restic.exe'
    Move-Item -LiteralPath $downloaded.FullName -Destination $resticPath
    $directory = $installDirectory
    if ($env:GITHUB_PATH) {
        $directory | Add-Content -LiteralPath $env:GITHUB_PATH
    }
}
else {
    $resticPath = $command.Source
}

& $resticPath version
if ($LASTEXITCODE -ne 0) {
    throw 'restic could not be started.'
}

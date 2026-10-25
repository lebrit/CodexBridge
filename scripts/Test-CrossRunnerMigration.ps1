[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Create', 'Restore')]
    [string]$Mode,

    [Parameter(Mandatory)]
    [string]$FixtureDirectory,

    [string]$ResticExecutable = 'restic'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fixtureRoot = [IO.Path]::GetFullPath($FixtureDirectory)
$restic = (Get-Command $ResticExecutable -ErrorAction Stop).Source
$repository = Join-Path $fixtureRoot 'repository'
$passwordFile = Join-Path $fixtureRoot 'synthetic-password.txt'
$expectedFile = Join-Path $fixtureRoot 'expected.json'

function Invoke-Restic {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

    & $restic @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "restic failed with exit code $LASTEXITCODE."
    }
}

function Get-RelativeHashManifest {
    param([Parameter(Mandatory)][string]$Root)

    @(Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

if ($Mode -eq 'Create') {
    if (Test-Path -LiteralPath $fixtureRoot) {
        throw "Fixture directory already exists: $fixtureRoot"
    }

    $source = Join-Path $fixtureRoot 'source'
    New-Item -ItemType Directory -Path (Join-Path $source 'Projects\alpha\src') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'Projects\beta\docs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'Environment') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'Projects\alpha\src\hello.txt') `
        -Value 'CodexBridge cross-runner migration fixture: alpha' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $source 'Projects\beta\docs\readme.md') `
        -Value '# Synthetic project beta' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $source 'Environment\safe-profile.json') `
        -Value '{"schema":1,"containsSecrets":false}' -Encoding utf8NoBOM

    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    $password = "synthetic-ci-$([Guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $passwordFile -Value $password -Encoding utf8NoBOM -NoNewline

    Invoke-Restic --repo $repository --password-file $passwordFile init
    Push-Location $source
    try {
        Invoke-Restic --repo $repository --password-file $passwordFile backup . --tag codexbridge-cross-runner
    }
    finally {
        Pop-Location
    }
    Invoke-Restic --repo $repository --password-file $passwordFile check --read-data-subset=100%

    $expected = [ordered]@{
        schemaVersion = 1
        fixture = 'synthetic-only'
        sourceComputer = $env:COMPUTERNAME
        sourceImage = $env:ImageOS
        files = Get-RelativeHashManifest -Root $source
    }
    $expected | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $expectedFile -Encoding utf8NoBOM
    Remove-Item -LiteralPath $source -Recurse -Force
    Write-Host "Created encrypted synthetic migration fixture at $fixtureRoot."
    exit 0
}

foreach ($requiredPath in @($repository, $passwordFile, $expectedFile)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Migration fixture is incomplete: $requiredPath"
    }
}

$expected = Get-Content -LiteralPath $expectedFile -Raw | ConvertFrom-Json
if ($expected.fixture -ne 'synthetic-only' -or $expected.schemaVersion -ne 1) {
    throw 'Migration fixture metadata is invalid.'
}
if ($expected.sourceComputer -and $expected.sourceComputer -eq $env:COMPUTERNAME) {
    throw 'Cross-runner test unexpectedly resumed on the source computer.'
}

$restoreRoot = Join-Path $fixtureRoot 'restored-on-destination'
Invoke-Restic --repo $repository --password-file $passwordFile check --read-data-subset=100%
Invoke-Restic --repo $repository --password-file $passwordFile restore latest --target $restoreRoot --verify

$actual = Get-RelativeHashManifest -Root $restoreRoot
$expectedFiles = @($expected.files | ForEach-Object { "$($_.path)|$($_.sha256)" })
$actualFiles = @($actual | ForEach-Object { "$($_.path)|$($_.sha256)" })
if (Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $actualFiles) {
    throw 'Restored file list or SHA-256 hashes do not match the source runner.'
}

# A second pass proves that retrying the destination-side restore is safe.
$probe = Join-Path $restoreRoot 'Projects\alpha\src\hello.txt'
Remove-Item -LiteralPath $probe -Force
Invoke-Restic --repo $repository --password-file $passwordFile restore latest --target $restoreRoot --verify
$retryFiles = @(Get-RelativeHashManifest -Root $restoreRoot | ForEach-Object { "$($_.path)|$($_.sha256)" })
if (Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $retryFiles) {
    throw 'Repeated restore did not reproduce the source fixture.'
}

[ordered]@{
    passed = $true
    sourceComputer = $expected.sourceComputer
    destinationComputer = $env:COMPUTERNAME
    destinationImage = $env:ImageOS
    verifiedFiles = $expectedFiles.Count
    repositoryCheck = '100%'
    repeatedRestore = 'passed'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'migration-report.json') -Encoding utf8NoBOM

Write-Host "Cross-runner restore verified $($expectedFiles.Count) files and a repeated restore."

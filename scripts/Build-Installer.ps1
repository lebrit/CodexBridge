[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$SourceDirectory = '',
    [string]$OutputDirectory = '',
    [switch]$Require
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    Join-Path $repoRoot 'artifacts\publish\win-x64'
} else {
    [IO.Path]::GetFullPath($SourceDirectory)
}
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}

if (-not (Test-Path -LiteralPath (Join-Path $source 'CodexBridge.App.exe'))) {
    throw "Published application was not found: $source"
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'CodexBridge.Agent.exe'))) {
    throw "Published agent was not found: $source"
}

$knownCompilers = @(
    $env:INNO_SETUP_COMPILER,
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compiler = if ($command) {
    $command.Source
} else {
    $knownCompilers | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $compiler) {
    if ($Require) {
        throw 'Inno Setup compiler was not found.'
    }
    Write-Warning 'INSTALLER_SKIPPED=inno-setup-not-found'
    return
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$script = Join-Path $repoRoot 'installer\CodexBridge.iss'
& $compiler "/DAppVersion=$Version" "/DSourceDir=$source" "/DOutputDir=$output" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$setup = Join-Path $output "CodexBridge-$Version-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "Installer output was not found: $setup"
}

$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$setup.sha256"
Set-Content -LiteralPath $hashPath -Value "$hash  $([IO.Path]::GetFileName($setup))" -Encoding utf8
Write-Host "INSTALLER_OK=$setup"
Write-Host "INSTALLER_SHA256=$hash"

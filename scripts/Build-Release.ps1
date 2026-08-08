[CmdletBinding()]
param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $Version = [string]$props.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

& (Join-Path $PSScriptRoot 'Test-PublicSafety.ps1')

$artifacts = Join-Path $repoRoot 'artifacts'
$publish = Join-Path $artifacts 'publish\win-x64'
$resolvedRepo = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$resolvedArtifacts = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
if (-not $resolvedArtifacts.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Artifacts path escaped the repository.'
}
if (Test-Path -LiteralPath $artifacts) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $publish -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet restore .\CodexBridge.sln
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet test .\CodexBridge.sln -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }

    $publishArgs = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", '--output', $publish
    )
    dotnet publish .\src\CodexBridge.App\CodexBridge.App.csproj @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    dotnet publish .\src\CodexBridge.Agent\CodexBridge.Agent.csproj @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }

    Copy-Item -LiteralPath .\README.md -Destination $publish
    Copy-Item -LiteralPath .\LICENSE -Destination $publish

    $zip = Join-Path $artifacts "CodexBridge-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = "$zip.sha256"
    Set-Content -LiteralPath $hashPath -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding utf8

    Write-Host "BUILD_OK=$zip"
    Write-Host "SHA256=$hash"
}
finally {
    Pop-Location
}

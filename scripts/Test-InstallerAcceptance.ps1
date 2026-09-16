[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselineSetupPath,
    [Parameter(Mandatory)]
    [string]$CurrentSetupPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [string]$BaselineVersion = '0.0.0-ci-baseline'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer acceptance is restricted to the disposable GitHub Actions Windows runner.'
}

$baselineSetup = [IO.Path]::GetFullPath($BaselineSetupPath)
$currentSetup = [IO.Path]::GetFullPath($CurrentSetupPath)
foreach ($setup in @($baselineSetup, $currentSetup)) {
    if (-not (Test-Path -LiteralPath $setup)) {
        throw "Installer was not found: $setup"
    }
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexBridge'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexBridge'
if (Test-Path -LiteralPath $installDirectory) {
    throw "Clean-runner precondition failed; install directory already exists: $installDirectory"
}

$setupLog = Join-Path $env:RUNNER_TEMP 'CodexBridge-setup.log'
$upgradeLog = Join-Path $env:RUNNER_TEMP 'CodexBridge-upgrade.log'
$sentinel = Join-Path $dataDirectory 'installer-preservation-test.txt'
$sentinelValue = [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Set-Content -LiteralPath $sentinel -Value $sentinelValue -Encoding utf8

function Invoke-Setup([string]$Path, [string]$LogPath) {
    $arguments = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/CLOSEAPPLICATIONS',
        "/LOG=$LogPath"
    )
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode). See $LogPath"
    }
}

function Assert-Installed([string]$Version) {
    $app = Join-Path $installDirectory 'CodexBridge.App.exe'
    $agent = Join-Path $installDirectory 'CodexBridge.Agent.exe'
    if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $agent)) {
        throw 'Installed application or agent is missing.'
    }

    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
    if (-not $productVersion.StartsWith($Version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected installed version: $productVersion"
    }
}

function Assert-RegisteredVersion([string]$Version) {
    $entry = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        Where-Object { $_.DisplayName -eq 'CodexBridge' } |
        Select-Object -First 1
    $registeredVersion = if ($entry) { [string]$entry.DisplayVersion } else { '<missing>' }
    if ($registeredVersion -ne $Version) {
        throw "Unexpected installer registration version: $registeredVersion"
    }
}

Invoke-Setup $baselineSetup $setupLog
Assert-Installed $ExpectedVersion
Assert-RegisteredVersion $BaselineVersion
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $sentinelValue) {
    throw 'Initial install changed pre-existing user data.'
}

Invoke-Setup $currentSetup $upgradeLog
Assert-Installed $ExpectedVersion
Assert-RegisteredVersion $ExpectedVersion
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $sentinelValue) {
    throw 'The update changed user data.'
}

& (Join-Path $PSScriptRoot 'Test-ReleaseSmoke.ps1') -PublishDirectory $installDirectory

$taskName = 'CodexBridge Hourly Backup'
& "$env:SystemRoot\System32\schtasks.exe" /Create /TN $taskName /TR 'cmd.exe /c exit 0' /SC DAILY /ST 23:59 /F /RL LIMITED | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Could not create the scheduled-task uninstall sentinel.'
}

$uninstaller = Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe' -File | Select-Object -First 1
if (-not $uninstaller) {
    throw 'Uninstaller was not found.'
}
$uninstall = Start-Process -FilePath $uninstaller.FullName -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
) -Wait -PassThru
if ($uninstall.ExitCode -ne 0) {
    throw "Uninstaller failed with exit code $($uninstall.ExitCode)."
}
$installedApp = Join-Path $installDirectory 'CodexBridge.App.exe'
for ($attempt = 0; $attempt -lt 50 -and (Test-Path -LiteralPath $installedApp); $attempt++) {
    Start-Sleep -Milliseconds 200
}
if (Test-Path -LiteralPath $installedApp) {
    throw 'Application binary remained after uninstall.'
}
if (-not (Test-Path -LiteralPath $sentinel)) {
    throw 'Uninstall removed the user data directory.'
}
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $sentinelValue) {
    throw 'Uninstall changed the preserved user data.'
}
& "$env:SystemRoot\System32\schtasks.exe" /Query /TN $taskName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    throw 'Uninstall left the CodexBridge scheduled task behind.'
}

Write-Host "INSTALLER_ACCEPTANCE_OK=$ExpectedVersion"
Write-Host "USER_DATA_PRESERVED=$sentinel"

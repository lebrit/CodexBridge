[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselineSetupPath,
    [Parameter(Mandatory)]
    [string]$CurrentSetupPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [Parameter(Mandatory)]
    [string]$BaselineVersion
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
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer timed out.' }
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
    if (($productVersion -split '\+')[0] -ne $Version) {
        throw "Unexpected installed version: $productVersion"
    }
}

function Assert-RegisteredVersion([string]$Version) {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F573740-5355-4FB5-996B-44A79C6A334C}_is1'
    $entry = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
    $registeredVersion = if ($entry) { [string]$entry.DisplayVersion } else { '<missing>' }
    if ($registeredVersion -ne $Version) {
        throw "Unexpected installer registration version: $registeredVersion"
    }
}

function Get-RegisteredUninstaller {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F573740-5355-4FB5-996B-44A79C6A334C}_is1'
    $entry = Get-ItemProperty -LiteralPath $uninstallKey
    $path = [IO.Path]::GetFullPath(([string]$entry.UninstallString).Trim('"'))
    if ([IO.Path]::GetDirectoryName($path) -ne $installDirectory -or
        [IO.Path]::GetFileName($path) -notmatch '^unins\d+\.exe$' -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Registered uninstaller does not point to this installation.'
    }
    return $path
}

Invoke-Setup $baselineSetup $setupLog
Assert-Installed $BaselineVersion
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

& (Join-Path $PSScriptRoot 'Test-ReleaseSmoke.ps1') -PublishDirectory $installDirectory -ReportDirectory (Join-Path $env:RUNNER_TEMP 'CodexBridge-ui-smoke')

# Reapplying the same installer must also preserve the profile.
Invoke-Setup $currentSetup (Join-Path $env:RUNNER_TEMP 'CodexBridge-reinstall.log')
Assert-Installed $ExpectedVersion
Assert-RegisteredVersion $ExpectedVersion

$taskName = 'CodexBridge Hourly Backup'
$agent = Join-Path $installDirectory 'CodexBridge.Agent.exe'
& "$env:SystemRoot\System32\schtasks.exe" /Create /TN $taskName /TR "`"$agent`" --ci-no-backup" /SC ONCE /SD 12/31/2099 /ST 23:59 /F /RL LIMITED | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Could not create the scheduled-task uninstall sentinel.'
}

$uninstall = Start-Process -FilePath (Get-RegisteredUninstaller) -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\CodexBridge-uninstall.log"
) -WindowStyle Hidden -PassThru
if (-not $uninstall.WaitForExit(120000)) { $uninstall.Kill($true); throw 'Uninstall timed out.' }
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
$global:LASTEXITCODE = 0

# A task with the same name belonging to another copy must survive even after upgrade.
# This also proves the new RunOnceId entry supersedes the old installer's name-only deletion.
Invoke-Setup $baselineSetup (Join-Path $env:RUNNER_TEMP 'CodexBridge-foreign-baseline.log')
Invoke-Setup $currentSetup (Join-Path $env:RUNNER_TEMP 'CodexBridge-foreign-upgrade.log')
& "$env:SystemRoot\System32\schtasks.exe" /Create /TN $taskName /TR 'cmd.exe /c exit 0' /SC ONCE /SD 12/31/2099 /ST 23:59 /F /RL LIMITED | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create foreign task sentinel.' }
$uninstall = Start-Process -FilePath (Get-RegisteredUninstaller) -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\CodexBridge-foreign-uninstall.log"
) -WindowStyle Hidden -PassThru
if (-not $uninstall.WaitForExit(120000)) { $uninstall.Kill($true); throw 'Second uninstall timed out.' }
if ($uninstall.ExitCode -ne 0) { throw 'Second uninstall failed.' }
& "$env:SystemRoot\System32\schtasks.exe" /Query /TN $taskName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Uninstall deleted a task belonging to another application.' }
& "$env:SystemRoot\System32\schtasks.exe" /Delete /TN $taskName /F | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not clean the foreign-task test sentinel.' }
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $sentinelValue) { throw 'Reinstall/uninstall changed user data.' }

Write-Host "INSTALLER_ACCEPTANCE_OK=$ExpectedVersion"
Write-Host "USER_DATA_PRESERVED=$sentinel"

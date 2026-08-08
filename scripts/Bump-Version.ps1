[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
$propsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$current = [Version][string]$props.Project.PropertyGroup.VersionPrefix

$next = switch ($Part) {
    'major' { [Version]::new($current.Major + 1, 0, 0) }
    'minor' { [Version]::new($current.Major, $current.Minor + 1, 0) }
    default { [Version]::new($current.Major, $current.Minor, $current.Build + 1) }
}

$props.Project.PropertyGroup.VersionPrefix = $next.ToString(3)
$settings = [XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [XmlWriter]::Create($propsPath, $settings)
try { $props.Save($writer) } finally { $writer.Dispose() }
Write-Host "Version: $($current.ToString(3)) -> $($next.ToString(3))"

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$forbiddenFiles = @(
    'AGENTS.md',
    'auth.json',
    'state_5.sqlite',
    '.env'
)
$textExtensions = @('.cs', '.csproj', '.xaml', '.ps1', '.md', '.yml', '.yaml', '.json', '.props', '.sln', '.gitignore')
$patterns = @(
    '(?i)[A-Z]:\\Users\\[^\\\s]+',
    '(?i)github_pat_[A-Za-z0-9_]{20,}',
    '(?i)gh[opsu]_[A-Za-z0-9_]{20,}',
    '(?i)-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----',
    '(?i)CURRENT-PC-AUDIT'
)

Push-Location $repoRoot
try {
    $files = @(git ls-files --cached --others --exclude-standard)
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $files) {
        $name = [IO.Path]::GetFileName($relative)
        if ($forbiddenFiles -contains $name) {
            $failures.Add("forbidden file: $relative")
            continue
        }

        if ($relative -eq 'scripts/Test-PublicSafety.ps1') { continue }
        if ($textExtensions -notcontains [IO.Path]::GetExtension($relative)) { continue }

        $fullPath = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop
        foreach ($pattern in $patterns) {
            if ($content -match $pattern) {
                $failures.Add("sensitive pattern: $relative")
                break
            }
        }
    }

    if ($failures.Count -gt 0) {
        $failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
        throw 'Public safety check failed.'
    }

    Write-Host "Public safety check passed for $($files.Count) files."
}
finally {
    Pop-Location
}

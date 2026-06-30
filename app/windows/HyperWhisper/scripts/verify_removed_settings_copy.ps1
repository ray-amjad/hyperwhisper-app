param()

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RemovedApiKeysRoutePatterns = @(
    'Settings\s*(>|&gt;|\u2192)\s*API Keys',
    'Settings\s+to\s+add\s+your\s+API\s+key',
    'API\s+key\s+in\s+Settings',
    'Configuraci\u00F3n\s*(>|&gt;|\u2192)\s*Claves de API',
    '\u8A2D\u5B9A\s*(>|&gt;|\u2192)\s*API\u30AD\u30FC',
    '\u8BBE\u7F6E\s*(>|&gt;|\u2192)\s*API \u5BC6\u94A5'
)

$SearchRoots = @(
    (Join-Path $ProjectRoot "Resources"),
    (Join-Path $ProjectRoot "Services"),
    (Join-Path $ProjectRoot "Models"),
    (Join-Path $ProjectRoot "Views")
)

$Files = Get-ChildItem -LiteralPath $SearchRoots -Recurse -File -Include *.resx,*.cs,*.xaml |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj|Migrations)\\' -and
        $_.Name -ne "Strings.Designer.cs"
    }

$Failures = @()
foreach ($file in $Files) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($pattern in $RemovedApiKeysRoutePatterns) {
        $matches = [regex]::Matches($content, $pattern)
        foreach ($match in $matches) {
            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $relativePath = Resolve-Path -LiteralPath $file.FullName -Relative
            $Failures += "${relativePath}:${line}: $($match.Value)"
        }
    }
}

if ($Failures.Count -gt 0) {
    throw "Removed Settings > API Keys route still appears in user-facing copy:`n$($Failures -join "`n")"
}

Write-Host "Removed settings copy verifier passed."

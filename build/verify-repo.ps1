$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$internalMarker = 'pony' + 'tail'
$vendorMarker = 'ish' + 'ka'
$required = @(
    'src\DaveTheDiverMP\Bootstrap',
    'src\DaveTheDiverMP\Networking',
    'src\DaveTheDiverMP\Lobby',
    'src\DaveTheDiverMP\Gameplay',
    'src\DaveTheDiverMP\Replication'
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $root $path))) {
        throw "Required repository path is missing: $path"
    }
}

$tracked = git -C $root ls-files
$forbiddenTracked = $tracked | Where-Object {
    $_ -match '(^|/)(bin|obj)(/|$)' -or
    $_ -match "(?i)$internalMarker|$vendorMarker"
}
if ($forbiddenTracked) {
    throw "Forbidden tracked paths or markers found:`n$($forbiddenTracked -join "`n")"
}

$source = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -File -Include *.cs,*.md
$markers = $source | Select-String -Pattern "(?i)$internalMarker|$vendorMarker"
if ($markers) {
    throw "Forbidden source markers found:`n$($markers -join "`n")"
}

Write-Host 'Repository hygiene checks passed.'

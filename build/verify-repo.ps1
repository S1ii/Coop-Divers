$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$internalMarker = 'pony' + 'tail'
$vendorMarker = 'ish' + 'ka'
$required = @(
    'src\Bootstrap',
    'src\Networking',
    'src\Lobby',
    'src\Gameplay',
    'src\Replication',
    '.github\workflows\ci.yml',
    '.github\workflows\release.yml',
    'build\package.ps1'
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $root $path))) {
        throw "Required repository path is missing: $path"
    }
}

$tracked = git -C $root ls-files
$forbiddenTracked = $tracked | Where-Object {
    $_ -match '(^|/)(bin|obj|artifacts|coverage|references|game|DAVE THE DIVER)(/|$)' -or
    $_ -match '\.(7z|dll|exe|log|nupkg|pdb|rar|tmp|zip)$' -or
    $_ -match "(?i)$internalMarker|$vendorMarker"
}
if ($forbiddenTracked) {
    throw "Forbidden tracked paths or markers found:`n$($forbiddenTracked -join "`n")"
}

$largeFiles = foreach ($path in $tracked) {
    $file = Join-Path $root $path
    if ((Test-Path $file) -and ((Get-Item $file).Length -gt 1MB)) {
        $path
    }
}
if ($largeFiles) {
    throw "Tracked files over 1MB are not expected in this repository:`n$($largeFiles -join "`n")"
}

$textExtensions = '\.(cs|csproj|md|ps1|sln|txt|yml|yaml|json|gitignore)$'
$textFiles = $tracked | Where-Object { $_ -match $textExtensions } | ForEach-Object { Join-Path $root $_ }
$markers = $textFiles | Select-String -Pattern "(?i)$internalMarker|$vendorMarker"
if ($markers) {
    throw "Forbidden markers found:`n$($markers -join "`n")"
}

$projectileVisuals = Get-Content (Join-Path $root 'src\Replication\ProjectileVisualReplicator.cs') -Raw
if ($projectileVisuals -match '\bParticleSystem\b|CopyParticle|CopyModule|CopyBursts') {
    throw 'ProjectileVisualReplicator must not copy or create particle systems.'
}
$fishReplication = Get-Content (Join-Path $root 'src\Replication\FishReplicator.cs') -Raw
$missionReplication = Get-Content (Join-Path $root 'src\Gameplay\MissionProgressReplicator.cs') -Raw
if ($missionReplication -match 'deferTerminalStates|ShouldApplyTerminalState') {
    throw 'Mission terminal snapshots must apply immediately on clients.'
}
if ($fishReplication -match 'HarmonyPatch\(typeof\(FishAISystem\), nameof\(FishAISystem\.SetHPDamageQTE\)') {
    throw 'SetHPDamageQTE must not be Harmony-patched; IL2CPP re-entry overflows the stack.'
}
[xml]$project = Get-Content (Join-Path $root 'DaveTheDiverMP.csproj')
if ($project.Project.ItemGroup.Reference.Include -contains 'UnityEngine.ParticleSystemModule') {
    throw 'Unused UnityEngine.ParticleSystemModule reference must not be restored.'
}

Write-Host 'Repository hygiene checks passed.'

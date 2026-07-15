param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root "bin\$Configuration\net6.0\DaveTheDiverMP.dll"
if (-not (Test-Path $dll)) {
    throw "Build output is missing: $dll"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) "DaveTheDiverMP-package-$PID"
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'BepInEx\plugins\DaveTheDiverMP') | Out-Null

try {
    Copy-Item $dll (Join-Path $stage 'BepInEx\plugins\DaveTheDiverMP\DaveTheDiverMP.dll')
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
    }
    $archive = Join-Path $OutputDirectory "DaveTheDiverMP-$Version.zip"
    if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    Write-Host "Created $archive"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $stagePath = [IO.Path]::GetFullPath($stage)
    if ($stagePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $stagePath)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

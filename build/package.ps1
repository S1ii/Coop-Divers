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

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) "DaveTheDiverMP-package-$PID-$([Guid]::NewGuid().ToString('N'))"
$pluginDir = Join-Path $stage 'BepInEx\plugins\DaveTheDiverMP'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

try {
    Copy-Item $dll (Join-Path $pluginDir 'DaveTheDiverMP.dll')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $pluginDir 'LICENSE.txt')
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [xml]$project = Get-Content (Join-Path $root 'DaveTheDiverMP.csproj')
        $Version = [string]$project.Project.PropertyGroup.Version
    }
    $Version = $Version.Trim().TrimStart('v')
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
    }
    $archive = Join-Path $OutputDirectory "DaveTheDiverMP-$Version.zip"
    if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $archive
    $checksum = "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $archive)"
    Set-Content -LiteralPath "$archive.sha256" -Value $checksum -Encoding ASCII
    Write-Host "Created $archive"
    Write-Host "Created $archive.sha256"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $stagePath = [IO.Path]::GetFullPath($stage)
    if ($stagePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $stagePath)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

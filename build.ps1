param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$buildDir = Join-Path $repoRoot "build"
$driveMapperPublishDir = Join-Path $repoRoot "DriveMapper\bin\$Configuration\net8.0-windows\win-x64\publish"
$installerPublishDir = Join-Path $repoRoot "Install\bin\$Configuration\net8.0-windows\win-x64\publish"

dotnet publish (Join-Path $repoRoot "DriveMapper\DriveMapper.csproj") -c $Configuration
dotnet publish (Join-Path $repoRoot "Install\Install.csproj") -c $Configuration

$resolvedRepoRoot = (Resolve-Path $repoRoot).Path
if (Test-Path $buildDir) {
    $resolvedBuildDir = (Resolve-Path $buildDir).Path
    if (-not $resolvedBuildDir.StartsWith($resolvedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean build directory outside repository: $resolvedBuildDir"
    }

    Get-ChildItem -LiteralPath $buildDir -Force | Remove-Item -Force -Recurse
}
else {
    New-Item -ItemType Directory -Path $buildDir | Out-Null
}

Copy-Item -LiteralPath (Join-Path $driveMapperPublishDir "DriveMapper.exe") -Destination (Join-Path $buildDir "DriveMapper.exe") -Force
Copy-Item -LiteralPath (Join-Path $installerPublishDir "Install.exe") -Destination (Join-Path $buildDir "Install.exe") -Force

Write-Host "Build output:"
Get-ChildItem -LiteralPath $buildDir -File | Select-Object Name, Length, LastWriteTime

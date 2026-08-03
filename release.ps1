param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "1.3.3",
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$releaseRoot = Join-Path $artifacts "release"
$packageName = "CodexProviderSwitcher-v$Version-win-x64"
$stage = Join-Path $releaseRoot $packageName
$archive = Join-Path $releaseRoot "$packageName.zip"
$checksum = Join-Path $releaseRoot "$packageName.sha256"

$versionProjects = @(
    (Join-Path $root "src\CodexProviderSwitcher\CodexProviderSwitcher.csproj"),
    (Join-Path $root "src\CodexProviderToken\CodexProviderToken.csproj")
)
foreach ($projectPath in $versionProjects) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $versionNode = $project.SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $versionNode -or $versionNode.InnerText -ne $Version) {
        throw "Release version $Version does not match $projectPath."
    }
}

& (Join-Path $root "build.ps1") -DotNet $DotNet

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item `
    -Path (Join-Path $publish "*") `
    -Destination $stage `
    -Recurse `
    -Force

foreach ($requiredFile in @(
    "CodexProviderSwitcher.exe",
    "CodexProviderToken.exe",
    "WebView2Loader.dll",
    "install.ps1",
    "README.md",
    "CHANGELOG.md",
    "LICENSE"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $requiredFile))) {
        throw "Release package is missing $requiredFile."
    }
}

Compress-Archive -LiteralPath $stage -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $packageName.zip" | Set-Content -LiteralPath $checksum -Encoding ascii

Write-Host "Release archive: $archive"
Write-Host "SHA-256: $hash"

param(
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$tokenPublish = Join-Path $artifacts "token"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $argumentLine = ($Arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join " "
    $process = Start-Process `
        -FilePath $DotNet `
        -ArgumentList $argumentLine `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "dotnet failed with exit code $($process.ExitCode): $($Arguments -join ' ')"
    }
}

if (Test-Path -LiteralPath $artifacts) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}

New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path $tokenPublish -Force | Out-Null

Invoke-DotNet @(
    "publish",
    (Join-Path $root "src\CodexProviderSwitcher\CodexProviderSwitcher.csproj"),
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "false",
    "-o",
    $publish
)

Invoke-DotNet @(
    "publish",
    (Join-Path $root "src\CodexProviderToken\CodexProviderToken.csproj"),
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "false",
    "-o",
    $tokenPublish
)

Copy-Item `
    -LiteralPath (Join-Path $tokenPublish "CodexProviderToken.exe") `
    -Destination (Join-Path $publish "CodexProviderToken.exe") `
    -Force

Invoke-DotNet @(
    "run",
    "--project",
    (Join-Path $root "tests\CodexProviderSwitcher.SelfTest\CodexProviderSwitcher.SelfTest.csproj"),
    "-c",
    "Release",
    "--",
    (Join-Path $publish "CodexProviderToken.exe")
)

Write-Host "Publish output: $publish"

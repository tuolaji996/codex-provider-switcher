param(
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$tokenPublish = Join-Path $artifacts "token"
$routerPublish = Join-Path $artifacts "kimi-router"
$linuxRouterPublish = Join-Path $artifacts "kimi-router-linux-x64"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $argumentLine = ($Arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join " "
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $DotNet
    $startInfo.Arguments = $argumentLine
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $root

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Failed to start dotnet."
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        throw "dotnet failed with exit code ${exitCode}: $($Arguments -join ' ')"
    }
}

if (Test-Path -LiteralPath $artifacts) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}

New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path $tokenPublish -Force | Out-Null
New-Item -ItemType Directory -Path $routerPublish -Force | Out-Null
New-Item -ItemType Directory -Path $linuxRouterPublish -Force | Out-Null

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

Invoke-DotNet @(
    "publish",
    (Join-Path $root "src\CodexProviderKimiRouter\CodexProviderKimiRouter.csproj"),
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "false",
    "-o",
    $routerPublish
)

Invoke-DotNet @(
    "publish",
    (Join-Path $root "src\CodexProviderKimiRouter\CodexProviderKimiRouter.csproj"),
    "-c",
    "Release",
    "-r",
    "linux-x64",
    "--self-contained",
    "true",
    "-p:PublishSingleFile=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o",
    $linuxRouterPublish
)

$routerOutput = @(Get-ChildItem -LiteralPath $routerPublish -File)
$routerExecutable = $routerOutput |
    Where-Object { $_.Name -eq "CodexProviderKimiRouter.exe" } |
    Select-Object -First 1
if ($null -eq $routerExecutable) {
    throw "Published Kimi router was not found: $(Join-Path $routerPublish 'CodexProviderKimiRouter.exe')"
}

foreach ($routerSupportFile in @(
    "CodexProviderKimiRouter.dll",
    "CodexProviderKimiRouter.deps.json",
    "CodexProviderKimiRouter.runtimeconfig.json"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $routerPublish $routerSupportFile))) {
        throw "Published Kimi router output is missing $routerSupportFile."
    }
}

# The router is framework-dependent rather than single-file. Keep its
# executable and all router-named sidecars (deps/runtimeconfig/pdb) together
# in the same package as the GUI.
foreach ($routerFile in $routerOutput |
    Where-Object { $_.Name -like "CodexProviderKimiRouter*" }) {
    Copy-Item `
        -LiteralPath $routerFile.FullName `
        -Destination (Join-Path $publish $routerFile.Name) `
        -Force
}

$linuxRouterExecutable = Join-Path $linuxRouterPublish "CodexProviderKimiRouter"
if (-not (Test-Path -LiteralPath $linuxRouterExecutable -PathType Leaf)) {
    throw "Published Linux Kimi router was not found: $linuxRouterExecutable"
}

$packagedLinuxRouterDirectory = Join-Path $publish "linux-x64"
New-Item `
    -ItemType Directory `
    -Path $packagedLinuxRouterDirectory `
    -Force | Out-Null
Copy-Item `
    -LiteralPath $linuxRouterExecutable `
    -Destination (Join-Path $packagedLinuxRouterDirectory "CodexProviderKimiRouter") `
    -Force

$kimiLauncher = Join-Path $root "scripts\codex-provider-kimi-launcher.sh"
if (-not (Test-Path -LiteralPath $kimiLauncher -PathType Leaf)) {
    throw "K3 WSL launcher was not found: $kimiLauncher"
}
Copy-Item `
    -LiteralPath $kimiLauncher `
    -Destination (Join-Path $publish "codex-provider-kimi-launcher.sh") `
    -Force

Copy-Item `
    -LiteralPath (Join-Path $tokenPublish "CodexProviderToken.exe") `
    -Destination (Join-Path $publish "CodexProviderToken.exe") `
    -Force

foreach ($supportFile in @(
    "install.ps1",
    "README.md",
    "CHANGELOG.md",
    "LICENSE"
)) {
    Copy-Item `
        -LiteralPath (Join-Path $root $supportFile) `
        -Destination (Join-Path $publish $supportFile) `
        -Force
}

Invoke-DotNet @(
    "run",
    "--project",
    (Join-Path $root "tests\CodexProviderSwitcher.SelfTest\CodexProviderSwitcher.SelfTest.csproj"),
    "-c",
    "Release",
    "--",
    (Join-Path $publish "CodexProviderToken.exe")
)

Invoke-DotNet @(
    "run",
    "--project",
    (Join-Path $root "tests\CodexProviderKimiRouter.ProtocolTests\CodexProviderKimiRouter.ProtocolTests.csproj"),
    "-c",
    "Release"
)

Write-Host "Publish output: $publish"

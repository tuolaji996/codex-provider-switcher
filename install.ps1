param(
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $packagedExecutable = Join-Path $PSScriptRoot "CodexProviderSwitcher.exe"
    $PublishDirectory = if (Test-Path -LiteralPath $packagedExecutable) {
        $PSScriptRoot
    }
    else {
        Join-Path $PSScriptRoot "artifacts\publish"
    }
}

$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\CodexProviderSwitcher"
$programsRoot = Split-Path -Parent $installDirectory
$operationId = [Guid]::NewGuid().ToString("N")
$stageDirectory = Join-Path $programsRoot "CodexProviderSwitcher.installing-$operationId"
$previousDirectory = Join-Path $programsRoot "CodexProviderSwitcher.previous-$operationId"
$appSource = Join-Path $PublishDirectory "CodexProviderSwitcher.exe"
$brokerSource = Join-Path $PublishDirectory "CodexProviderToken.exe"

if (-not (Test-Path -LiteralPath $appSource)) {
    throw "Published GUI was not found: $appSource"
}

if (-not (Test-Path -LiteralPath $brokerSource)) {
    throw "Published token broker was not found: $brokerSource"
}

New-Item -ItemType Directory -Path $programsRoot -Force | Out-Null
try {
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    Copy-Item `
        -Path (Join-Path $PublishDirectory "*") `
        -Destination $stageDirectory `
        -Recurse `
        -Force

    if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory "CodexProviderSwitcher.exe")) -or
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory "CodexProviderToken.exe"))) {
        throw "The staged installation is incomplete."
    }
}
catch {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    throw
}

Get-Process CodexProviderSwitcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

try {
    if (Test-Path -LiteralPath $installDirectory) {
        Move-Item -LiteralPath $installDirectory -Destination $previousDirectory
    }

    Move-Item -LiteralPath $stageDirectory -Destination $installDirectory
}
catch {
    if (-not (Test-Path -LiteralPath $installDirectory) -and
        (Test-Path -LiteralPath $previousDirectory)) {
        Move-Item -LiteralPath $previousDirectory -Destination $installDirectory
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
}

if (Test-Path -LiteralPath $previousDirectory) {
    Remove-Item -LiteralPath $previousDirectory -Recurse -Force
}

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "Codex Provider Switcher.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDirectory "CodexProviderSwitcher.exe"
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = "Switch Codex between official OpenAI and a third-party provider without splitting chat history."
$shortcut.IconLocation = "$(Join-Path $installDirectory 'CodexProviderSwitcher.exe'),0"
$shortcut.Save()

Write-Host "Installed to: $installDirectory"
Write-Host "Desktop shortcut: $shortcutPath"

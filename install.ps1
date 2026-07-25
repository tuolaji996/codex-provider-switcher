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
$appSource = Join-Path $PublishDirectory "CodexProviderSwitcher.exe"
$brokerSource = Join-Path $PublishDirectory "CodexProviderToken.exe"

if (-not (Test-Path -LiteralPath $appSource)) {
    throw "Published GUI was not found: $appSource"
}

if (-not (Test-Path -LiteralPath $brokerSource)) {
    throw "Published token broker was not found: $brokerSource"
}

Get-Process CodexProviderSwitcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory "*") -Destination $installDirectory -Force

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

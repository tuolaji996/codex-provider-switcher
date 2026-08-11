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
$routerSource = Join-Path $PublishDirectory "CodexProviderKimiRouter.exe"
$webViewLoaderSource = Join-Path $PublishDirectory "WebView2Loader.dll"
$routerSupportFiles = @(
    "CodexProviderKimiRouter.dll",
    "CodexProviderKimiRouter.deps.json",
    "CodexProviderKimiRouter.runtimeconfig.json"
)

function Stop-AndWaitForProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProcessName,
        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    $running = @(
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    )
    if ($running.Count -gt 0) {
        $running |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $remaining = @(
            Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
        )
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$DisplayName is still running. Close it and run the installer again."
}

if (-not (Test-Path -LiteralPath $appSource)) {
    throw "Published GUI was not found: $appSource"
}

if (-not (Test-Path -LiteralPath $brokerSource)) {
    throw "Published token broker was not found: $brokerSource"
}

if (-not (Test-Path -LiteralPath $routerSource)) {
    throw "Published Kimi router was not found: $routerSource"
}

foreach ($routerSupportFile in $routerSupportFiles) {
    $routerSupportPath = Join-Path $PublishDirectory $routerSupportFile
    if (-not (Test-Path -LiteralPath $routerSupportPath)) {
        throw "Published Kimi router support file was not found: $routerSupportPath"
    }
}

if (-not (Test-Path -LiteralPath $webViewLoaderSource)) {
    throw "Published WebView2 loader was not found: $webViewLoaderSource"
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
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory "CodexProviderToken.exe")) -or
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory "CodexProviderKimiRouter.exe")) -or
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory "WebView2Loader.dll"))) {
        throw "The staged installation is incomplete."
    }

    foreach ($routerSupportFile in $routerSupportFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $routerSupportFile))) {
            throw "The staged installation is missing $routerSupportFile."
        }
    }
}
catch {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    throw
}

try {
    # Use exact process names only. The GUI and router are the only processes
    # owned by this installation that can lock the directory being replaced.
    Stop-AndWaitForProcess `
        -ProcessName "CodexProviderSwitcher" `
        -DisplayName "Codex Provider Switcher"
    Stop-AndWaitForProcess `
        -ProcessName "CodexProviderKimiRouter" `
        -DisplayName "Codex Provider Kimi Router"
}
catch {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    throw
}

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

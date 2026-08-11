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
$wslLauncherName = "codex-provider-kimi-launcher.sh"
$linuxRouterRelativePath = "linux-x64\CodexProviderKimiRouter"
$wslLauncherSource = Join-Path $PublishDirectory $wslLauncherName
$linuxRouterSource = Join-Path $PublishDirectory $linuxRouterRelativePath
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

function Convert-ToWslPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WindowsPath
    )

    $fullPath = [IO.Path]::GetFullPath($WindowsPath)
    if ($fullPath.Length -lt 3 -or
        $fullPath[1] -ne ':' -or
        ($fullPath[2] -ne '\' -and $fullPath[2] -ne '/')) {
        throw "The K3 launcher must use an absolute local Windows drive path: $WindowsPath"
    }

    # Match ConfigService.ToWslPath. Calling wslpath through wsl.exe can lose
    # Windows backslashes before wslpath receives the argument, which breaks an
    # in-place upgrade once the previous installation already has a launcher.
    $drive = [char]::ToLowerInvariant($fullPath[0])
    $relative = $fullPath.Substring(3).Replace('\', '/')
    return "/mnt/$drive/$relative"
}

function Invoke-WslKimiLauncher {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LauncherWindowsPath,
        [Parameter(Mandatory = $true)]
        [ValidateSet("--ensure-only", "--stop")]
        [string]$Action
    )

    if (-not (Test-Path -LiteralPath $LauncherWindowsPath -PathType Leaf)) {
        if ($Action -eq "--stop") {
            return
        }

        throw "K3 WSL launcher was not found: $LauncherWindowsPath"
    }

    $launcherWslPath = Convert-ToWslPath -WindowsPath $LauncherWindowsPath
    $process = Start-Process `
        -FilePath "wsl.exe" `
        -ArgumentList @("--exec", "/bin/sh", $launcherWslPath, $Action) `
        -NoNewWindow `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "K3 WSL launcher failed with exit code $($process.ExitCode) ($Action)."
    }
}

function Test-KimiConfigurationActive {
    $configPath = Join-Path $env:USERPROFILE ".codex\config.toml"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        return $false
    }

    $content = Get-Content -LiteralPath $configPath -Raw
    $hasLoopback = $content -match '(?m)^\s*base_url\s*=\s*"http://127\.0\.0\.1:17866/v1"\s*$'
    $hasManagedCatalog = $content -match '(?m)^\s*model_catalog_json\s*=\s*"codex-provider-switcher-kimi-model-catalog\.json"\s*$'
    return $hasLoopback -and $hasManagedCatalog
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

if (-not (Test-Path -LiteralPath $wslLauncherSource -PathType Leaf)) {
    throw "Published K3 WSL launcher was not found: $wslLauncherSource"
}

if (-not (Test-Path -LiteralPath $linuxRouterSource -PathType Leaf)) {
    throw "Published Linux K3 router was not found: $linuxRouterSource"
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
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory $wslLauncherName)) -or
        -not (Test-Path -LiteralPath (Join-Path $stageDirectory $linuxRouterRelativePath)) -or
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

$k3WasActive = Test-KimiConfigurationActive
$oldWslLauncher = Join-Path $installDirectory $wslLauncherName
try {
    # Use exact process names only. The GUI and router are the only processes
    # owned by this installation that can lock the directory being replaced.
    Stop-AndWaitForProcess `
        -ProcessName "CodexProviderSwitcher" `
        -DisplayName "Codex Provider Switcher"
    Stop-AndWaitForProcess `
        -ProcessName "CodexProviderKimiRouter" `
        -DisplayName "Codex Provider Kimi Router"
    Invoke-WslKimiLauncher `
        -LauncherWindowsPath $oldWslLauncher `
        -Action "--stop"
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

    if ($k3WasActive) {
        Invoke-WslKimiLauncher `
            -LauncherWindowsPath (Join-Path $installDirectory $wslLauncherName) `
            -Action "--ensure-only"
    }
}
catch {
    $installFailed = $_
    $newWslLauncher = Join-Path $installDirectory $wslLauncherName
    try {
        Invoke-WslKimiLauncher `
            -LauncherWindowsPath $newWslLauncher `
            -Action "--stop"
    }
    catch {
        # Continue the filesystem rollback even if the failed new router did
        # not stop cleanly; the original installation is still recoverable.
    }

    if (Test-Path -LiteralPath $installDirectory) {
        Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $installDirectory) -and
        (Test-Path -LiteralPath $previousDirectory)) {
        Move-Item -LiteralPath $previousDirectory -Destination $installDirectory
    }

    if ($k3WasActive) {
        try {
            Invoke-WslKimiLauncher `
                -LauncherWindowsPath (Join-Path $installDirectory $wslLauncherName) `
                -Action "--ensure-only"
        }
        catch {
            # Preserve the original installation even if its optional WSL
            # launcher cannot be restarted. The original failure is reported.
        }
    }

    throw $installFailed
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

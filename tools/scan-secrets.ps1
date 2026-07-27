param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$textExtensions = @(
    ".config",
    ".cs",
    ".csproj",
    ".gitignore",
    ".json",
    ".md",
    ".props",
    ".ps1",
    ".targets",
    ".toml",
    ".txt",
    ".xaml",
    ".xml",
    ".yaml",
    ".yml"
)

$patterns = @(
    [pscustomobject]@{
        Name = "OpenAI-style API key"
        Regex = '(?<![A-Za-z0-9])sk-[A-Za-z0-9][A-Za-z0-9_-]{19,}'
    },
    [pscustomobject]@{
        Name = "GitHub token"
        Regex = '(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})'
    },
    [pscustomobject]@{
        Name = "AWS access key"
        Regex = '(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])'
    },
    [pscustomobject]@{
        Name = "Google API key"
        Regex = '(?<![A-Za-z0-9_-])AIza[A-Za-z0-9_-]{35}(?![A-Za-z0-9_-])'
    },
    [pscustomobject]@{
        Name = "Slack token"
        Regex = '(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{20,}'
    },
    [pscustomobject]@{
        Name = "Stripe live secret"
        Regex = '(?<![A-Za-z0-9])sk_live_[A-Za-z0-9]{16,}'
    },
    [pscustomobject]@{
        Name = "JWT"
        Regex = '(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
    },
    [pscustomobject]@{
        Name = "Private key"
        Regex = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
    },
    [pscustomobject]@{
        Name = "Hard-coded credential assignment"
        Regex = '(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token|password)\s*[:=]\s*["''](?!YOUR_|REPLACE_|EXAMPLE|<)[A-Za-z0-9+/_=-]{24,}["'']'
    }
)

$gitCommand = Get-Command "git.exe" -ErrorAction SilentlyContinue
if ($null -eq $gitCommand) {
    $gitFallback = Join-Path $env:ProgramFiles "Git\cmd\git.exe"
    if (Test-Path -LiteralPath $gitFallback -PathType Leaf) {
        $gitPath = $gitFallback
    }
    else {
        throw "Git was not found in PATH or the standard Windows installation directory."
    }
}
else {
    $gitPath = $gitCommand.Source
}

$relativePaths = @(
    & $gitPath -C $root ls-files --cached --others --exclude-standard
)
if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE."
}

if ($relativePaths.Count -eq 0) {
    $excludedDirectoryNames = @(
        ".git",
        ".vs",
        "artifacts",
        "bin",
        "obj"
    )
    $relativePaths = @(
        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object {
                $relativePath = $_.FullName.Substring($root.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
                $pathSegments = $relativePath -split '[\\/]'
                ($pathSegments | Where-Object {
                    $excludedDirectoryNames -contains $_
                }).Count -eq 0
            } |
            ForEach-Object {
                $_.FullName.Substring($root.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
            }
    )
}

$findings = New-Object System.Collections.Generic.List[string]
$scannedFileCount = 0

foreach ($relativePath in $relativePaths) {
    $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
    $fileName = [System.IO.Path]::GetFileName($relativePath)
    if ($textExtensions -notcontains $extension -and
        $fileName -ne ".gitignore") {
        continue
    }

    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        continue
    }

    $content = Get-Content -LiteralPath $path -Raw
    if ([string]::IsNullOrEmpty($content)) {
        continue
    }

    $scannedFileCount++
    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches(
            $content,
            $pattern.Regex,
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        foreach ($match in $matches) {
            $lineNumber = 1 + [regex]::Matches(
                $content.Substring(0, $match.Index),
                '\r?\n').Count
            $findings.Add(
                "${relativePath}:${lineNumber} matched $($pattern.Name).")
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host "ERROR: Potential secrets were found. Matched values are intentionally redacted." `
        -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "ERROR: $finding" -ForegroundColor Red
    }

    throw "Secret scan failed with $($findings.Count) finding(s)."
}

if ($scannedFileCount -eq 0) {
    throw "Secret scan did not inspect any repository text files."
}

Write-Host "Secret scan passed for $scannedFileCount repository text files."

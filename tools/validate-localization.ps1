param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.Substring($root.Length).TrimStart(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

$requiredMarkers = [ordered]@{
    "src\CodexProviderSwitcher.Core\Localizer.cs" = @(
        'public const string ChineseCode = "zh-CN";',
        'public const string EnglishCode = "en-US";'
    )
    "src\CodexProviderSwitcher\MainWindow.xaml.cs" = @(
        "ChangeLanguageAsync(AppLanguage.Chinese)",
        "ChangeLanguageAsync(AppLanguage.English)",
        "private void ApplyLanguage()"
    )
    "src\CodexProviderSwitcher\SetupWizardWindow.xaml.cs" = @(
        "ChangeLanguage(AppLanguage.Chinese)",
        "ChangeLanguage(AppLanguage.English)",
        "private void ApplyLanguage()"
    )
}

$failures = New-Object System.Collections.Generic.List[string]
foreach ($entry in $requiredMarkers.GetEnumerator()) {
    $path = Join-Path $root $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing localization source: $($entry.Key)")
        continue
    }

    $content = Get-Content -LiteralPath $path -Raw
    foreach ($marker in $entry.Value) {
        if (-not $content.Contains($marker)) {
            $failures.Add("$($entry.Key) is missing required marker: $marker")
        }
    }
}

$localizedCallPattern = [regex]::new(
    '(?ms)(?<![\w.])(?:T|F|L|Localizer\.(?:Text|Format)|SetWizardStatus|SetWebViewStatus|StartWebViewSoftTimeout)\s*\(\s*\$?"((?:\\.|[^"\\])*)"\s*,\s*\$?"((?:\\.|[^"\\])*)"',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
$cjkPattern = [regex]::new(
    '[\u3400-\u4DBF\u4E00-\u9FFF]',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
$latinPattern = [regex]::new(
    '[A-Za-z]',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

$pairCounts = @{}
$totalPairs = 0
$sourceRoot = Join-Path $root "src"
$sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter "*.cs"

foreach ($file in $sourceFiles) {
    $relativePath = Get-RelativePath -Path $file.FullName
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $matches = $localizedCallPattern.Matches($content)
    $pairCounts[$relativePath] = $matches.Count
    $totalPairs += $matches.Count

    foreach ($match in $matches) {
        $chinese = $match.Groups[1].Value
        $english = $match.Groups[2].Value
        $lineNumber = 1 + [regex]::Matches(
            $content.Substring(0, $match.Index),
            '\r?\n').Count

        if ([string]::IsNullOrWhiteSpace($chinese) -or
            [string]::IsNullOrWhiteSpace($english)) {
            $failures.Add(
                "${relativePath}:${lineNumber} has an empty localization value.")
            continue
        }

        if ($cjkPattern.IsMatch($chinese) -and
            -not $latinPattern.IsMatch($english)) {
            $failures.Add(
                "${relativePath}:${lineNumber} has Chinese copy without an English translation.")
        }

        if ($cjkPattern.IsMatch($english)) {
            $failures.Add(
                "${relativePath}:${lineNumber} has Chinese characters in the English translation.")
        }
    }
}

$minimumPairCounts = [ordered]@{
    "src\CodexProviderSwitcher\MainWindow.xaml.cs" = 60
    "src\CodexProviderSwitcher\SetupWizardWindow.xaml.cs" = 25
}

foreach ($entry in $minimumPairCounts.GetEnumerator()) {
    $actual = if ($pairCounts.ContainsKey($entry.Key)) {
        $pairCounts[$entry.Key]
    }
    else {
        0
    }

    if ($actual -lt $entry.Value) {
        $failures.Add(
            "$($entry.Key) has $actual literal localization pairs; expected at least $($entry.Value).")
    }
}

if ($totalPairs -lt 100) {
    $failures.Add(
        "Found $totalPairs literal localization pairs; expected at least 100.")
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "ERROR: $failure" -ForegroundColor Red
    }

    throw "Bilingual localization validation failed with $($failures.Count) issue(s)."
}

Write-Host "Validated $totalPairs Chinese/English localization pairs."

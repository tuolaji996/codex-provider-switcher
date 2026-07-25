param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class IconNative
{
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@

$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(13, 17, 23))

$background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Rectangle 18, 18, 220, 220),
    [System.Drawing.Color]::FromArgb(25, 195, 125),
    [System.Drawing.Color]::FromArgb(45, 112, 213),
    45)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(18, 18, 52, 52, 180, 90)
$path.AddArc(186, 18, 52, 52, 270, 90)
$path.AddArc(186, 186, 52, 52, 0, 90)
$path.AddArc(18, 186, 52, 52, 90, 90)
$path.CloseFigure()
$graphics.FillPath($background, $path)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 18)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen, 62, 92, 184, 92)
$graphics.DrawLine($pen, 154, 58, 188, 92)
$graphics.DrawLine($pen, 188, 92, 154, 126)
$graphics.DrawLine($pen, 194, 164, 72, 164)
$graphics.DrawLine($pen, 102, 130, 68, 164)
$graphics.DrawLine($pen, 68, 164, 102, 198)

$iconHandle = $bitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
    $stream = [System.IO.File]::Create($OutputPath)
    try {
        $icon.Save($stream)
    } finally {
        $stream.Dispose()
        $icon.Dispose()
    }
} finally {
    [IconNative]::DestroyIcon($iconHandle) | Out-Null
    $pen.Dispose()
    $path.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host $OutputPath

param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.RectangleF]$Bounds,
        [Parameter(Mandatory = $true)]
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(
        $Bounds.Left,
        $Bounds.Top,
        $diameter,
        $diameter,
        180,
        90)
    $path.AddArc(
        $Bounds.Right - $diameter,
        $Bounds.Top,
        $diameter,
        $diameter,
        270,
        90)
    $path.AddArc(
        $Bounds.Right - $diameter,
        $Bounds.Bottom - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc(
        $Bounds.Left,
        $Bounds.Bottom - $diameter,
        $diameter,
        $diameter,
        90,
        90)
    $path.CloseFigure()
    return $path
}

function New-IconFrame {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode =
            [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode =
            [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $margin = [Math]::Max(1.0, $Size * 0.07)
        $bounds = New-Object System.Drawing.RectangleF(
            [float]$margin,
            [float]$margin,
            [float]($Size - (2 * $margin)),
            [float]($Size - (2 * $margin)))
        $radius = [Math]::Max(2.0, $Size * 0.18)
        $backgroundPath = New-RoundedRectanglePath `
            -Bounds $bounds `
            -Radius $radius
        $backgroundBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 15, 138, 95))
        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)
        }
        finally {
            $backgroundBrush.Dispose()
            $backgroundPath.Dispose()
        }

        $strokeWidth = [Math]::Max(1.5, $Size * 0.075)
        $pen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::White,
            [float]$strokeWidth)
        try {
            $pen.StartCap =
                [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap =
                [System.Drawing.Drawing2D.LineCap]::Round
            $pen.LineJoin =
                [System.Drawing.Drawing2D.LineJoin]::Round

            $left = [float]($Size * 0.27)
            $right = [float]($Size * 0.73)
            $upperY = [float]($Size * 0.38)
            $lowerY = [float]($Size * 0.62)
            $arrowInset = [float]($Size * 0.12)

            $graphics.DrawLine($pen, $left, $upperY, $right, $upperY)
            $graphics.DrawLine(
                $pen,
                $right - $arrowInset,
                $upperY - $arrowInset,
                $right,
                $upperY)
            $graphics.DrawLine(
                $pen,
                $right,
                $upperY,
                $right - $arrowInset,
                $upperY + $arrowInset)

            $graphics.DrawLine($pen, $right, $lowerY, $left, $lowerY)
            $graphics.DrawLine(
                $pen,
                $left + $arrowInset,
                $lowerY - $arrowInset,
                $left,
                $lowerY)
            $graphics.DrawLine(
                $pen,
                $left,
                $lowerY,
                $left + $arrowInset,
                $lowerY + $arrowInset)
        }
        finally {
            $pen.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save(
            $stream,
            [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @(
    foreach ($size in $sizes) {
        [pscustomobject]@{
            Size = $size
            Bytes = New-IconFrame -Size $size
        }
    }
)

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force |
        Out-Null
}

$stream = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) {
            [byte]0
        }
        else {
            [byte]$frame.Size
        }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]([byte[]]$frame.Bytes).Length)
        $writer.Write([uint32]$offset)
        $offset += ([byte[]]$frame.Bytes).Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated $($frames.Count)-size Windows icon: $OutputPath"

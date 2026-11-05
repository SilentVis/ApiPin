# Renders the ApiPin mark (same shapes as src/ApiPin.UI/wwwroot/icon.svg) into a multi-size .ico
# for the desktop app. Each size is drawn from the vector shapes, so small sizes stay crisp.
#
#   pwsh tools/icon/make-icon.ps1 [-Output path.ico] [-PreviewDir folder]

param(
    [string]$Output = (Join-Path $PSScriptRoot '..\..\src\ApiPin.Desktop\Assets\apipin.ico'),
    [string]$PreviewDir
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Rounded square, x=8 y=8 240x240, radius 56, vertical blue gradient.
    $r = 56; $d = $r * 2; $x = 8; $y = 8; $w = 240
    $square = New-Object System.Drawing.Drawing2D.GraphicsPath
    $square.AddArc($x, $y, $d, $d, 180, 90)
    $square.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $square.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90)
    $square.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $square.CloseFigure()
    $top = [System.Drawing.ColorTranslator]::FromHtml('#3b82f6')
    $bottom = [System.Drawing.ColorTranslator]::FromHtml('#1d4ed8')
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 8)), ([System.Drawing.PointF]::new(0, 248)), $top, $bottom
    $g.FillPath($gradient, $square)

    # Pin: M128 218 C112 196 68 150 68 108 A60 60 0 0 1 188 108 C188 150 144 196 128 218 Z
    $pin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pin.AddBezier(128, 218, 112, 196, 68, 150, 68, 108)
    $pin.AddArc(68, 48, 120, 120, 180, 180)
    $pin.AddBezier(188, 108, 188, 150, 144, 196, 128, 218)
    $pin.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $pin)

    # Hole: circle at 128,108 r=24.
    $hole = New-Object System.Drawing.SolidBrush $bottom
    $g.FillEllipse($hole, 104, 84, 48, 48)

    $g.Dispose(); $gradient.Dispose(); $hole.Dispose(); $square.Dispose(); $pin.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Mark $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $bmp.Save((Join-Path $PreviewDir "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$img.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Flush()

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $Output)).Path + '\' + (Split-Path $Output -Leaf), $out.ToArray())
"Wrote $Output ($($out.Length) bytes, sizes: $($sizes -join ', '))"

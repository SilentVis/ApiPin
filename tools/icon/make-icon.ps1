# Renders the ApiPin mark (pin between braces on a dark tile; same shapes as
# src/ApiPin.UI/wwwroot/icon.svg) into a multi-size .ico
# for the desktop app. Each size is drawn from the vector shapes, so small sizes stay crisp.
#
#   pwsh tools/icon/make-icon.ps1 [-Output path.ico] [-PreviewDir folder]

param(
    [string]$Output = (Join-Path $PSScriptRoot '..\..\src\ApiPin.Desktop\Assets\apipin.ico'),
    [string]$PreviewDir
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Gradient([float]$y1, [float]$y2, [string]$from, [string]$to) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, $y1)), ([System.Drawing.PointF]::new(0, $y2)), (Color $from), (Color $to)
}

function Polygon([float[]]$xy) {
    $points = for ($i = 0; $i -lt $xy.Length; $i += 2) { [System.Drawing.PointF]::new($xy[$i], $xy[$i + 1]) }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon([System.Drawing.PointF[]]$points)
    return $path
}

# Adds an SVG quadratic segment (P0 -> control Q -> P2) as the equivalent cubic Bezier.
function AddQuad($path, [float]$x0, [float]$y0, [float]$qx, [float]$qy, [float]$x2, [float]$y2) {
    $path.AddBezier($x0, $y0,
        ($x0 + 2 / 3 * ($qx - $x0)), ($y0 + 2 / 3 * ($qy - $y0)),
        ($x2 + 2 / 3 * ($qx - $x2)), ($y2 + 2 / 3 * ($qy - $y2)),
        $x2, $y2)
}

# One curly brace: open end at x $tip, spine at x $spine, point at x $point.
# Left brace: Brace 88 66 48 ==
#   M88 64 Q66 64 66 90 L66 110 Q66 128 48 128 Q66 128 66 146 L66 166 Q66 192 88 192
function Brace([float]$tip, [float]$spine, [float]$point) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    AddQuad $p $tip 64 $spine 64 $spine 90
    $p.AddLine($spine, 90, $spine, 110)
    AddQuad $p $spine 110 $spine 128 $point 128
    AddQuad $p $point 128 $spine 128 $spine 146
    $p.AddLine($spine, 146, $spine, 166)
    AddQuad $p $spine 166 $spine 192 $tip 192
    return $p
}

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 256-unit canvas.
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Tile: rect 8,8 240x240 rx 52
    $tile = RoundedRect 8 8 240 240 52
    $tileBrush = New-Object System.Drawing.SolidBrush (Color '#1f2329')
    $g.FillPath($tileBrush, $tile)

    # Braces: stroke #e5e7eb width 18, round caps/joins. Small sizes get a slightly
    # heavier stroke so the braces don't fade into the tile.
    $braceWidth = if ($size -le 24) { 22 } else { 18 }
    $bracePen = New-Object System.Drawing.Pen (Color '#e5e7eb'), $braceWidth
    $bracePen.StartCap = 'Round'; $bracePen.EndCap = 'Round'; $bracePen.LineJoin = 'Round'
    foreach ($braces in (Brace 88 66 48), (Brace 168 190 208)) {
        $g.DrawPath($bracePen, $braces)
        $braces.Dispose()
    }

    # Pin, upright, between the braces (SVG: translate(128 128) scale(0.6) translate(-128 -130)).
    $g.TranslateTransform(128, 128)
    $g.ScaleTransform(0.6, 0.6)
    $g.TranslateTransform(-128, -130)

    # Needle: M121 146 L135 146 L131 226 L128 240 L125 226 Z
    $needle = Polygon @(121, 146, 135, 146, 131, 226, 128, 240, 125, 226)
    $needleBrush = New-Object System.Drawing.SolidBrush (Color '#9ca3af')
    $g.FillPath($needleBrush, $needle)

    # Flange: rect 64,114 128x36 rx 18
    $flange = RoundedRect 64 114 128 36 18
    $flangeBrush = Gradient 114 150 '#ef4444' '#b91c1c'
    $g.FillPath($flangeBrush, $flange)

    # Neck: M98 64 L158 64 L150 120 L106 120 Z
    $neck = Polygon @(98, 64, 158, 64, 150, 120, 106, 120)
    $neckBrush = New-Object System.Drawing.SolidBrush (Color '#b91c1c')
    $g.FillPath($neckBrush, $neck)

    # Head: rect 74,20 108x50 rx 25
    $head = RoundedRect 74 20 108 50 25
    $headBrush = Gradient 20 70 '#f87171' '#dc2626'
    $g.FillPath($headBrush, $head)

    # Highlight: ellipse at 106,36 rx 18 ry 8, white at 45%
    $shine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(115, 255, 255, 255))
    $g.FillEllipse($shine, 88, 28, 36, 16)

    foreach ($d in $g, $tile, $tileBrush, $bracePen, $needle, $needleBrush, $flange, $flangeBrush, $neck, $neckBrush, $head, $headBrush, $shine) { $d.Dispose() }
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

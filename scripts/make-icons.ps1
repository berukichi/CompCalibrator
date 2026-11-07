# アプリのアイコン(コンプレッサーの入出力カーブ)を各サイズで描いて Assets と app.ico に出力する。
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\CompCalibrator.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$bg = [System.Drawing.Color]::FromArgb(0x0B, 0x0C, 0x0E)
$grid = [System.Drawing.Color]::FromArgb(0x2A, 0x2E, 0x36)
$lime = [System.Drawing.Color]::FromArgb(0xC6, 0xF4, 0x32)

# (cx, cy) を中心に、一辺 s の正方形へカーブを描く。
function Draw-Mark($g, [double]$cx, [double]$cy, [double]$s, [System.Drawing.Color]$color, [bool]$showGrid) {
    $left = $cx - $s / 2
    $top = $cy - $s / 2
    function Point([double]$x, [double]$y) {
        New-Object System.Drawing.PointF([float]($left + $s * $x), [float]($top + $s * $y))
    }

    if ($showGrid -and $s -ge 64) {
        $pen = New-Object System.Drawing.Pen($grid, [float][Math]::Max(1, $s * 0.012))
        foreach ($t in 0.14, 0.38, 0.62, 0.86) {
            $g.DrawLine($pen, (Point $t 0.14), (Point $t 0.86))
            $g.DrawLine($pen, (Point 0.14 $t), (Point 0.86 $t))
        }
        $pen.Dispose()
    }

    $width = [Math]::Max(1.6, $s * $(if ($s -le 32) { 0.15 } else { 0.1 }))
    $pen = New-Object System.Drawing.Pen($color, [float]$width)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Square
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Square
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    [System.Drawing.PointF[]]$points = @((Point 0.16 0.84), (Point 0.54 0.46), (Point 0.84 0.32))
    $g.DrawLines($pen, $points)
    $pen.Dispose()
}

function New-Canvas([int]$w, [int]$h) {
    $bitmap = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    @($bitmap, $g)
}

function Save-Plated([string]$name, [int]$w, [int]$h, [double]$markScale) {
    $bitmap, $g = New-Canvas $w $h
    $g.Clear($bg)
    Draw-Mark $g ($w / 2) ($h / 2) ([Math]::Min($w, $h) * $markScale) $lime $true
    $g.Dispose()
    $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Save-Unplated([string]$name, [int]$s, [System.Drawing.Color]$color) {
    $bitmap, $g = New-Canvas $s $s
    Draw-Mark $g ($s / 2) ($s / 2) $s $color $false
    $g.Dispose()
    $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

foreach ($scale in 100, 200, 400) {
    $k = $scale / 100
    Save-Plated "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.72
    Save-Plated "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.72
    Save-Plated "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 0.86
}
foreach ($scale in 100, 125, 150, 200, 400) {
    Save-Plated "Square44x44Logo.scale-$scale.png" ([int](44 * $scale / 100)) ([int](44 * $scale / 100)) 0.86
}
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Plated "Square44x44Logo.targetsize-$size.png" $size $size 0.9
    Save-Unplated "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $lime
    Save-Unplated "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $bg
}

# app.ico は PNG をそのまま格納する。
$sizes = 16, 24, 32, 48, 256
$images = foreach ($size in $sizes) { ,[IO.File]::ReadAllBytes((Join-Path $assets "Square44x44Logo.targetsize-$size.png")) }
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = $sizes[$i]
    $writer.Write([byte]($edge % 256)); $writer.Write([byte]($edge % 256)); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $stream.ToArray())
Write-Host "icons written to $assets"

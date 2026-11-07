# アプリのアイコン(ライムの VU メーターパネル)を各サイズで描いて Assets と app.ico に出力する。
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\make-icons.ps1
#
# 図形は 64 単位の方眼で定義し、16 / 24 / 32 / 48px で端がピクセルの境界にそろうようにしている。
# 24px 以下は線がつぶれるので太くする。下部の枠線は 1px に収まる 48px 以上でだけ描く。
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\CompCalibrator.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$ink = [System.Drawing.Color]::FromArgb(0x0B, 0x0C, 0x0E)
$lime = [System.Drawing.Color]::FromArgb(0xC6, 0xF4, 0x32)
$frame = [System.Drawing.Color]::FromArgb(0x5F, 0x75, 0x18)

# (left, top) から一辺 s の正方形へメーターを描く。plated=$false なら下部の枠線を省く。
function Draw-Mark($g, [double]$left, [double]$top, [double]$s, [bool]$plated) {
    $u = $s / 64
    $small = $s -le 24

    if ($plated -and $s -ge 48) {
        # パネルの下半分を囲む細い枠線。
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $radius = 3.5 * $u
        $l = [float]($left + 4.5 * $u); $r = [float]($left + 59.5 * $u)
        $start = [float]($top + 47 * $u); $bottom = [float]($top + 55.5 * $u)
        $path.AddLine($l, $start, $l, [float]($bottom - $radius))
        $path.AddArc($l, [float]($bottom - 2 * $radius), [float](2 * $radius), [float](2 * $radius), 180, -90)
        $path.AddLine([float]($l + $radius), $bottom, [float]($r - $radius), $bottom)
        $path.AddArc([float]($r - 2 * $radius), [float]($bottom - 2 * $radius), [float](2 * $radius), [float](2 * $radius), 90, -90)
        $path.AddLine($r, [float]($bottom - $radius), $r, $start)
        $pen = New-Object System.Drawing.Pen($frame, [float][Math]::Max(1, $u))
        $g.DrawPath($pen, $path)
        $pen.Dispose(); $path.Dispose()
    }

    # パネル本体。上だけ角丸で、底は平ら。
    $panel = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x0 = [float]($left + 4 * $u); $x1 = [float]($left + 60 * $u)
    $y0 = [float]($top + 8 * $u); $y1 = [float]($top + 48 * $u)
    $d = [float](8 * $u)
    $panel.AddArc($x0, $y0, $d, $d, 180, 90)
    $panel.AddArc([float]($x1 - $d), $y0, $d, $d, 270, 90)
    $panel.AddLine($x1, [float]($y0 + $d / 2), $x1, $y1)
    $panel.AddLine($x1, $y1, $x0, $y1)
    $panel.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush($lime)
    $g.FillPath($brush, $panel)
    $panel.Dispose()

    # 目盛りの弧と針。
    $arcWidth = $(if ($small) { 5.5 } else { 4 })
    $needleWidth = $(if ($small) { 5 } else { 3.5 })
    $pen = New-Object System.Drawing.Pen($ink, [float]($arcWidth * $u))
    $g.DrawArc($pen, [float]($left + 12 * $u), [float]($top + 20 * $u), [float](40 * $u), [float](40 * $u), 180, 180)
    $pen.Dispose()
    $pen = New-Object System.Drawing.Pen($ink, [float]($needleWidth * $u))
    $g.DrawLine($pen, [float]($left + 32 * $u), [float]($top + 48 * $u), [float]($left + 45 * $u), [float]($top + 18 * $u))
    $pen.Dispose()

    # 支点。
    $pivot = $(if ($small) { 6 } else { 5 })
    $g.FillEllipse($brush, [float]($left + (32 - $pivot) * $u), [float]($top + (48 - $pivot) * $u), [float](2 * $pivot * $u), [float](2 * $pivot * $u))
    $brush.Dispose()
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
    $g.Clear($ink)
    $s = [Math]::Min($w, $h) * $markScale
    Draw-Mark $g (($w - $s) / 2) (($h - $s) / 2) $s $true
    $g.Dispose()
    $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# タスクバー用の透過版。ライムのパネルは明るい背景でも暗い背景でも読める。
function Save-Unplated([string]$name, [int]$s) {
    $bitmap, $g = New-Canvas $s $s
    Draw-Mark $g 0 0 $s $false
    $g.Dispose()
    $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

foreach ($scale in 100, 200, 400) {
    $k = $scale / 100
    Save-Plated "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.8
    Save-Plated "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.8
    Save-Plated "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 1
}
foreach ($scale in 100, 125, 150, 200, 400) {
    Save-Plated "Square44x44Logo.scale-$scale.png" ([int](44 * $scale / 100)) ([int](44 * $scale / 100)) 1
}
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Plated "Square44x44Logo.targetsize-$size.png" $size $size 1
    Save-Unplated "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size
    Save-Unplated "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size
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

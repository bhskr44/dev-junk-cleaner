# Draws src\app.ico and docs\icon.png (broom + sparkles on a green tile) at all Windows icon sizes.
# Run: powershell -ExecutionPolicy Bypass -File make-icon.ps1
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot
$out = Join-Path $repo 'src\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Draw([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $s / 256.0

    # Rounded tile with a green gradient.
    $r = 56 * $k; $m = 8 * $k; $w = $s - 2 * $m
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), ([Drawing.Color]::FromArgb(255, 52, 211, 153)), ([Drawing.Color]::FromArgb(255, 4, 120, 87))
    $g.FillPath($grad, $path)

    # Broom, tilted: handle from top-right to the bristle head at bottom-left.
    $g.TranslateTransform(128 * $k, 128 * $k); $g.RotateTransform(40); $g.TranslateTransform(-128 * $k, -128 * $k)
    $handle = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 255, 237, 213))
    $g.FillRectangle($handle, 118 * $k, 26 * $k, 20 * $k, 112 * $k)
    $band = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 180, 83, 9))
    $g.FillRectangle($band, 104 * $k, 132 * $k, 48 * $k, 18 * $k)
    $head = New-Object Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon([Drawing.PointF[]]@(
        (New-Object Drawing.PointF (104 * $k), (150 * $k)), (New-Object Drawing.PointF (152 * $k), (150 * $k)),
        (New-Object Drawing.PointF (176 * $k), (228 * $k)), (New-Object Drawing.PointF (80 * $k), (228 * $k))))
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 251, 191, 36))), $head)
    if ($s -ge 32) {
        $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(160, 180, 83, 9)), ([Math]::Max(1, 5 * $k))
        foreach ($x in 112, 128, 144) { $g.DrawLine($pen, $x * $k, 160 * $k, ($x + ($x - 128) * 0.6) * $k, 220 * $k) }
    }
    $g.ResetTransform()

    # Sparkles (only where they stay readable).
    if ($s -ge 24) {
        $white = [Drawing.Brushes]::White
        foreach ($sp in @(@(66, 72, 30), @(200, 194, 20), @(212, 128, 14))) {
            if ($s -lt 48 -and $sp[2] -lt 20) { continue }
            $cx = $sp[0] * $k; $cy = $sp[1] * $k; $a = $sp[2] * $k; $b = $a * 0.28
            $g.FillPolygon($white, [Drawing.PointF[]]@(
                (New-Object Drawing.PointF $cx, ($cy - $a)), (New-Object Drawing.PointF ($cx + $b), ($cy - $b)),
                (New-Object Drawing.PointF ($cx + $a), $cy), (New-Object Drawing.PointF ($cx + $b), ($cy + $b)),
                (New-Object Drawing.PointF $cx, ($cy + $a)), (New-Object Drawing.PointF ($cx - $b), ($cy + $b)),
                (New-Object Drawing.PointF ($cx - $a), $cy), (New-Object Drawing.PointF ($cx - $b), ($cy - $b))))
        }
    }
    $g.Dispose()
    return $bmp
}

# ICO file: header, one directory entry per size, then PNG images.
$pngs = foreach ($s in $sizes) { $b = Draw $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); if ($s -eq 256) { $b.Save((Join-Path $repo 'docs\icon.png')) }; $b.Dispose(); , $ms.ToArray() }
$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Wrote $out"


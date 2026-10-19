# Regenerate the application icon using Windows drawing APIs; no external tools required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/LiveCaptions.Desktop/Assets'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc(($x + $width - $diameter), $y, $diameter, $diameter, 270, 90)
    $path.AddArc(($x + $width - $diameter), ($y + $height - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc($x, ($y + $height - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

$frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    # Supersample each size to keep small taskbar/title-bar edges smooth.
    $canvas = [Drawing.Bitmap]::new(1024, 1024)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4, 4)
    $tile = New-RoundedPath 8 8 240 240 52
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.Point]::new(24, 16), [Drawing.Point]::new(232, 248),
        [Drawing.ColorTranslator]::FromHtml('#1689CF'), [Drawing.ColorTranslator]::FromHtml('#1255A8'))
    $graphics.FillPath($gradient, $tile)
    $bubble = New-RoundedPath 44 59 168 122 23
    $white = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $graphics.FillPath($white, $bubble)
    $graphics.FillPolygon($white, [Drawing.PointF[]]@(
        [Drawing.PointF]::new(73, 170), [Drawing.PointF]::new(73, 207), [Drawing.PointF]::new(114, 170)))
    $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#156BBB'))
    $firstLine = New-RoundedPath 68 91 120 13 6.5
    $secondLine = New-RoundedPath 68 126 84 13 6.5
    $graphics.FillPath($ink, $firstLine)
    $graphics.FillPath($ink, $secondLine)
    $dot = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#24B8C8'))
    $graphics.FillEllipse($dot, 171, 125, 15, 15)

    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $scaled = [Drawing.Graphics]::FromImage($bitmap)
    $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scaled.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scaled.DrawImage($canvas, 0, 0, $size, $size)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $destination 'App.png'), $stream.ToArray()) }
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $scaled.Dispose()
    $bitmap.Dispose()
    $dot.Dispose()
    $secondLine.Dispose()
    $firstLine.Dispose()
    $ink.Dispose()
    $white.Dispose()
    $bubble.Dispose()
    $gradient.Dispose()
    $tile.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
}

$file = [IO.File]::Create((Join-Path $destination 'App.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose() }
Write-Output (Join-Path $destination 'App.ico')

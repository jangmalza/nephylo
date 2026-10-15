param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'),
    [string]$PreviewPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Original SVG geometry is the single source for every size. The intentionally
# small renderer accepts rectangles, circles and polygons for native vector icons.
$sourcePath = Join-Path $PSScriptRoot 'assets/app-icon/usage-widget.svg'
[xml]$artwork = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Get-Coordinate([double]$Value, [int]$Size, [bool]$Snap) {
    $scaled = $Value * $Size / 64.0
    if ($Snap -and $Size -lt 64) { $scaled = [Math]::Round($scaled, [MidpointRounding]::AwayFromZero) }
    return [single]($scaled * 4)
}

function New-IconBitmap([int]$Size) {
    $canvas = [System.Drawing.Bitmap]::new($Size * 4, $Size * 4, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    try {
        foreach ($shape in $artwork.DocumentElement.ChildNodes) {
            if ($shape.LocalName -in @('title', 'desc', '#comment')) { continue }
            if ($shape.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            $snap = $shape.GetAttribute('data-snap') -eq 'pixel'
            $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($shape.GetAttribute('fill')))
            try {
                if ($shape.LocalName -eq 'rect') {
                    $x = Get-Coordinate ([double]$shape.GetAttribute('x')) $Size $snap
                    $y = Get-Coordinate ([double]$shape.GetAttribute('y')) $Size $snap
                    $right = Get-Coordinate ([double]$shape.GetAttribute('x') + [double]$shape.GetAttribute('width')) $Size $snap
                    $bottom = Get-Coordinate ([double]$shape.GetAttribute('y') + [double]$shape.GetAttribute('height')) $Size $snap
                    $width = $right - $x; $height = $bottom - $y
                    $radius = 0
                    if ($shape.HasAttribute('rx')) { $radius = Get-Coordinate ([double]$shape.GetAttribute('rx')) $Size $false }
                    if ($radius -gt 0) {
                        $diameter = [single]($radius * 2)
                        $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
                        $path.AddArc($right - $diameter, $y, $diameter, $diameter, 270, 90)
                        $path.AddArc($right - $diameter, $bottom - $diameter, $diameter, $diameter, 0, 90)
                        $path.AddArc($x, $bottom - $diameter, $diameter, $diameter, 90, 90)
                        $path.CloseFigure()
                    } else { $path.AddRectangle([System.Drawing.RectangleF]::new($x, $y, $width, $height)) }
                } elseif ($shape.LocalName -eq 'circle') {
                    $centerX = [double]$shape.GetAttribute('cx')
                    $centerY = [double]$shape.GetAttribute('cy')
                    $radius = [double]$shape.GetAttribute('r')
                    $left = Get-Coordinate ($centerX - $radius) $Size $snap
                    $top = Get-Coordinate ($centerY - $radius) $Size $snap
                    $right = Get-Coordinate ($centerX + $radius) $Size $snap
                    $bottom = Get-Coordinate ($centerY + $radius) $Size $snap
                    $path.AddEllipse($left, $top, ($right - $left), ($bottom - $top))
                } elseif ($shape.LocalName -eq 'polygon') {
                    $points = @($shape.GetAttribute('points').Trim() -split '\s+' | ForEach-Object {
                        $pair = $_ -split ','
                        [System.Drawing.PointF]::new((Get-Coordinate ([double]$pair[0]) $Size $snap), (Get-Coordinate ([double]$pair[1]) $Size $snap))
                    })
                    $path.AddPolygon([System.Drawing.PointF[]]$points)
                } else { throw ('Unsupported icon shape: ' + $shape.LocalName) }
                $graphics.FillPath($brush, $path)
                if ($shape.HasAttribute('stroke')) {
                    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($shape.GetAttribute('stroke')), (Get-Coordinate ([double]$shape.GetAttribute('stroke-width')) $Size $false))
                    try { $graphics.DrawPath($pen, $path) } finally { $pen.Dispose() }
                }
            } finally { $brush.Dispose(); $path.Dispose() }
        }
        $result = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $outputGraphics = [System.Drawing.Graphics]::FromImage($result)
        try {
            $outputGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $outputGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $outputGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $outputGraphics.DrawImage($canvas, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
        } catch { $result.Dispose(); throw } finally { $outputGraphics.Dispose() }
        return $result
    } finally { $graphics.Dispose(); $canvas.Dispose() }
}

function ConvertTo-IconDib([System.Drawing.Bitmap]$Bitmap) {
    # Small PNG-compressed ICO frames are misread by .NET Framework's native
    # System.Drawing.Icon path. Use the standard XOR bitmap + AND mask instead.
    $size = $Bitmap.Width
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $memory = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($memory)
    try {
        $writer.Write([uint32]40) # BITMAPINFOHEADER
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2)) # XOR color plane + AND transparency mask
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]0) # BI_RGB, uncompressed BGRA
        $writer.Write([uint32]($size * $size * 4 + $maskStride * $size))
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $color = $Bitmap.GetPixel($x, $y)
                if ($color.A -eq 0) {
                    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
                } else {
                    $writer.Write([byte]$color.B); $writer.Write([byte]$color.G); $writer.Write([byte]$color.R)
                }
                $writer.Write([byte]$color.A)
            }
        }
        for ($y = $size - 1; $y -ge 0; $y--) {
            $mask = New-Object byte[] $maskStride
            for ($x = 0; $x -lt $size; $x++) {
                if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                    $index = [int][Math]::Floor($x / 8.0)
                    $mask[$index] = [byte]($mask[$index] -bor (0x80 -shr ($x % 8)))
                }
            }
            $writer.Write([byte[]]$mask)
        }
        return ,$memory.ToArray()
    } finally { $writer.Dispose(); $memory.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $memory = [System.IO.MemoryStream]::new()
    try {
        if ($size -lt 256) {
            $frames += ,([byte[]](ConvertTo-IconDib $bitmap))
        } else {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,$memory.ToArray()
        }
    } finally { $memory.Dispose(); $bitmap.Dispose() }
}
$iconPath = Join-Path $OutputDirectory 'widget.ico'
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($iconPath))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
$large = New-IconBitmap 512
try { $large.Save((Join-Path $OutputDirectory 'widget.png'), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $large.Dispose() }

if ($PreviewPath) {
    $previewDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($PreviewPath))
    New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
    $board = [System.Drawing.Bitmap]::new(840, 400)
    $g = [System.Drawing.Graphics]::FromImage($board)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $font = [System.Drawing.Font]::new('Segoe UI', 19, [System.Drawing.FontStyle]::Bold)
    $smallFont = [System.Drawing.Font]::new('Segoe UI', 10)
    try {
        for ($panel = 0; $panel -lt 2; $panel++) {
            $left = $panel * 420
            $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($(if ($panel -eq 0) { '#101113' } else { '#FAFAF7' })))
            $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($(if ($panel -eq 0) { '#FFFFFF' } else { '#111111' })))
            $muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($(if ($panel -eq 0) { '#AAAAAA' } else { '#686868' })))
            try {
                $g.FillRectangle($background, $left, 0, 420, 400)
                $g.DrawString('Nephylo', $font, $foreground, $left + 32, 25)
                $g.DrawString('Remaining capacity pictogram', $smallFont, $muted, $left + 33, 61)
                $hero = New-IconBitmap 144
                try { $g.DrawImageUnscaled($hero, $left + 138, 102) } finally { $hero.Dispose() }
                $samples = @(16, 24, 32, 48)
                for ($i = 0; $i -lt $samples.Count; $i++) {
                    $size = $samples[$i]; $center = $left + 88 + $i * 80
                    $sample = New-IconBitmap $size
                    try { $g.DrawImageUnscaled($sample, $center - $size / 2, 294 - $size / 2) } finally { $sample.Dispose() }
                    $g.DrawString(($size.ToString() + ' px'), $smallFont, $muted, $center - 18, 327)
                }
            } finally { $background.Dispose(); $foreground.Dispose(); $muted.Dispose() }
        }
        $board.Save([System.IO.Path]::GetFullPath($PreviewPath), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $font.Dispose(); $smallFont.Dispose(); $g.Dispose(); $board.Dispose() }
}
Write-Output ('Generated app icon: ' + $iconPath)

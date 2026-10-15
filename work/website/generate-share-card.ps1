$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Render the canonical app SVG rather than maintaining a second logo geometry.
$iconTemporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('nephylo-share-icon-' + [Guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $PSScriptRoot '../generate-app-icon.ps1') -OutputDirectory $iconTemporaryDirectory | Out-Null
    $logoSource = [Drawing.Bitmap]::new((Join-Path $iconTemporaryDirectory 'widget.png'))
    try { $logo = [Drawing.Bitmap]::new($logoSource) } finally { $logoSource.Dispose() }
} finally {
    # Only remove the two known generated files and the newly created empty directory.
    foreach ($name in @('widget.png', 'widget.ico')) {
        $generatedPath = Join-Path $iconTemporaryDirectory $name
        if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Force }
    }
    if (Test-Path -LiteralPath $iconTemporaryDirectory) { Remove-Item -LiteralPath $iconTemporaryDirectory -Force }
}
$bitmap = New-Object Drawing.Bitmap 1200,630
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
function Brush([string]$hex) { return [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($hex)) }
function Label([string]$text,[float]$size,[float]$x,[float]$y,[string]$color='#F4F4F1',[bool]$bold=$false) {
    $style = if($bold){[Drawing.FontStyle]::Bold}else{[Drawing.FontStyle]::Regular}
    $font=[Drawing.Font]::new('Segoe UI',$size,$style,[Drawing.GraphicsUnit]::Pixel)
    $brush=Brush $color
    try{$graphics.DrawString($text,$font,$brush,$x,$y)}finally{$brush.Dispose();$font.Dispose()}
}
function Rect([string]$color,[float]$x,[float]$y,[float]$w,[float]$h){$brush=Brush $color;try{$graphics.FillRectangle($brush,$x,$y,$w,$h)}finally{$brush.Dispose()}}
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#101113'))
    Rect '#333437' 64 140 1072 1
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($logo, [Drawing.Rectangle]::new(64, 56, 56, 56))
    Label 'Nephylo' 34 130 62 '#F4F4F1' $true
    Label 'nephylo.com' 19 986 81 '#A4A5A7'
    Label 'AI usage.' 70 64 208 '#F4F4F1' $true
    Label 'At a glance.' 70 64 285 '#F4F4F1' $true
    Label 'A small widget for your Windows desktop.' 24 68 395 '#A4A5A7'
    Rect '#F4F4F1' 70 475 265 53
    Label 'All features. Free.' 23 88 484 '#101113' $true
    Label 'Windows 10/11  /  No Nephylo sign-in' 19 68 550 '#A4A5A7'
    Rect '#333437' 727 190 409 361
    Rect '#101113' 729 192 405 357
    Label 'NEPHYLO / PREVIEW' 19 754 216 '#F4F4F1' $true
    Rect '#333437' 756 260 351 1
    Label 'Codex' 27 752 282 '#F4F4F1' $true
    Label 'Weekly remaining' 20 754 348 '#A4A5A7'
    Label '42%' 44 1006 329 '#F4F4F1' $true
    for($i=0;$i -lt 28;$i++){ $color=if($i -lt 12){'#F4F4F1'}else{'#333437'}; Rect $color (756+$i*12.5) 397 10.5 7 }
    Label 'Resets in 6 days' 18 754 419 '#A4A5A7'
    Rect '#333437' 756 474 351 1
    Label 'SAMPLE DATA  /  BLACK THEME' 14 754 500 '#A4A5A7'
    $bitmap.Save((Join-Path $PSScriptRoot 'dist/assets/share-card.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally {$graphics.Dispose();$bitmap.Dispose();$logo.Dispose()}
Write-Output 'Generated assets/share-card.png (1200 x 630)'

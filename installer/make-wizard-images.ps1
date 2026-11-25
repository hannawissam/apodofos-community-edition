# Generates the branded WiX wizard artwork from the application logo (assets/apodofos-512.png).
#   installer/images/banner.bmp  493x58   (top banner of the inner wizard pages; WixUIBannerBmp)
#   installer/images/dialog.bmp  493x312  (welcome and finish pages; WixUIDialogBmp; the left 164 px is the art panel)
# Windows only (System.Drawing). Re-run after changing the logo or the colours; the .bmp files are committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$logoPath = Join-Path $root 'assets\apodofos-512.png'
$outDir = Join-Path $PSScriptRoot 'images'
New-Item -ItemType Directory -Force $outDir | Out-Null
$navy = [Drawing.Color]::FromArgb(0x0F, 0x1B, 0x2D)
$amber = [Drawing.Color]::FromArgb(0xFF, 0xB3, 0x47)
$muted = [Drawing.Color]::FromArgb(0xA9, 0xBA, 0xD3)
$logo = [Drawing.Image]::FromFile($logoPath)

function New-Canvas([int]$w, [int]$h, [Drawing.Color]$bg) {
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $g.Clear($bg)
    return @{ Bmp = $bmp; G = $g }
}
function Draw-Logo($g, [int]$x, [int]$y, [int]$size) { $g.DrawImage($logo, (New-Object Drawing.Rectangle $x, $y, $size, $size)) }

# banner: white, logo at the right (the dialog title text is drawn by Windows Installer on the left), thin brand line below
$c = New-Canvas 493 58 ([Drawing.Color]::White)
Draw-Logo $c.G 437 5 48
$brush = New-Object Drawing.SolidBrush $navy; $c.G.FillRectangle($brush, 0, 56, 493, 2)
$c.Bmp.Save((Join-Path $outDir 'banner.bmp'), [Drawing.Imaging.ImageFormat]::Bmp); $c.G.Dispose(); $c.Bmp.Dispose()

# welcome/finish: navy art panel (164 px) with logo and wordmark, white elsewhere
$c = New-Canvas 493 312 ([Drawing.Color]::White)
$c.G.FillRectangle($brush, 0, 0, 164, 312)
Draw-Logo $c.G 26 58 112
$f1 = New-Object Drawing.Font 'Segoe UI Semibold', 15, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$f2 = New-Object Drawing.Font 'Segoe UI', 10, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'
$c.G.DrawString('Apodofos', $f1, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 182, 164, 26), $sf)
$c.G.DrawString('Performance Report Analyzer', $f2, (New-Object Drawing.SolidBrush $muted), (New-Object Drawing.RectangleF 0, 210, 164, 18), $sf)
$c.G.FillRectangle((New-Object Drawing.SolidBrush $amber), 62, 240, 40, 3)
$c.Bmp.Save((Join-Path $outDir 'dialog.bmp'), [Drawing.Imaging.ImageFormat]::Bmp); $c.G.Dispose(); $c.Bmp.Dispose()
$logo.Dispose()
Get-ChildItem $outDir | ForEach-Object { '{0}  {1} bytes' -f $_.Name, $_.Length }

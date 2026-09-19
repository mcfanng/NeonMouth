param([string]$LogoPath, [string]$Out)
Add-Type -AssemblyName System.Drawing
$size = 1024
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)

# Keep everything inside the central circle Android 12+ leaves visible (~2/3 of the width).
$img = [System.Drawing.Image]::FromFile($LogoPath)
# Logo art occupies roughly x 95-915, y 290-750 of the 1024 source; crop to it.
$src = New-Object System.Drawing.Rectangle 90, 285, 830, 470
$w = 560; $h = [int]($w * 470 / 830)
$dst = New-Object System.Drawing.Rectangle ([int](($size - $w) / 2)), 290, $w, $h
$g.DrawImage($img, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)

$font = New-Object System.Drawing.Font 'Segoe UI', 88, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$rect = New-Object System.Drawing.RectangleF 0, ($dst.Bottom + 20), $size, 130
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddString('NeonMouth', $font.FontFamily, [int]$font.Style, $font.Size, $rect, $fmt)
# Purple glow, then white core, matching the logo's neon style.
foreach ($pw in 22, 14, 8) {
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 138, 43, 226)), $pw
  $pen.LineJoin = 'Round'; $g.DrawPath($pen, $path)
}
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 125, 40, 215)), 4
$g.DrawPath($pen, $path)
$g.FillPath([System.Drawing.Brushes]::White, $path)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $img.Dispose()

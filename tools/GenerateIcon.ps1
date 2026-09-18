param([string]$OutputPath)

Add-Type -AssemblyName System.Drawing.Common
$folder = [System.IO.Path]::GetDirectoryName($OutputPath)
[System.IO.Directory]::CreateDirectory($folder) | Out-Null
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$radius = 52
$path.AddArc(12, 12, $radius, $radius, 180, 90)
$path.AddArc(192, 12, $radius, $radius, 270, 90)
$path.AddArc(192, 192, $radius, $radius, 0, 90)
$path.AddArc(12, 192, $radius, $radius, 90, 90)
$path.CloseFigure()
$brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(24, 20), [System.Drawing.Point]::new(232, 236), [System.Drawing.Color]::FromArgb(255, 0, 120, 212), [System.Drawing.Color]::FromArgb(255, 0, 78, 146))
$graphics.FillPath($brush, $path)
$font = [System.Drawing.Font]::new('Segoe UI', 144, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$format = [System.Drawing.StringFormat]::new()
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$graphics.DrawString('X', $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, -4, 256, 256), $format)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create($OutputPath)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$font.Dispose()
$brush.Dispose()
$path.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

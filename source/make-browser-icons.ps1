$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'chrome-extension\icons'
New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
foreach ($size in @(16,32,48,128)) {
    $bitmap = New-Object Drawing.Bitmap($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(($size / 32.0),($size / 32.0))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0,0,14,14,180,90); $path.AddArc(18,0,14,14,270,90); $path.AddArc(18,18,14,14,0,90); $path.AddArc(0,18,14,14,90,90); $path.CloseFigure()
    $brush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#0A7A70'))
    $pen = New-Object Drawing.Pen([Drawing.Color]::White,2.2)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.FillPath($brush,$path)
    $graphics.DrawBezier($pen,6,11,10,6,14,14,18,10)
    $graphics.DrawBezier($pen,18,10,21,7,23,9,26,10)
    $graphics.DrawLine($pen,16,14,16,25); $graphics.DrawLine($pen,11,20,16,25); $graphics.DrawLine($pen,16,25,21,20)
    $bitmap.Save((Join-Path $iconDir "$size.png"),[Drawing.Imaging.ImageFormat]::Png)
    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

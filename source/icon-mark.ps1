# The Flow mark (wave over a down arrow) on a square cobalt tile, shared by make-app-icon.ps1 and
# make-browser-icons.ps1. Afiş ızgarası: square corners, the cobalt of the window's rail (Theme.cs Light "Accent").
Add-Type -AssemblyName System.Drawing
function New-FlowMark([int]$Size) {
    $bitmap = New-Object Drawing.Bitmap($Size, $Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.ScaleTransform(($Size / 32.0), ($Size / 32.0))
    $brush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#1F3FD1'))
    $graphics.FillRectangle($brush, 0, 0, 32, 32)
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, 2.4)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawBezier($pen, 6, 11, 10, 6, 14, 14, 18, 10)
    $graphics.DrawBezier($pen, 18, 10, 21, 7, 23, 9, 26, 10)
    $graphics.DrawLine($pen, 16, 14, 16, 25); $graphics.DrawLine($pen, 11, 20, 16, 25); $graphics.DrawLine($pen, 16, 25, 21, 20)
    $pen.Dispose(); $brush.Dispose(); $graphics.Dispose()
    return $bitmap
}

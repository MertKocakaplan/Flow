# Writes source\app.ico (16, 32, 48, 64, 128, 256 px, PNG entries) from the shared Flow mark.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'icon-mark.ps1')
$sizes = @(16, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-FlowMark $size
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += , $stream.ToArray()
    $stream.Dispose(); $bitmap.Dispose()
}
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($out)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([Byte]$edge); $writer.Write([Byte]$edge); $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
$writer.Dispose()
Write-Output "Wrote app.ico ($($sizes.Count) sizes)"

$ErrorActionPreference = 'Stop'
# Extension toolbar icons from the shared Flow mark (icon-mark.ps1); build-discovery.ps1 copies them to Opera.
. (Join-Path $PSScriptRoot 'icon-mark.ps1')
$iconDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'chrome-extension\icons'
New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
foreach ($size in @(16,32,48,128)) {
    $bitmap = New-FlowMark $size
    $bitmap.Save((Join-Path $iconDir "$size.png"),[Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}
Write-Output "Wrote extension icons to $iconDir"

param([string]$OutputDir = '')
$ErrorActionPreference = 'Stop'
if (-not $OutputDir) { $OutputDir = Split-Path -Parent $PSScriptRoot }
& (Join-Path $PSScriptRoot 'build-discovery.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -OutputName 'Flow-2.2.exe' -OutputDir $OutputDir
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$nativeArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/codepage:65001',"/out:$OutputDir\Flow.BrowserHost.exe",'/reference:System.dll','/reference:System.Core.dll','/reference:System.Web.Extensions.dll',(Join-Path $PSScriptRoot 'BrowserProtocol.cs'),(Join-Path $PSScriptRoot 'native\Main.cs'))
& (Join-Path $framework 'csc.exe') @nativeArgs
if ($LASTEXITCODE -ne 0) { throw 'Native host compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Flow.exe.config') -Destination (Join-Path $OutputDir 'Flow.BrowserHost.exe.config') -Force
Write-Output "Built browser integration in $OutputDir"

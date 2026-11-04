# Builds Flow.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK or NuGet needed.
param([ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$')][string]$OutputName = 'Flow.exe', [string]$OutputDir = '')
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not $OutputDir) { $OutputDir = Split-Path -Parent $PSScriptRoot }
$csc = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Web.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',"/out:$OutputDir\$OutputName","/win32manifest:$PSScriptRoot\app.manifest")
foreach ($xaml in Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.xaml) { $compilerArgs += "/resource:$($xaml.FullName),$($xaml.Name)" }
if (Test-Path -LiteralPath "$PSScriptRoot\app.ico") { $compilerArgs += "/win32icon:$PSScriptRoot\app.ico"; $compilerArgs += "/resource:$PSScriptRoot\app.ico,app.ico" }
foreach ($ref in $refs) { $compilerArgs += '/reference:' + (Join-Path $framework $ref) }
$compilerArgs += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Flow.exe.config') -Destination (Join-Path $OutputDir ($OutputName + '.config')) -Force
Write-Output "Built: $OutputDir\$OutputName"

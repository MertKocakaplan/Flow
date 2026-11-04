# Builds and runs the automated checks. Tests run next to the real tools folder but with isolated data folders.
# -Live also runs the small set of network tests (real sites; results depend on access from this connection).
param([switch]$Live, [switch]$Ui)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root '.test'
# Output of the previous run only (screenshots, isolated data folders, test downloads).
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $root 'FlowTests.exe'
$refs = @('System.dll','System.Core.dll','System.Web.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/main:FlowTests.Program',"/out:$exe","/win32manifest:$PSScriptRoot\app.manifest")
foreach ($xaml in Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.xaml) { $compilerArgs += "/resource:$($xaml.FullName),$($xaml.Name)" }
if (Test-Path -LiteralPath "$PSScriptRoot\app.ico") { $compilerArgs += "/resource:$PSScriptRoot\app.ico,app.ico" }
foreach ($ref in $refs) { $compilerArgs += '/reference:' + (Join-Path $framework $ref) }
$compilerArgs += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.cs | ForEach-Object { $_.FullName })
$compilerArgs += (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter *.cs | ForEach-Object { $_.FullName })
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$env:FLOW_TEST_OUT = $out
$env:FLOW_TEST_FIXTURES = Join-Path $PSScriptRoot 'tests\fixtures'
$modes = @()
if ($Live) { $modes += 'live' }
if ($Ui) { $modes += 'ui' }
& $exe @modes
$code = $LASTEXITCODE
Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
exit $code

param([switch]$Uninstall, [switch]$Check, [string]$PackageRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $PackageRoot) { $PackageRoot = Split-Path -Parent $PSScriptRoot }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$hostName = 'com.flow.downloader'
$hostFile = Join-Path $PackageRoot 'Flow.BrowserHost.exe'
$manifestFile = Join-Path $PackageRoot 'flow.native.json'
$keyPath = 'Software\Google\Chrome\NativeMessagingHosts\' + $hostName
# Use the 32-bit view explicitly: Chrome checks this view first, even on 64-bit Windows.
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
try {
    if ($Uninstall) {
        $existing = $registry.OpenSubKey($keyPath)
        $registered = if ($existing) { $existing.GetValue('') } else { $null }
        if ($existing) { $existing.Close() }
        if ($registered -and [IO.Path]::GetFullPath($registered) -ne $manifestFile) { throw 'Another Flow package owns this registration. Run uninstall from that package.' }
        if ($registered) { $registry.DeleteSubKey($keyPath, $false) }
        Write-Output 'Flow browser connection removed. Downloads and settings are unchanged.'
        return
    }
    $extension = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PackageRoot 'chrome-extension\manifest.json') | ConvertFrom-Json
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Convert]::FromBase64String($extension.key)) } finally { $sha.Dispose() }
    $hex = -join ($hash[0..15] | ForEach-Object { $_.ToString('x2') })
    $extensionId = -join ($hex.ToCharArray() | ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_,16)) })
    if ($extensionId -ne 'eidibhbenooohggnhdeblgfdclahhdnd') { throw 'The bundled extension identity does not match the native host.' }
    foreach ($required in @($hostFile, (Join-Path $PackageRoot 'Flow-2.2.exe'))) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing application file: $required" } }
    $manifest = @{ name=$hostName; description='Flow browser companion'; path=$hostFile; type='stdio'; allowed_origins=@("chrome-extension://$extensionId/") }
    if ($Check) {
        Write-Output "Package verified. Extension ID: $extensionId"
        Write-Output ($manifest | ConvertTo-Json)
        return
    }
    [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $key = $registry.CreateSubKey($keyPath)
    try { $key.SetValue('', $manifestFile, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Close() }
    Write-Output "Flow browser connection installed for this user. Extension ID: $extensionId"
    Write-Output "Load unpacked extension folder: $(Join-Path $PackageRoot 'chrome-extension')"
} finally { $registry.Close() }

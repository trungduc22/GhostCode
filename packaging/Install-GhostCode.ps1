[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\GhostCode"),
    [switch]$CreateDesktopShortcut,
    [switch]$SkipShortcuts
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @(
    (Join-Path $scriptRoot "app"),
    (Resolve-Path -Path (Join-Path $scriptRoot "..\artifacts\Release\GhostCode-win-x64") -ErrorAction SilentlyContinue | ForEach-Object { $_.Path })
) | Where-Object { $_ -and (Test-Path (Join-Path $_ "GhostCode.App.exe")) }

$sourceDir = $candidates | Select-Object -First 1
if (-not $sourceDir) {
    throw "GhostCode portable payload (GhostCode.App.exe) not found next to installer or in artifacts\Release\GhostCode-win-x64."
}

Write-Host "[1/4] Stopping running GhostCode instances (if any)..."
Get-Process -Name "GhostCode.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Write-Host "[2/4] Copying GhostCode application files to '$InstallDir' (preserving all user archives & settings)..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir "*") -Destination $InstallDir -Recurse -Force

# Stage Chromium & Firefox extension bundles if available alongside source or repo root
$appDataRoot = Join-Path $env:LOCALAPPDATA "GhostCode"
New-Item -ItemType Directory -Force -Path (Join-Path $appDataRoot "Extensions") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $appDataRoot "NativeMessaging") | Out-Null

$repoRoot = Split-Path -Parent $scriptRoot
foreach ($pair in @(
    @{ Name = "browser-extension"; Sub = "chromium" },
    @{ Name = "browser-extension-firefox"; Sub = "firefox" }
)) {
    $srcExt = Join-Path $sourceDir $pair.Name
    if (-not (Test-Path $srcExt)) { $srcExt = Join-Path $repoRoot $pair.Name }
    if (Test-Path $srcExt) {
        $destExt = Join-Path (Join-Path $appDataRoot "Extensions") $pair.Sub
        New-Item -ItemType Directory -Force -Path $destExt | Out-Null
        Get-ChildItem -Path $srcExt -File | Where-Object { $_.Name -ne "pairing.generated.json" } | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $destExt $_.Name) -Force
        }
    }
}

Write-Host "[3/4] Registering Native Messaging Host manifests (HKCU)..."
$exePath = Join-Path $InstallDir "GhostCode.App.exe"
$crManifest = Join-Path $appDataRoot "NativeMessaging\com.ghostcode.bridge.chromium.json"
$ffManifest = Join-Path $appDataRoot "NativeMessaging\com.ghostcode.bridge.firefox.json"

@{
    name = "com.ghostcode.bridge"
    description = "GhostCode Authenticated Native Messaging Bridge"
    path = $exePath
    type = "stdio"
    allowed_origins = @("chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/")
} | ConvertTo-Json -Depth 4 | Set-Content -Path $crManifest -Encoding UTF8

@{
    name = "com.ghostcode.bridge"
    description = "GhostCode Native Messaging Bridge (Firefox)"
    path = $exePath
    type = "stdio"
    allowed_extensions = @("ghostcode-bridge@trungduc22.github.io")
} | ConvertTo-Json -Depth 4 | Set-Content -Path $ffManifest -Encoding UTF8

foreach ($reg in @(
    @{ Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge"; Path = $crManifest },
    @{ Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge"; Path = $crManifest },
    @{ Key = "HKCU:\Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge"; Path = $crManifest },
    @{ Key = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge"; Path = $ffManifest }
)) {
    New-Item -Path $reg.Key -Force | Out-Null
    Set-ItemProperty -Path $reg.Key -Name "(default)" -Value $reg.Path
}

if (-not $SkipShortcuts) {
    Write-Host "[4/4] Creating Start Menu shortcut..."
    $ws = New-Object -ComObject WScript.Shell
    $ico = Join-Path $InstallDir "Assets\app.ico"
    $startMenu = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\GhostCode.lnk"
    $sc = $ws.CreateShortcut($startMenu)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $InstallDir
    if (Test-Path $ico) { $sc.IconLocation = $ico }
    $sc.Description = "GhostCode Code Companion & Cloud Archive"
    $sc.Save()

    if ($CreateDesktopShortcut) {
        $desk = Join-Path ([Environment]::GetFolderPath("Desktop")) "GhostCode.lnk"
        $dsc = $ws.CreateShortcut($desk)
        $dsc.TargetPath = $exePath
        $dsc.WorkingDirectory = $InstallDir
        if (Test-Path $ico) { $dsc.IconLocation = $ico }
        $dsc.Save()
    }
}
else {
    Write-Host "[4/4] Skipping shortcuts (-SkipShortcuts specified)."
}

Write-Host "GhostCode installed cleanly to '$InstallDir'. User data in '$appDataRoot' was preserved."

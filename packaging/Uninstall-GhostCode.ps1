[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\GhostCode"),
    [switch]$PurgeUserData
)

$ErrorActionPreference = "Stop"

Write-Host "[1/4] Stopping running GhostCode instances (if any)..."
Get-Process -Name "GhostCode.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Write-Host "[2/4] Removing application binaries from '$InstallDir'..."
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

Write-Host "[3/4] Removing shortcuts and Native Messaging registrations..."
$startMenu = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\GhostCode.lnk"
$desk = Join-Path ([Environment]::GetFolderPath("Desktop")) "GhostCode.lnk"
if (Test-Path $startMenu) { Remove-Item $startMenu -Force }
if (Test-Path $desk) { Remove-Item $desk -Force }

foreach ($reg in @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge",
    "HKCU:\Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge",
    "HKCU:\Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge"
)) {
    if (Test-Path $reg) { Remove-Item $reg -Recurse -Force }
}

$appDataRoot = Join-Path $env:LOCALAPPDATA "GhostCode"
if ($PurgeUserData) {
    Write-Host "[4/4] Purging local GhostCode app data ('$appDataRoot') as requested..."
    if (Test-Path $appDataRoot) { Remove-Item -Path $appDataRoot -Recurse -Force }
}
else {
    Write-Host "[4/4] Preserved local GhostCode settings/database ('$appDataRoot') and all user source/GhostCodeArchive folders."
}

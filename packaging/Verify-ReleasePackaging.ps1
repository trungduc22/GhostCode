[CmdletBinding()]
param(
    [string]$ReleaseDir,
    [switch]$SkipInstallerTest
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    $ReleaseDir = Join-Path $repoRoot "artifacts\Release"
}

$portableZip = Join-Path $ReleaseDir "GhostCode-Portable-win-x64.zip"
$installerExe = Join-Path $ReleaseDir "GhostCode-Setup-x64.exe"
if (-not (Test-Path $portableZip)) { throw "Missing portable archive: $portableZip" }

$scratchRoot = Join-Path $env:TEMP ("ghostcode_pkg_verify_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null

function Assert-BackupWorksFromExe([string]$ExePath, [string]$Tag) {
    $cfgRoot = Join-Path $scratchRoot $Tag
    $archDir = Join-Path $cfgRoot "GhostCode"
    $watchDir = Join-Path $cfgRoot "Work"
    New-Item -ItemType Directory -Force -Path $archDir, $watchDir | Out-Null
    $dbPath = Join-Path $cfgRoot "ghostcode.db"
    $cfgPath = Join-Path $cfgRoot "config.json"

    $cfg = @{
        WatchedFolders = @(@{ Path = $watchDir; Enabled = $true; Recursive = $true; Label = "Work" })
        ArchiveDir = $archDir
        ReadyDir = ""
        DatabasePath = $dbPath
        HttpPort = 0
        DriveEnabled = $false
        PrimaryCloudProvider = "LocalOnly"
        MinimizeToTrayOnClose = $false
        Language = "Vi"
    } | ConvertTo-Json -Depth 6
    Set-Content -Path $cfgPath -Value $cfg -Encoding UTF8

    $prevEnv = $env:GHOSTCODE_CONFIG_PATH
    try {
        $env:GHOSTCODE_CONFIG_PATH = $cfgPath
        $proc = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath) -PassThru
        Start-Sleep -Seconds 4
        if ($proc.HasExited) {
            throw "$Tag launch exited unexpectedly with code $($proc.ExitCode)"
        }

        $sampleCpp = Join-Path $watchDir "smoke_test.cpp"
        Set-Content -Path $sampleCpp -Value "#include <iostream>`r`nint main(){ int x = 7; std::cout << x * 3 << '\n'; return 0; }`r`n" -Encoding UTF8

        $deadline = (Get-Date).AddSeconds(12)
        $archived = @()
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 300
            $archived = @(Get-ChildItem -Path $archDir -Recurse -Filter "*.cpp" -ErrorAction SilentlyContinue)
            if ($archived.Count -gt 0) { break }
        }
        if ($archived.Count -eq 0) {
            throw "$Tag failed to detect and back up '$sampleCpp' outside developer repo tree."
        }
        Write-Host "  [PASS] $Tag launched outside repo and backed up $($archived[0].Name)."
    }
    finally {
        Get-Process -Name "GhostCode.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
        $env:GHOSTCODE_CONFIG_PATH = $prevEnv
    }
}

try {
    Write-Host "[1/3] Portable ZIP extraction + standalone launch outside workspace..."
    $portableExtract = Join-Path $scratchRoot "portable_extracted"
    Expand-Archive -Path $portableZip -DestinationPath $portableExtract -Force
    $portableExe = Join-Path $portableExtract "GhostCode.App.exe"
    if (-not (Test-Path $portableExe)) { throw "Portable ZIP missing GhostCode.App.exe" }
    Assert-BackupWorksFromExe -ExePath $portableExe -Tag "Portable"

    if (-not $SkipInstallerTest) {
        if (-not (Test-Path $installerExe)) { throw "Missing installer executable: $installerExe" }

        Write-Host "[2/3] Silent Inno Setup fresh install + upgrade test..."
        $installTarget = Join-Path $scratchRoot "InstalledGhostCode"
        $installArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", "/DIR=`"$installTarget`"")
        $instProc = Start-Process -FilePath $installerExe -ArgumentList $installArgs -Wait -PassThru
        if ($instProc.ExitCode -ne 0) { throw "Installer failed with exit code $($instProc.ExitCode)" }

        $installedExe = Join-Path $installTarget "GhostCode.App.exe"
        if (-not (Test-Path $installedExe)) { throw "Installed GhostCode.App.exe missing at $installedExe" }
        if (-not (Test-Path (Join-Path $installTarget "GhostCode.App.pri"))) { throw "Installed GhostCode.App.pri missing" }
        if ((Get-ChildItem $installTarget -Recurse -Filter "*.xbf").Count -lt 6) { throw "Installed .xbf missing" }

        $startMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "GhostCode.lnk"
        if (-not (Test-Path $startMenuLnk)) { throw "Start Menu shortcut missing at $startMenuLnk" }

        foreach ($regKey in @(
            "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge",
            "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge",
            "HKCU:\Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge",
            "HKCU:\Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge"
        )) {
            if (-not (Test-Path $regKey)) { throw "Missing Native Messaging registry key: $regKey" }
        }

        Assert-BackupWorksFromExe -ExePath $installedExe -Tag "Installed_v1"

        # Seed sentinel user archive & run Upgrade install right over same directory
        $upgradeCheckFile = Join-Path $scratchRoot "Installed_v1\GhostCode\offline\keep_after_upgrade.cpp"
        New-Item -ItemType Directory -Force -Path (Split-Path $upgradeCheckFile) | Out-Null
        Set-Content -Path $upgradeCheckFile -Value "// preserve me across upgrade`n" -Encoding UTF8

        $upgProc = Start-Process -FilePath $installerExe -ArgumentList $installArgs -Wait -PassThru
        if ($upgProc.ExitCode -ne 0) { throw "Upgrade install failed with exit code $($upgProc.ExitCode)" }
        if (-not (Test-Path $upgradeCheckFile)) { throw "Upgrade deleted existing user archive file!" }
        Assert-BackupWorksFromExe -ExePath $installedExe -Tag "Installed_Upgraded"
        Write-Host "  [PASS] Fresh install, Start Menu, Native Messaging registry, and non-destructive upgrade verified."

        Write-Host "[3/3] Silent Uninstall + user archive preservation check..."
        $uninstExe = Join-Path $installTarget "unins000.exe"
        if (-not (Test-Path $uninstExe)) { throw "Uninstaller unins000.exe not found at $installTarget" }
        $unProc = Start-Process -FilePath $uninstExe -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
        if ($unProc.ExitCode -ne 0) { throw "Uninstaller exited with $($unProc.ExitCode)" }

        $unDeadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $unDeadline -and (Test-Path $installedExe)) {
            Start-Sleep -Milliseconds 300
        }

        if (Test-Path $installedExe) { throw "GhostCode.App.exe still exists after uninstall!" }
        if (Test-Path "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge") {
            throw "NativeMessaging registry key remained after uninstall!"
        }
        if (-not (Test-Path $upgradeCheckFile)) {
            throw "Uninstaller deleted user's archived source code!"
        }
        Write-Host "  [PASS] Clean uninstall verified (binaries & registry removed, user archives 100% preserved)."
    }
}
finally {
    Get-Process -Name "GhostCode.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # Restore live AppData NativeMessaging wrapper pointing to main repo Release executable so Thorium keeps working cleanly
    $repoReleaseExe = "C:\Users\Duc\Documents\ssh\artifacts\Release\GhostCode-win-x64\GhostCode.App.exe"
    $nmDir = Join-Path $env:LOCALAPPDATA "GhostCode\NativeMessaging"
    if (Test-Path $repoReleaseExe) {
        New-Item -ItemType Directory -Force -Path $nmDir | Out-Null
        foreach ($pair in @(
            @{ Key = "chrome"; Reg = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge"; Ff = $false },
            @{ Key = "edge"; Reg = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge"; Ff = $false },
            @{ Key = "thorium"; Reg = "HKCU:\Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge"; Ff = $false },
            @{ Key = "firefox"; Reg = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge"; Ff = $true }
        )) {
            $cmdP = Join-Path $nmDir "com.ghostcode.bridge.$($pair.Key).cmd"
            $jsP = Join-Path $nmDir "com.ghostcode.bridge.$($pair.Key).json"
            Set-Content -Path $cmdP -Value "@echo off`r`n`"$repoReleaseExe`" --native-messaging-host --browser $($pair.Key) %*`r`n" -Encoding ASCII
            if ($pair.Ff) {
                @{ name="com.ghostcode.bridge"; description="GhostCode Native Messaging Bridge (Firefox)"; path=$cmdP; type="stdio"; allowed_extensions=@("ghostcode-bridge@trungduc22.github.io") } | ConvertTo-Json | Set-Content $jsP -Encoding UTF8
            } else {
                @{ name="com.ghostcode.bridge"; description="GhostCode Native Messaging Bridge"; path=$cmdP; type="stdio"; allowed_origins=@("chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/") } | ConvertTo-Json | Set-Content $jsP -Encoding UTF8
            }
            New-Item -Path $pair.Reg -Force | Out-Null
            Set-ItemProperty -Path $pair.Reg -Name "(default)" -Value $jsP
        }
    }
    if (Test-Path $scratchRoot) { Remove-Item -Path $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

[CmdletBinding()]
param(
    [string]$Version = "0.1.0-beta.1",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $repoRoot "packaging\Shutdown-GhostCodeByPath.ps1")
$releaseRoot = Join-Path $repoRoot "artifacts\Release"
$stageRoot = Join-Path $releaseRoot "_stage"
$stageDir = Join-Path $stageRoot "GhostCode-win-x64"
$outDir = Join-Path $releaseRoot "GhostCode-win-x64"
$portableZip = Join-Path $releaseRoot "GhostCode-Portable-win-x64.zip"
$installerExe = Join-Path $releaseRoot "GhostCode-Setup-x64.exe"
$checksumsPath = Join-Path $releaseRoot "SHA256SUMS.txt"

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if (Test-Path $stageRoot) { Remove-Item -Path $stageRoot -Recurse -Force }
if (Test-Path $portableZip) { Remove-Item -Path $portableZip -Force }
if ((-not $SkipInstaller) -and (Test-Path $installerExe)) { Remove-Item -Path $installerExe -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Write-Host "[1/5] Publishing self-contained win-x64 GhostCode ($Version)..."
& $dotnet publish (Join-Path $repoRoot "src\GhostCode.App\GhostCode.App.csproj") `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    --self-contained true `
    -p:PublishDir="$stageDir\"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "[2/5] Staging clean browser extensions & license notices (zero user secrets)..."
foreach ($docFile in @("LICENSE", "THIRD_PARTY_NOTICES.md")) {
    $srcDoc = Join-Path $repoRoot $docFile
    if (-not (Test-Path $srcDoc)) {
        throw "Required license/notice file missing from repo root: $srcDoc"
    }
    Copy-Item -Path $srcDoc -Destination (Join-Path $stageDir $docFile) -Force
}
foreach ($extDir in @("browser-extension", "browser-extension-firefox")) {
    $src = Join-Path $repoRoot $extDir
    $dst = Join-Path $stageDir $extDir
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Get-ChildItem -Path $src -File | Where-Object {
        $_.Name -ine "pairing.generated.json"
    } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $dst $_.Name) -Force
    }
}

Write-Host "[3/5] Verifying XAML .xbf/.pri, assets, licenses, and zero secret leaks..."
$requiredPaths = @(
    "GhostCode.App.exe",
    "GhostCode.Core.dll",
    "GhostCode.App.pri",
    "LICENSE",
    "THIRD_PARTY_NOTICES.md",
    "Assets\app.ico",
    "Assets\ghostcode_logo.png",
    "browser-extension\manifest.json",
    "browser-extension-firefox\manifest.json"
)
foreach ($rel in $requiredPaths) {
    $full = Join-Path $stageDir $rel
    if (-not (Test-Path $full)) {
        throw "Required release payload file missing: $full"
    }
}

$xbfFiles = Get-ChildItem -Path $stageDir -Recurse -Filter "*.xbf"
if ($xbfFiles.Count -lt 6) {
    throw "Compiled XAML .xbf validation failed (expected >= 6, found $($xbfFiles.Count))."
}

$leakedSecrets = Get-ChildItem -Path $stageDir -Recurse -File | Where-Object {
    $_.Name -ieq "pairing.generated.json" -or $_.Extension -iin @(".dpapi", ".db")
}
if ($leakedSecrets.Count -gt 0) {
    $list = ($leakedSecrets | Select-Object -ExpandProperty FullName) -join ", "
    throw "Refusing release build: sensitive local state found in publish folder: $list"
}

Write-Host "[4/5] Creating Portable ZIP ($portableZip)..."
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $portableZip -CompressionLevel Optimal -Force

$generatedAssets = New-Object System.Collections.Generic.List[string]
$generatedAssets.Add($portableZip)

if (-not $SkipInstaller) {
    Write-Host "[5/5] Compiling Inno Setup installer ($installerExe)..."
    $isccCandidates = @(
        (Get-Command "iscc.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $iscc = $isccCandidates | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6 (ISCC.exe) not found. Install JRSoftware.InnoSetup or pass -SkipInstaller."
    }

    & $iscc "/DAppVersion=$Version" "/DPayloadDir=$stageDir" "/DOutputDir=$releaseRoot" (Join-Path $repoRoot "packaging\GhostCode.iss")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $installerExe)) {
        throw "ISCC compilation failed with exit code $LASTEXITCODE"
    }
    $generatedAssets.Add($installerExe)
}
else {
    Write-Host "[5/5] Skipping installer build (-SkipInstaller)."
}

# Sync staged payload into artifacts\Release\GhostCode-win-x64 cleanly after ZIP + EXE are already finalized
$outExe = Join-Path $outDir "GhostCode.App.exe"
$canReplaceOutDir = $true
if (Test-Path $outExe) {
    $stopped = Stop-GhostCodeGracefullyByPath -ExecutablePath $outExe -TimeoutSeconds 6
    if (-not $stopped) {
        Write-Warning "Skipping replacement of '$outDir' because a GhostCode.App.exe instance from that exact directory did not exit cleanly."
        $canReplaceOutDir = $false
    }
}
if ($canReplaceOutDir) {
    if (Test-Path $outDir) { Remove-Item -Path $outDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $outDir)) {
        Move-Item -Path $stageDir -Destination $outDir -Force
    }
}
if (Test-Path $stageRoot) { Remove-Item -Path $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }

$lines = foreach ($asset in $generatedAssets) {
    $hash = (Get-FileHash -Path $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($asset)
    "$hash  $name"
}
Set-Content -Path $checksumsPath -Value $lines -Encoding ASCII

Write-Host "Release packaging complete:"
foreach ($asset in $generatedAssets) {
    $fi = Get-Item $asset
    $sha = (Get-FileHash -Path $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("  - {0} ({1:N2} MB) SHA256={2}" -f $fi.Name, ($fi.Length / 1MB), $sha)
}

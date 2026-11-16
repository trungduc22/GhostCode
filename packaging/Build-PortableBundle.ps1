$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $repoRoot "artifacts\Release\GhostCode-win-x64"
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if (Test-Path $outDir) { Remove-Item -Path $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $dotnet publish (Join-Path $repoRoot "src\GhostCode.App\GhostCode.App.csproj") `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    --self-contained true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -Path (Join-Path $repoRoot "browser-extension") -Destination (Join-Path $outDir "browser-extension") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "browser-extension-firefox") -Destination (Join-Path $outDir "browser-extension-firefox") -Recurse -Force
Write-Host "Portable Win-x64 release bundle ready at: $outDir"

param(
    [Parameter(Mandatory)][string]$Application,
    [Parameter(Mandatory)][string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Application).Path
$reportRoot = (New-Item -ItemType Directory -Path $ReportDirectory).FullName
$data = (New-Item -ItemType Directory -Path (Join-Path $reportRoot 'AppData')).FullName
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try {
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
}
finally {
    $listener.Stop()
}

$config = @{
    ArchiveDir = Join-Path $data 'GhostCode'
    DatabasePath = Join-Path $data 'ghostcode.db'
    WatchedFolders = @()
    HttpPort = $port
    DriveEnabled = $false
    SyncPausedManually = $false
    GlobalHotkeyEnabled = $false
    LaunchAtStartup = $false
}
$env:GHOSTCODE_CONFIG_PATH = Join-Path $data 'config.json'
$env:GHOSTCODE_SELF_TEST_REPORT = Join-Path $reportRoot 'checks.json'
$env:GHOSTCODE_SCREENSHOT_DIR = Join-Path $reportRoot 'screenshots'
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $env:GHOSTCODE_CONFIG_PATH -Encoding utf8

$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
if (!$process.WaitForExit(180000)) {
    $process.Kill()
    throw 'GhostCode UI self-test timed out.'
}
if ($process.ExitCode -ne 0) {
    throw "GhostCode exited with $($process.ExitCode)."
}

$result = Get-Content -LiteralPath $env:GHOSTCODE_SELF_TEST_REPORT -Raw | ConvertFrom-Json
$checks = @($result.checks.PSObject.Properties)
$failures = @($checks | Where-Object {-not $_.Value.passed})
foreach ($failure in $failures) {
    Write-Host "$($failure.Name): $($failure.Value.detail)"
}
Write-Host "$($checks.Count - $failures.Count) / $($checks.Count) passed. $($result.screenshots.Count) captures."
if (!$result.all_passed) {
    throw 'GhostCode UI self-test failed.'
}

function Get-GhostCodeExitEventName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $normalized = [System.IO.Path]::GetFullPath($ExecutablePath.Trim()).ToLowerInvariant()
    $bytes = [System.Text.Encoding]::Unicode.GetBytes($normalized)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex16 = ([System.BitConverter]::ToString($hash, 0, 8)).Replace("-", "").ToLowerInvariant()
    return "Local\GhostCode_Exit_$hex16"
}

function Get-GhostCodeProcessesByExePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $normalizedTarget = [System.IO.Path]::GetFullPath($ExecutablePath.Trim()).ToLowerInvariant()
    $matches = New-Object System.Collections.Generic.List[object]

    $procs = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'GhostCode.App.exe'" -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        if ([string]::IsNullOrWhiteSpace($proc.ExecutablePath)) {
            continue
        }

        try {
            $procPath = [System.IO.Path]::GetFullPath($proc.ExecutablePath.Trim()).ToLowerInvariant()
            if ($procPath -eq $normalizedTarget) {
                $matches.Add($proc)
            }
        }
        catch {
            # Ignore inaccessible or exiting process path checks
        }
    }

    return $matches
}

function Stop-GhostCodeGracefullyByPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,
        [int]$TimeoutSeconds = 8
    )

    $running = Get-GhostCodeProcessesByExePath -ExecutablePath $ExecutablePath
    if ($running.Count -eq 0) {
        return $true
    }

    $eventName = Get-GhostCodeExitEventName -ExecutablePath $ExecutablePath
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        [System.Threading.EventWaitHandle]$waitHandle = $null
        if ([System.Threading.EventWaitHandle]::TryOpenExisting($eventName, [ref]$waitHandle)) {
            try {
                [void]$waitHandle.Set()
            }
            finally {
                $waitHandle.Dispose()
            }
        }

        Start-Sleep -Milliseconds 200
        $running = Get-GhostCodeProcessesByExePath -ExecutablePath $ExecutablePath
        if ($running.Count -eq 0) {
            return $true
        }
    }

    return $false
}

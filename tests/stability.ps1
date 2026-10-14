# Stability and cost checks for Desktop Vitals.
# Run from the repo (or via CI): pwsh -File tests\stability.ps1
# Env: SOAK_SECONDS (default 480). Prints PASS/FAIL. Exit non-zero on failure.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$script:FailCount = 0
$SoakSeconds = 480
if ($env:SOAK_SECONDS -and $env:SOAK_SECONDS -match '^\d+$') {
    $SoakSeconds = [int]$env:SOAK_SECONDS
}
$Work = Join-Path $env:TEMP ("dv-stability-" + [guid]::NewGuid().ToString('N'))
$ResultsDir = Join-Path $Root 'tests\results'
$ConfigDir = Join-Path $env:APPDATA 'DesktopVitals'
$ConfigFile = Join-Path $ConfigDir 'watch.txt'
$ReviewFile = Join-Path $ConfigDir 'review.txt'
$ConfigBackup = Join-Path $Work 'watch.txt.bak'
$ReviewBackup = Join-Path $Work 'review.txt.bak'
$HadConfig = $false
$HadReview = $false
$Children = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DvStabNative {
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public const int SW_MINIMIZE = 6;
  public const uint GR_GDIOBJECTS = 0;
  public const uint GR_USEROBJECTS = 1;
}
"@

function Write-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:FailCount++ }
    if ($Detail) { Write-Host ("{0} {1} ({2})" -f $tag, $Name, $Detail) }
    else { Write-Host ("{0} {1}" -f $tag, $Name) }
}

function Save-Config {
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    if (Test-Path -LiteralPath $ConfigFile) {
        $script:HadConfig = $true
        Copy-Item -LiteralPath $ConfigFile -Destination $ConfigBackup -Force
    }
    if (Test-Path -LiteralPath $ReviewFile) {
        $script:HadReview = $true
        Copy-Item -LiteralPath $ReviewFile -Destination $ReviewBackup -Force
    }
}

function Restore-Config {
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    if ($script:HadConfig -and (Test-Path -LiteralPath $ConfigBackup)) {
        Copy-Item -LiteralPath $ConfigBackup -Destination $ConfigFile -Force
    }
    elseif (Test-Path -LiteralPath $ConfigFile) {
        Remove-Item -LiteralPath $ConfigFile -Force -ErrorAction SilentlyContinue
    }
    if ($script:HadReview -and (Test-Path -LiteralPath $ReviewBackup)) {
        Copy-Item -LiteralPath $ReviewBackup -Destination $ReviewFile -Force
    }
    elseif (Test-Path -LiteralPath $ReviewFile) {
        Remove-Item -LiteralPath $ReviewFile -Force -ErrorAction SilentlyContinue
    }
}

function Stop-Own {
    foreach ($p in $Children) {
        try {
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    $Children.Clear()
    Get-Process -Name 'DesktopVitals' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Get-GuiCounts {
    param([System.Diagnostics.Process]$Proc)
    $gdi = [DvStabNative]::GetGuiResources($Proc.Handle, [DvStabNative]::GR_GDIOBJECTS)
    $user = [DvStabNative]::GetGuiResources($Proc.Handle, [DvStabNative]::GR_USEROBJECTS)
    return @{ Gdi = [int]$gdi; User = [int]$user }
}

function Get-Sample {
    param([int]$ProcId)
    $p = Get-Process -Id $ProcId -ErrorAction Stop
    $gui = Get-GuiCounts -Proc $p
    return [ordered]@{
        Utc = (Get-Date).ToUniversalTime().ToString('o')
        Handles = $p.HandleCount
        Threads = $p.Threads.Count
        PrivateBytes = $p.PrivateMemorySize64
        WorkingSet = $p.WorkingSet64
        Gdi = $gui.Gdi
        User = $gui.User
        TotalProcessorTimeMs = [int64]$p.TotalProcessorTime.TotalMilliseconds
    }
}

function Wait-MainWindow {
    param([int]$ProcId, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
        if ($null -eq $p) { throw 'process exited before window' }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 250
    }
    throw 'main window not found'
}

function Invoke-Snapshot {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--snapshot', $Dir) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(40000)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'snapshot exceeded 40 seconds'
    }
    if ($p.ExitCode -ne 0) { throw "snapshot exit $($p.ExitCode)" }
    $txt = Join-Path $Dir 'vitals.txt'
    $png = Join-Path $Dir 'vitals.png'
    if (-not (Test-Path -LiteralPath $txt)) { throw 'missing vitals.txt' }
    if (-not (Test-Path -LiteralPath $png)) { throw 'missing vitals.png' }
    return @{ Text = [System.IO.File]::ReadAllText($txt) }
}

New-Item -ItemType Directory -Force -Path $Work, $ResultsDir | Out-Null
Save-Config
$report = [ordered]@{
    machine = @{
        os = [Environment]::OSVersion.VersionString
        arch = $env:PROCESSOR_ARCHITECTURE
        cores = [Environment]::ProcessorCount
        computer = '<redacted>'
    }
    soakSecondsRequested = $SoakSeconds
    soakSecondsActual = 0
    checks = @{}
    samples = @()
    idle = @{}
    notes = New-Object System.Collections.Generic.List[string]
}

try {
    # Build once
    $buildOk = $false
    $buildDetail = ''
    try {
        $buildOut = & (Join-Path $Root 'build.ps1') 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { $buildDetail = "exit $LASTEXITCODE" }
        elseif ($buildOut -match '(?im)^\s*warning\b') { $buildDetail = 'compiler warning' }
        else { $buildOk = $true }
    }
    catch { $buildDetail = $_.Exception.Message }
    Write-Check 'build' $buildOk $buildDetail
    $report.checks['build'] = @{ ok = $buildOk; detail = $buildDetail }
    if (-not $buildOk) { throw 'build failed; aborting stability' }

    $exe = Join-Path $Root 'bin\DesktopVitals.exe'

    # --- Edge: binary / corrupt watch.txt ---
    $binOk = $false
    $binDetail = ''
    try {
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        $bytes = New-Object byte[] 4096
        (New-Object Random 1).NextBytes($bytes)
        # Sprinkle NULs and high bytes so this is not valid UTF-8 text.
        $bytes[0] = 0; $bytes[1] = 0; $bytes[2] = 0xff; $bytes[3] = 0xfe
        [System.IO.File]::WriteAllBytes($ConfigFile, $bytes)
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-binary-watch')
        $binOk = $true
    }
    catch { $binDetail = $_.Exception.Message }
    finally { Restore-Config }
    Write-Check 'binary-watch-txt' $binOk $binDetail
    $report.checks['binary-watch-txt'] = @{ ok = $binOk; detail = $binDetail }

    # --- Edge: locked watch.txt ---
    $lockOk = $false
    $lockDetail = ''
    $lockStream = $null
    try {
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllText($ConfigFile, "# locked`nPinned = notepad.exe`n")
        $lockStream = [System.IO.File]::Open($ConfigFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-locked-watch')
        $lockOk = $true
    }
    catch { $lockDetail = $_.Exception.Message }
    finally {
        if ($lockStream) { $lockStream.Dispose() }
        Restore-Config
    }
    Write-Check 'locked-watch-txt' $lockOk $lockDetail
    $report.checks['locked-watch-txt'] = @{ ok = $lockOk; detail = $lockDetail }

    # --- Edge: second instance returns; one process remains ---
    $secondOk = $false
    $secondDetail = ''
    try {
        Stop-Own
        $first = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
        $Children.Add($first) | Out-Null
        $null = Wait-MainWindow -ProcId $first.Id -TimeoutSeconds 40
        Start-Sleep -Seconds 1
        $second = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
        if (-not $second.WaitForExit(15000)) {
            try { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'second instance did not exit within 15s'
        }
        Start-Sleep -Milliseconds 500
        $alive = @(Get-Process -Name 'DesktopVitals' -ErrorAction SilentlyContinue)
        if ($alive.Count -ne 1) { throw ("expected 1 DesktopVitals process, found {0}" -f $alive.Count) }
        if ($alive[0].Id -ne $first.Id) { throw 'first instance was replaced' }
        $secondOk = $true
        $secondDetail = "second exit $($second.ExitCode); first pid kept"
    }
    catch { $secondDetail = $_.Exception.Message }
    finally { Stop-Own }
    Write-Check 'second-instance' $secondOk $secondDetail
    $report.checks['second-instance'] = @{ ok = $secondOk; detail = $secondDetail }

    # --- Soak while Live (normal window) ---
    $soakOk = $false
    $soakDetail = ''
    $samples = New-Object System.Collections.Generic.List[object]
    try {
        Stop-Own
        # Reviewed so first-run review does not block the live dashboard.
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $ConfigDir 'review.txt'), "@reviewed = 1`n")
        if (Test-Path -LiteralPath $ConfigFile) { Remove-Item -LiteralPath $ConfigFile -Force -ErrorAction SilentlyContinue }

        $live = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
        $Children.Add($live) | Out-Null
        $null = Wait-MainWindow -ProcId $live.Id -TimeoutSeconds 40
        Start-Sleep -Seconds 3

        $start = Get-Date
        $deadline = $start.AddSeconds($SoakSeconds)
        $interval = [Math]::Max(15, [Math]::Min(30, [int]($SoakSeconds / 16)))
        while ((Get-Date) -lt $deadline) {
            $live.Refresh()
            if ($live.HasExited) { throw "process exited during soak code $($live.ExitCode)" }
            $s = Get-Sample -ProcId $live.Id
            $samples.Add($s) | Out-Null
            Write-Host ("soak sample t={0}s handles={1} gdi={2} privateMB={3:N1} threads={4} cpuMs={5}" -f `
                [int]((Get-Date) - $start).TotalSeconds, $s.Handles, $s.Gdi, ($s.PrivateBytes / 1MB), $s.Threads, $s.TotalProcessorTimeMs)
            $remain = ($deadline - (Get-Date)).TotalSeconds
            if ($remain -le 0) { break }
            Start-Sleep -Seconds ([Math]::Min($interval, [Math]::Ceiling($remain)))
        }
        $live.Refresh()
        if ($live.HasExited) { throw "process exited at soak end code $($live.ExitCode)" }
        $endSample = Get-Sample -ProcId $live.Id
        $samples.Add($endSample) | Out-Null
        $report.soakSecondsActual = [int]((Get-Date) - $start).TotalSeconds
        $report.samples = @($samples)

        if ($samples.Count -lt 2) { throw 'need at least 2 samples' }
        $firstS = $samples[0]
        $lastS = $samples[$samples.Count - 1]
        $handleDelta = $lastS.Handles - $firstS.Handles
        $gdiDelta = $lastS.Gdi - $firstS.Gdi
        $privDelta = $lastS.PrivateBytes - $firstS.PrivateBytes
        $threadDelta = $lastS.Threads - $firstS.Threads
        $cpuDeltaMs = $lastS.TotalProcessorTimeMs - $firstS.TotalProcessorTimeMs
        $wallMs = [Math]::Max(1, $report.soakSecondsActual * 1000)
        $cores = [Math]::Max(1, [Environment]::ProcessorCount)
        $pctOfMachine = (100.0 * $cpuDeltaMs) / ($wallMs * $cores)

        $report.liveCost = [ordered]@{
            handleDelta = $handleDelta
            gdiDelta = $gdiDelta
            privateBytesDelta = $privDelta
            threadDelta = $threadDelta
            cpuDeltaMs = $cpuDeltaMs
            wallMs = $wallMs
            cores = $cores
            pctOfMachine = [Math]::Round($pctOfMachine, 3)
            claimTargetPct = 0.1
        }

        # Leak ship-blockers: growing handles/GDI/threads or runaway private bytes.
        if ($handleDelta -gt 200) { throw "handle leak delta=$handleDelta" }
        if ($gdiDelta -gt 50) { throw "GDI leak delta=$gdiDelta" }
        if ($threadDelta -gt 20) { throw "thread leak delta=$threadDelta" }
        if ($privDelta -gt (80MB)) { throw ("private bytes grew {0:N0}" -f $privDelta) }

        $soakOk = $true
        $soakDetail = ("{0}s live; handles {1}->{2} (d{3}); gdi {4}->{5} (d{6}); privateMB {7:N1}->{8:N1}; threads {9}->{10}; cpu {11:N3}% of machine" -f `
            $report.soakSecondsActual, $firstS.Handles, $lastS.Handles, $handleDelta, `
            $firstS.Gdi, $lastS.Gdi, $gdiDelta, `
            ($firstS.PrivateBytes / 1MB), ($lastS.PrivateBytes / 1MB), `
            $firstS.Threads, $lastS.Threads, $pctOfMachine)
        if ($SoakSeconds -lt 7200) {
            $report.notes += "Soak shorter than 2h: ran $($report.soakSecondsActual)s (SOAK_SECONDS=$SoakSeconds)."
        }
        if ($pctOfMachine -gt 1.0) {
            $report.notes += ("Live cost {0:N3}% of machine exceeds ~0.1% README claim on this runner; not treated as crash/leak ship-blocker." -f $pctOfMachine)
        }
    }
    catch { $soakDetail = $_.Exception.Message }
    Write-Check 'soak-live' $soakOk $soakDetail
    $report.checks['soak-live'] = @{ ok = $soakOk; detail = $soakDetail }

    # --- Minimized = timer stopped (CPU time nearly flat) ---
    $idleOk = $false
    $idleDetail = ''
    try {
        if (-not $soakOk) { throw 'skipped: soak failed' }
        $live = Get-Process -Name 'DesktopVitals' -ErrorAction Stop
        $hwnd = Wait-MainWindow -ProcId $live.Id -TimeoutSeconds 10
        [void][DvStabNative]::ShowWindow($hwnd, [DvStabNative]::SW_MINIMIZE)
        Start-Sleep -Seconds 2
        $live.Refresh()
        $before = Get-Sample -ProcId $live.Id
        Start-Sleep -Seconds 20
        $live.Refresh()
        if ($live.HasExited) { throw 'exited while minimized' }
        $after = Get-Sample -ProcId $live.Id
        $cpuIdleDelta = $after.TotalProcessorTimeMs - $before.TotalProcessorTimeMs
        # Allow a little scheduler noise; Live tick is every 1.5s and would burn far more.
        $idleOk = ($cpuIdleDelta -le 250)
        $idleDetail = ("minimized 20s; cpuMs delta={0}; handles {1}->{2}" -f $cpuIdleDelta, $before.Handles, $after.Handles)
        $report.idle = [ordered]@{
            mode = 'minimized'
            wallSeconds = 20
            cpuDeltaMs = $cpuIdleDelta
            handlesBefore = $before.Handles
            handlesAfter = $after.Handles
            note = 'Live-off shares UpdateRunning() timer.Stop path with minimize (src DesktopVitals.cs).'
        }
        if (-not $idleOk) { throw $idleDetail }
    }
    catch {
        $idleDetail = $_.Exception.Message
        $idleOk = $false
    }
    finally { Stop-Own }
    Write-Check 'minimized-idle-cpu' $idleOk $idleDetail
    $report.checks['minimized-idle-cpu'] = @{ ok = $idleOk; detail = $idleDetail }

    $report.notes += 'Sleep/resume, RDP reconnect, Fast User Switching, and display topology changes were not exercised on this runner. Known limits.'
}
finally {
    Stop-Own
    Restore-Config
    try {
        $jsonPath = Join-Path $ResultsDir 'stability.json'
        $txtPath = Join-Path $ResultsDir 'stability.txt'
        ($report | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $jsonPath -Encoding UTF8
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add('Desktop Vitals stability report')
        $lines.Add(("soakSecondsRequested={0} soakSecondsActual={1}" -f $report.soakSecondsRequested, $report.soakSecondsActual))
        foreach ($k in $report.checks.Keys) {
            $c = $report.checks[$k]
            $lines.Add(("{0}: ok={1} {2}" -f $k, $c.ok, $c.detail))
        }
        if ($report.liveCost) {
            $lines.Add(("liveCost pctOfMachine={0} handleDelta={1} gdiDelta={2} privateBytesDelta={3} threadDelta={4}" -f `
                $report.liveCost.pctOfMachine, $report.liveCost.handleDelta, $report.liveCost.gdiDelta, `
                $report.liveCost.privateBytesDelta, $report.liveCost.threadDelta))
        }
        foreach ($n in $report.notes) { $lines.Add("NOTE: $n") }
        $lines | Set-Content -LiteralPath $txtPath -Encoding UTF8
    }
    catch { }
    try { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

if ($script:FailCount -gt 0) {
    Write-Host ("Stability finished with {0} failure(s)." -f $script:FailCount)
    exit 1
}
Write-Host 'Stability finished: all checks passed.'
exit 0

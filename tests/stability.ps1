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
$PrefsFile = Join-Path $ConfigDir 'prefs.txt'
$ConfigBackup = Join-Path $Work 'watch.txt.bak'
$ReviewBackup = Join-Path $Work 'review.txt.bak'
$PrefsBackup = Join-Path $Work 'prefs.txt.bak'
$HadConfig = $false
$HadReview = $false
$HadPrefs = $false
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
    if (-not $Ok) {
        $script:FailCount++
        $flat = if ($Detail) { $Detail -replace '[\r\n]+', ' ' } else { 'failed' }
        Write-Host ("::error::{0}: {1}" -f $Name, $flat)
    }
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
    if (Test-Path -LiteralPath $PrefsFile) {
        $script:HadPrefs = $true
        Copy-Item -LiteralPath $PrefsFile -Destination $PrefsBackup -Force
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
    if ($script:HadPrefs -and (Test-Path -LiteralPath $PrefsBackup)) {
        Copy-Item -LiteralPath $PrefsBackup -Destination $PrefsFile -Force
    }
    elseif (Test-Path -LiteralPath $PrefsFile) {
        Remove-Item -LiteralPath $PrefsFile -Force -ErrorAction SilentlyContinue
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
    # Plain hashtable with explicit CLR types (OrderedDictionary + -f caused "Argument types do not match" in CI).
    return @{
        Utc = [string](Get-Date).ToUniversalTime().ToString('o')
        Handles = [int]$p.HandleCount
        Threads = [int]$p.Threads.Count
        PrivateBytes = [int64]$p.PrivateMemorySize64
        WorkingSet = [int64]$p.WorkingSet64
        Gdi = [int]$gui.Gdi
        User = [int]$gui.User
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
$reportNotes = New-Object System.Collections.Generic.List[string]
$report = @{
    machine = @{
        os = [string][Environment]::OSVersion.VersionString
        arch = [string]$env:PROCESSOR_ARCHITECTURE
        cores = [int][Environment]::ProcessorCount
        computer = '<redacted>'
    }
    soakSecondsRequested = [int]$SoakSeconds
    soakSecondsActual = 0
    checks = @{}
    samples = @()
    idle = @{}
    liveCost = $null
    notes = @()
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

    # --- Edge: binary / corrupt prefs.txt ---
    $binOk = $false
    $binDetail = ''
    try {
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        $bytes = New-Object byte[] 4096
        (New-Object Random 1).NextBytes($bytes)
        # Sprinkle NULs and high bytes so this is not valid UTF-8 text.
        $bytes[0] = 0; $bytes[1] = 0; $bytes[2] = 0xff; $bytes[3] = 0xfe
        [System.IO.File]::WriteAllBytes($PrefsFile, $bytes)
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-binary-prefs')
        $binOk = $true
    }
    catch { $binDetail = $_.Exception.Message }
    finally { Restore-Config }
    Write-Check 'binary-prefs' $binOk $binDetail
    $report.checks['binary-prefs'] = @{ ok = $binOk; detail = $binDetail }

    # --- Edge: locked prefs.txt ---
    $lockOk = $false
    $lockDetail = ''
    $lockStream = $null
    try {
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllText($PrefsFile, "# locked`nPinned = notepad.exe`n")
        $lockStream = [System.IO.File]::Open($PrefsFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-locked-prefs')
        $lockOk = $true
    }
    catch { $lockDetail = $_.Exception.Message }
    finally {
        if ($lockStream) { $lockStream.Dispose() }
        Restore-Config
    }
    Write-Check 'locked-prefs' $lockOk $lockDetail
    $report.checks['locked-prefs'] = @{ ok = $lockOk; detail = $lockDetail }

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
            [void]$samples.Add($s)
            $tSec = [int]((Get-Date) - $start).TotalSeconds
            $privMb = [double]($s.PrivateBytes / 1MB)
            Write-Host ("soak sample t={0}s handles={1} gdi={2} privateMB={3} threads={4} cpuMs={5}" -f `
                $tSec, $s.Handles, $s.Gdi, ([Math]::Round($privMb, 1)), $s.Threads, $s.TotalProcessorTimeMs)
            $remain = ($deadline - (Get-Date)).TotalSeconds
            if ($remain -le 0) { break }
            $sleepSec = [int][Math]::Min([double]$interval, [Math]::Ceiling($remain))
            if ($sleepSec -lt 1) { $sleepSec = 1 }
            Start-Sleep -Seconds $sleepSec
        }
        $live.Refresh()
        if ($live.HasExited) { throw "process exited at soak end code $($live.ExitCode)" }
        [void]$samples.Add((Get-Sample -ProcId $live.Id))
        $report.soakSecondsActual = [int]((Get-Date) - $start).TotalSeconds
        $report.samples = [object[]]@($samples.ToArray())

        if ($samples.Count -lt 2) { throw 'need at least 2 samples' }
        $firstS = $samples[0]
        $lastS = $samples[$samples.Count - 1]
        $handleDelta = [int]($lastS.Handles - $firstS.Handles)
        $gdiDelta = [int]($lastS.Gdi - $firstS.Gdi)
        $privDelta = [int64]($lastS.PrivateBytes - $firstS.PrivateBytes)
        $threadDelta = [int]($lastS.Threads - $firstS.Threads)
        $cpuDeltaMs = [int64]($lastS.TotalProcessorTimeMs - $firstS.TotalProcessorTimeMs)
        $wallMs = [int64][Math]::Max(1, $report.soakSecondsActual * 1000)
        $cores = [int][Math]::Max(1, [Environment]::ProcessorCount)
        $pctOfMachine = [double]((100.0 * $cpuDeltaMs) / ($wallMs * $cores))
        $pctRounded = [double][Math]::Round($pctOfMachine, 3)
        $privFirstMb = [double][Math]::Round(($firstS.PrivateBytes / 1MB), 1)
        $privLastMb = [double][Math]::Round(($lastS.PrivateBytes / 1MB), 1)

        $report.liveCost = @{
            handleDelta = $handleDelta
            gdiDelta = $gdiDelta
            privateBytesDelta = $privDelta
            threadDelta = $threadDelta
            cpuDeltaMs = $cpuDeltaMs
            wallMs = $wallMs
            cores = $cores
            pctOfMachine = $pctRounded
            claimTargetPct = 0.1
        }

        # Leak ship-blockers: growing handles/GDI/threads or runaway private bytes.
        if ($handleDelta -gt 200) { throw "handle leak delta=$handleDelta" }
        if ($gdiDelta -gt 50) { throw "GDI leak delta=$gdiDelta" }
        if ($threadDelta -gt 20) { throw "thread leak delta=$threadDelta" }
        if ($privDelta -gt 80MB) { throw "private bytes grew $privDelta" }

        $soakOk = $true
        $soakDetail = "$(($report.soakSecondsActual))s live; handles $($firstS.Handles)->$($lastS.Handles) (d$handleDelta); gdi $($firstS.Gdi)->$($lastS.Gdi) (d$gdiDelta); privateMB $privFirstMb->$privLastMb; threads $($firstS.Threads)->$($lastS.Threads); cpu $pctRounded% of machine"
        if ($SoakSeconds -lt 7200) {
            [void]$reportNotes.Add("Soak shorter than 2h: ran $($report.soakSecondsActual)s (SOAK_SECONDS=$SoakSeconds).")
        }
        if ($pctOfMachine -gt 1.0) {
            [void]$reportNotes.Add("Live cost $pctRounded% of machine exceeds ~0.1% README claim on this runner; not treated as crash/leak ship-blocker.")
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
        $cpuIdleDelta = [int64]($after.TotalProcessorTimeMs - $before.TotalProcessorTimeMs)
        # Allow a little scheduler noise; Live tick is every 1.5s and would burn far more.
        $idleOk = ($cpuIdleDelta -le 250)
        $idleDetail = "minimized 20s; cpuMs delta=$cpuIdleDelta; handles $($before.Handles)->$($after.Handles)"
        $report.idle = @{
            mode = 'minimized'
            wallSeconds = 20
            cpuDeltaMs = $cpuIdleDelta
            handlesBefore = [int]$before.Handles
            handlesAfter = [int]$after.Handles
            note = 'Live-off shares UpdateRunning() timer.Stop path with minimize (src/Window.cs).'
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

    [void]$reportNotes.Add('Sleep/resume, RDP reconnect, Fast User Switching, and display topology changes were not exercised on this runner. Known limits.')
}
finally {
    Stop-Own
    Restore-Config
    try {
        $report.notes = [string[]]@($reportNotes.ToArray())
        $jsonPath = Join-Path $ResultsDir 'stability.json'
        $txtPath = Join-Path $ResultsDir 'stability.txt'
        ($report | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $jsonPath -Encoding UTF8
        $lines = New-Object System.Collections.Generic.List[string]
        [void]$lines.Add('Desktop Vitals stability report')
        [void]$lines.Add("soakSecondsRequested=$($report.soakSecondsRequested) soakSecondsActual=$($report.soakSecondsActual)")
        foreach ($k in @($report.checks.Keys)) {
            $c = $report.checks[$k]
            [void]$lines.Add("${k}: ok=$($c.ok) $($c.detail)")
        }
        if ($null -ne $report.liveCost) {
            $lc = $report.liveCost
            [void]$lines.Add("liveCost pctOfMachine=$($lc.pctOfMachine) handleDelta=$($lc.handleDelta) gdiDelta=$($lc.gdiDelta) privateBytesDelta=$($lc.privateBytesDelta) threadDelta=$($lc.threadDelta)")
        }
        foreach ($n in $report.notes) { [void]$lines.Add("NOTE: $n") }
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

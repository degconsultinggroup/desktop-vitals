# Short CI (or local) safety runtime gate for Desktop Vitals.
# Proves: no non-loopback TCP while DesktopVitals runs; file writes only under
# %APPDATA%\DesktopVitals, %LOCALAPPDATA%\DesktopVitals, and the explicit snapshot dir.
# Usage: pwsh -File tests\safety-runtime.ps1 [-Seconds 45] [-SkipBuild]
# For a 10 minute local soak use tests\safety-runtime-10min.ps1.
param(
    [int]$Seconds = 45,
    [string]$ResultsDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

if ($Seconds -lt 15) { $Seconds = 15 }
if (-not $ResultsDir) { $ResultsDir = Join-Path $Root 'tests\results' }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

$Work = Join-Path $env:TEMP ("dv-safety-" + [guid]::NewGuid().ToString('N'))
$SnapDir = Join-Path $Work 'snapshot'
$LogPath = Join-Path $ResultsDir 'safety-runtime.log'
$ConfigDir = Join-Path $env:APPDATA 'DesktopVitals'
$StateDir = Join-Path $env:LOCALAPPDATA 'DesktopVitals'
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
$Fail = $false
$Lines = New-Object System.Collections.Generic.List[string]

function Log {
    param([string]$Message)
    $line = ("{0:o}  {1}" -f [DateTimeOffset]::UtcNow, $Message)
    [void]$Lines.Add($line)
    Write-Host $line
    if ($Message -like 'FAIL*' -or $Message -like '*FAILED') {
        Write-Host ("::error::safety: {0}" -f ($Message -replace '[\r\n]+', ' '))
    }
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

function Test-IsLoopbackAddress {
    param([string]$Address)
    if ([string]::IsNullOrWhiteSpace($Address)) { return $true }
    if ($Address -eq '::' -or $Address -eq '0.0.0.0') { return $true }
    try {
        $ip = [System.Net.IPAddress]::Parse($Address)
        return [System.Net.IPAddress]::IsLoopback($ip)
    }
    catch {
        return ($Address -eq '127.0.0.1' -or $Address -eq '::1' -or $Address -eq 'localhost')
    }
}

function Get-TreeStamp {
    param([string]$Path)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
        $map[$_.FullName] = '{0}|{1}' -f $_.Length, $_.LastWriteTimeUtc.Ticks
    }
    return $map
}

function Get-NewOrChanged {
    param($Before, $After)
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($key in $After.Keys) {
        if (-not $Before.ContainsKey($key)) { [void]$hits.Add("new:$key"); continue }
        if ($Before[$key] -ne $After[$key]) { [void]$hits.Add("chg:$key") }
    }
    return @($hits)
}

function Test-AllowedWritePath {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $roots = @(
        [System.IO.Path]::GetFullPath($ConfigDir),
        [System.IO.Path]::GetFullPath($StateDir),
        [System.IO.Path]::GetFullPath($SnapDir)
    )
    foreach ($root in $roots) {
        if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        $prefix = $root.TrimEnd('\') + '\'
        if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

New-Item -ItemType Directory -Force -Path $Work, $SnapDir | Out-Null
Save-Config
Log ("safety-runtime start seconds={0} skipBuild={1}" -f $Seconds, [bool]$SkipBuild)

try {
    if (-not $SkipBuild) {
        Log 'building'
        & (Join-Path $Root 'build.ps1')
        if ($LASTEXITCODE -ne 0) { throw "build exit $LASTEXITCODE" }
    }

    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }

    # Canary trees outside allowed roots must stay untouched.
    $canaries = @(
        (Join-Path $env:USERPROFILE 'Documents\dv-safety-canary'),
        (Join-Path $env:USERPROFILE 'Downloads\dv-safety-canary'),
        (Join-Path $env:TEMP 'dv-safety-canary-out')
    )
    $canaryBefore = @{}
    foreach ($c in $canaries) {
        New-Item -ItemType Directory -Force -Path $c | Out-Null
        $marker = Join-Path $c 'marker.txt'
        Set-Content -LiteralPath $marker -Value 'do-not-touch' -Encoding ascii
        $canaryBefore[$c] = Get-TreeStamp $c
    }

    $watchBefore = Get-TreeStamp $ConfigDir
    $stateBefore = Get-TreeStamp $StateDir
    $deskBefore = @{}
    $deskPath = [Environment]::GetFolderPath('Desktop')
    if ($deskPath) { $deskBefore = Get-TreeStamp $deskPath }

    $outbound = New-Object System.Collections.Generic.List[string]
    $seenPids = New-Object 'System.Collections.Generic.HashSet[int]'
    $snapCount = 0

    # Background TCP watch so snapshot WaitForExit does not starve the probe.
    $monitor = Start-Job -ScriptBlock {
        param($Seconds)
        $hits = New-Object System.Collections.Generic.List[string]
        $pids = New-Object 'System.Collections.Generic.HashSet[int]'
        $deadline = (Get-Date).AddSeconds($Seconds)
        function IsLoopback([string]$Address) {
            if ([string]::IsNullOrWhiteSpace($Address)) { return $true }
            if ($Address -eq '::' -or $Address -eq '0.0.0.0') { return $true }
            try { return [System.Net.IPAddress]::IsLoopback([System.Net.IPAddress]::Parse($Address)) }
            catch { return ($Address -eq '127.0.0.1' -or $Address -eq '::1') }
        }
        while ((Get-Date) -lt $deadline) {
            foreach ($p in @(Get-Process -Name 'DesktopVitals' -ErrorAction SilentlyContinue)) {
                [void]$pids.Add([int]$p.Id)
            }
            if ($pids.Count -gt 0) {
                try {
                    foreach ($c in @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $pids.Contains([int]$_.OwningProcess) })) {
                        $remote = [string]$c.RemoteAddress
                        if (IsLoopback $remote) { continue }
                        $key = ("pid={0} {1}:{2}->{3}:{4} state={5}" -f $c.OwningProcess, $c.LocalAddress, $c.LocalPort, $remote, $c.RemotePort, $c.State)
                        if (-not $hits.Contains($key)) { [void]$hits.Add($key) }
                    }
                }
                catch { }
            }
            Start-Sleep -Milliseconds 300
        }
        ,@{ Hits = $hits; Pids = @($pids) }
    } -ArgumentList ($Seconds + 20)

    Log 'monitor begin'
    $deadline = (Get-Date).AddSeconds($Seconds)
    $nextSnap = Get-Date
    while ((Get-Date) -lt $deadline) {
        if ((Get-Date) -ge $nextSnap) {
            $snapCount++
            $dir = Join-Path $SnapDir ("pass-{0}" -f $snapCount)
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Log ("snapshot $snapCount -> $dir")
            $p = Start-Process -FilePath $exe -ArgumentList @('--snapshot', $dir) -PassThru -WindowStyle Hidden
            $Children.Add($p) | Out-Null
            [void]$seenPids.Add([int]$p.Id)
            if (-not $p.WaitForExit(70000)) {
                try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
                throw "snapshot $snapCount exceeded 70 seconds"
            }
            if ($p.ExitCode -ne 0) { throw "snapshot $snapCount exit $($p.ExitCode)" }
            if (-not (Test-Path -LiteralPath (Join-Path $dir 'vitals.txt'))) { throw "snapshot $snapCount missing vitals.txt" }
            $gap = if ($Seconds -ge 300) { 30 } else { [Math]::Max(10, [int]($Seconds / 3)) }
            $nextSnap = (Get-Date).AddSeconds($gap)
        }
        else {
            Start-Sleep -Milliseconds 500
        }
    }

    # Brief live window sample (own process only). Soft if the host has no interactive session.
    Log 'live window sample'
    try {
        $live = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
        $Children.Add($live) | Out-Null
        [void]$seenPids.Add([int]$live.Id)
        Start-Sleep -Seconds ([Math]::Min(8, [Math]::Max(4, [int]($Seconds / 8))))
        try { if (-not $live.HasExited) { Stop-Process -Id $live.Id -Force -ErrorAction SilentlyContinue } } catch { }
        Log ("live pid={0} exited={1}" -f $live.Id, $live.HasExited)
    }
    catch {
        Log ("live window skipped: $($_.Exception.Message)")
    }

    Wait-Job $monitor -Timeout ($Seconds + 40) | Out-Null
    $mon = Receive-Job $monitor -ErrorAction SilentlyContinue
    Remove-Job $monitor -Force -ErrorAction SilentlyContinue
    if ($mon -and $mon.Hits) {
        foreach ($h in @($mon.Hits)) {
            if ($h) { [void]$outbound.Add([string]$h); Log ("OUTBOUND $h") }
        }
    }
    if ($mon -and $mon.Pids) {
        foreach ($id in @($mon.Pids)) { [void]$seenPids.Add([int]$id) }
    }

    if ($outbound.Count -gt 0) {
        $Fail = $true
        Log ("FAIL outbound non-loopback TCP count={0}" -f $outbound.Count)
    }
    else {
        Log 'PASS outbound: zero non-loopback TCP for DesktopVitals'
    }

    $watchAfter = Get-TreeStamp $ConfigDir
    $stateAfter = Get-TreeStamp $StateDir
    $watchDelta = @(Get-NewOrChanged $watchBefore $watchAfter)
    $stateDelta = @(Get-NewOrChanged $stateBefore $stateAfter)
    foreach ($d in @($watchDelta + $stateDelta)) {
        if (-not $d) { continue }
        $path = ($d -split ':', 2)[1]
        if (-not (Test-AllowedWritePath $path)) {
            $Fail = $true
            Log "FAIL write outside allowed roots: $d"
        }
        else {
            Log "ok write under allowed root: $d"
        }
    }

    # Explicit snapshot tree is allowed; require at least one vitals pair.
    $snapFiles = @(Get-ChildItem -LiteralPath $SnapDir -Recurse -File -ErrorAction SilentlyContinue)
    if ($snapFiles.Count -lt 2) {
        $Fail = $true
        Log 'FAIL snapshot dir missing outputs'
    }
    else {
        Log ("PASS snapshot writes count={0} under {1}" -f $snapFiles.Count, $SnapDir)
    }

    foreach ($c in $canaries) {
        $after = Get-TreeStamp $c
        $delta = Get-NewOrChanged $canaryBefore[$c] $after
        # marker.txt may show chg if something else touched it; only fail on new files or marker content change.
        $marker = Join-Path $c 'marker.txt'
        $text = if (Test-Path -LiteralPath $marker) { [System.IO.File]::ReadAllText($marker).Trim() } else { '' }
        $newFiles = @($delta | Where-Object { $_ -like 'new:*' })
        if ($newFiles.Count -gt 0 -or $text -ne 'do-not-touch') {
            $Fail = $true
            Log ("FAIL canary polluted: $c delta=$($delta -join ', ') text=$text")
        }
        else {
            Log "PASS canary clean: $c"
        }
    }

    if ($deskPath) {
        $deskAfter = Get-TreeStamp $deskPath
        $deskDelta = @(Get-NewOrChanged $deskBefore $deskAfter | Where-Object {
                $_ -match '(?i)DesktopVitals|vitals\.(txt|png)'
            })
        if ($deskDelta.Count -gt 0) {
            $Fail = $true
            Log ("FAIL unexpected Desktop writes: " + ($deskDelta -join ', '))
        }
        else {
            Log 'PASS no DesktopVitals Desktop writes'
        }
    }

    Log ("pids seen: " + (($seenPids | Sort-Object) -join ', '))
    Log ("snapshots: $snapCount")
}
catch {
    $Fail = $true
    Log ("FAIL exception: $($_.Exception.Message)")
}
finally {
    Stop-Own
    Restore-Config
    try {
        [System.IO.File]::WriteAllLines($LogPath, $Lines)
    }
    catch { }
    try { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

if ($Fail) {
    Log 'safety-runtime FAILED'
    try { [System.IO.File]::WriteAllLines($LogPath, $Lines) } catch { }
    exit 1
}
Log 'safety-runtime PASSED'
try { [System.IO.File]::WriteAllLines($LogPath, $Lines) } catch { }
exit 0

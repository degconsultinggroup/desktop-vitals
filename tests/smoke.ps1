# Smoke checks for Desktop Vitals. Run from the repo (or via CI): pwsh -File tests\smoke.ps1
# Prints one PASS/FAIL line per check. Exits non-zero if any check fails.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$script:FailCount = 0
$Children = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$Work = Join-Path $env:TEMP ("dv-smoke-" + [guid]::NewGuid().ToString('N'))
$ResultsDir = Join-Path $Root 'tests\results'
$ConfigDir = Join-Path $env:APPDATA 'DesktopVitals'
$ConfigFile = Join-Path $ConfigDir 'watch.txt'
$ReviewFile = Join-Path $ConfigDir 'review.txt'
$ConfigBackup = Join-Path $Work 'watch.txt.bak'
$ReviewBackup = Join-Path $Work 'review.txt.bak'
$HadConfig = $false
$HadReview = $false

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
        Remove-Item -LiteralPath $ConfigFile -Force
    }
    if ($script:HadReview -and (Test-Path -LiteralPath $ReviewBackup)) {
        Copy-Item -LiteralPath $ReviewBackup -Destination $ReviewFile -Force
    }
    elseif (Test-Path -LiteralPath $ReviewFile) {
        Remove-Item -LiteralPath $ReviewFile -Force
    }
}

function Set-ConfigText {
    param([string]$Text)
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    [System.IO.File]::WriteAllText($ConfigFile, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Start-Child {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )
    $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -WindowStyle Hidden
    $Children.Add($p) | Out-Null
    return $p
}

function Stop-Children {
    foreach ($p in $Children) {
        try {
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    $Children.Clear()
    Get-Process -Name 'calc', 'CalculatorApp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Wait-ReviewFile {
    param([int]$TimeoutSeconds = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $ReviewFile) {
            try {
                $text = [System.IO.File]::ReadAllText($ReviewFile)
                if ($text -match '@reviewed\s*=') { return $text }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'review.txt not written'
}

function Invoke-Snapshot {
    param(
        [string]$Dir,
        [switch]$Review,
        [switch]$AcceptReview
    )
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }
    $argList = [System.Collections.Generic.List[string]]::new()
    [void]$argList.Add('--snapshot')
    [void]$argList.Add($Dir)
    if ($Review) { [void]$argList.Add('--review') }
    if ($AcceptReview) { [void]$argList.Add('--accept-review') }
    $p = Start-Process -FilePath $exe -ArgumentList $argList.ToArray() -PassThru -WindowStyle Hidden
    # ARM x64 emulation can need well over 20s for the first WPF cold start.
    if (-not $p.WaitForExit(40000)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'snapshot exceeded 40 seconds'
    }
    if ($p.ExitCode -ne 0) { throw "snapshot exit $($p.ExitCode)" }
    $txt = Join-Path $Dir 'vitals.txt'
    $png = Join-Path $Dir 'vitals.png'
    if (-not (Test-Path -LiteralPath $txt)) { throw 'missing vitals.txt' }
    if (-not (Test-Path -LiteralPath $png)) { throw 'missing vitals.png' }
    if ($AcceptReview) { $null = Wait-ReviewFile -TimeoutSeconds 15 }
    return @{ Txt = $txt; Png = $png; Text = [System.IO.File]::ReadAllText($txt) }
}

function Get-PythonCommand {
    foreach ($name in @('python', 'py')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -eq $cmd) { continue }
        try {
            $ver = & $cmd.Source --version 2>&1 | Out-String
            if ($ver -match 'Python') { return $cmd.Source }
        }
        catch { }
    }
    return $null
}

function Test-HasCommandLines {
    param([string]$Text)
    if ($Text -match '(?i)\bcmd\.exe\b') { return $true }
    if ($Text -match '(?i)[a-z]:\\[^\r\n]{0,240}\.exe["\s]+["/]') { return $true }
    if ($Text -match '(?i)\s/[cCkK]\s+"') { return $true }
    return $false
}

New-Item -ItemType Directory -Force -Path $Work, $ResultsDir | Out-Null
Save-Config

try {
    # 1. Build (csc already uses /warnaserror+; also reject warning text)
    $buildOk = $false
    $buildDetail = ''
    try {
        $buildOut = & (Join-Path $Root 'build.ps1') 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { $buildDetail = "exit $LASTEXITCODE" }
        elseif ($buildOut -match '(?im)^\s*warning\b') { $buildDetail = 'compiler warning' }
        else { $buildOk = $true }
    }
    catch {
        $buildDetail = $_.Exception.Message
    }
    Write-Check 'build' $buildOk $buildDetail

    # 2. Snapshot and review snapshot
    $snapOk = $false
    $snapDetail = ''
    try {
        $snapDir = Join-Path $Work 'snap-basic'
        $basic = Invoke-Snapshot -Dir $snapDir
        $reviewDir = Join-Path $Work 'snap-review'
        $null = Invoke-Snapshot -Dir $reviewDir -Review
        Copy-Item -LiteralPath $basic.Png -Destination (Join-Path $ResultsDir 'vitals.png') -Force
        Copy-Item -LiteralPath $basic.Txt -Destination (Join-Path $ResultsDir 'vitals.txt') -Force
        $snapOk = $true
    }
    catch {
        $snapDetail = $_.Exception.Message
    }
    Write-Check 'snapshot' $snapOk $snapDetail

    # 3. Scripts: PowerShell -File sleeper; Python script file when available
    #    (python -c is not a script path, so Scripts would not list it)
    $scriptsOk = $false
    $scriptsDetail = ''
    try {
        $scriptHome = Join-Path $Work 'scripts'
        New-Item -ItemType Directory -Force -Path $scriptHome | Out-Null
        $psScript = Join-Path $scriptHome 'dv_smoke_ps.ps1'
        Set-Content -LiteralPath $psScript -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $psScript)

        $expect = @('dv_smoke_ps.ps1')
        $py = Get-PythonCommand
        if ($py) {
            $pyScript = Join-Path $scriptHome 'dv_smoke_py.py'
            Set-Content -LiteralPath $pyScript -Value "import time`ntime.sleep(60)`n" -Encoding UTF8
            $null = Start-Child -FilePath $py -ArgumentList @($pyScript)
            $expect += 'dv_smoke_py.py'
        }
        else {
            $scriptsDetail = 'python missing; powershell only'
        }

        Start-Sleep -Seconds 2
        $scriptsSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-scripts')
        $sectionLines = New-Object System.Collections.Generic.List[string]
        $inScripts = $false
        foreach ($line in ($scriptsSnap.Text -split "`r?`n")) {
            if (-not $inScripts) {
                if ($line -eq 'Scripts') { $inScripts = $true }
                elseif ($line -like 'Scripts:*') { throw 'Scripts section empty' }
                continue
            }
            if ($line.Length -eq 0) { break }
            if ($line -notmatch '^\s') { break }
            [void]$sectionLines.Add($line)
        }
        if (-not $inScripts) { throw 'Scripts section missing' }
        $section = [string]::Join("`n", $sectionLines)
        foreach ($name in $expect) {
            if ($section -notmatch [regex]::Escape($name)) { throw "missing $name under Scripts" }
        }
        $scriptsOk = $true
    }
    catch {
        if (-not $scriptsDetail) { $scriptsDetail = $_.Exception.Message }
        else { $scriptsDetail = "$scriptsDetail; $($_.Exception.Message)" }
    }
    finally {
        Stop-Children
    }
    Write-Check 'scripts-listed' $scriptsOk $scriptsDetail

    # 4. Injection regression: folder name contains "& calc &"
    $injOk = $false
    $injDetail = ''
    try {
        Get-Process -Name 'calc', 'CalculatorApp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $injDir = Join-Path $Work 'x & calc &'
        New-Item -ItemType Directory -Force -Path $injDir | Out-Null
        $injScript = Join-Path $injDir 'dv_smoke_inj.ps1'
        Set-Content -LiteralPath $injScript -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $injScript)
        Start-Sleep -Seconds 2
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-inject')
        Start-Sleep -Seconds 1
        $calc = @(Get-Process -Name 'calc', 'CalculatorApp' -ErrorAction SilentlyContinue)
        if ($calc.Count -gt 0) { throw 'calc.exe was started' }
        $injOk = $true
    }
    catch {
        $injDetail = $_.Exception.Message
    }
    finally {
        Stop-Children
    }
    Write-Check 'no-calc-injection' $injOk $injDetail

    # 5. Non-loopback http check is invalid and makes no outbound TCP
    $httpOk = $false
    $httpDetail = ''
    try {
        $httpScript = Join-Path $Work 'dv_smoke_http.ps1'
        Set-Content -LiteralPath $httpScript -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $httpScript)
        Set-ConfigText "SmokeHttp = dv_smoke_http.ps1 | check: http://example.com`n"

        $exampleIps = @()
        try {
            $exampleIps = @([System.Net.Dns]::GetHostAddresses('example.com') | ForEach-Object { $_.IPAddressToString })
        }
        catch { }
        if ($exampleIps.Count -eq 0) { throw 'could not resolve example.com for outbound watch' }

        $monitor = Start-Job -ScriptBlock {
            param($Ips, $Seconds)
            $deadline = (Get-Date).AddSeconds($Seconds)
            $hits = New-Object System.Collections.Generic.List[string]
            while ((Get-Date) -lt $deadline) {
                $conns = Get-NetTCPConnection -ErrorAction SilentlyContinue |
                    Where-Object { $Ips -contains $_.RemoteAddress }
                foreach ($c in @($conns)) {
                    $key = "$($c.RemoteAddress):$($c.RemotePort)"
                    if (-not $hits.Contains($key)) { [void]$hits.Add($key) }
                }
                Start-Sleep -Milliseconds 250
            }
            ,$hits
        } -ArgumentList @(, $exampleIps), 18

        Start-Sleep -Seconds 1
        $httpSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-http')
        Wait-Job $monitor -Timeout 25 | Out-Null
        $jobHits = @(Receive-Job $monitor -ErrorAction SilentlyContinue | ForEach-Object { $_ })
        Remove-Job $monitor -Force -ErrorAction SilentlyContinue
        $direct = @($jobHits | Where-Object { $_ -and "$_".Length -gt 0 } | ForEach-Object { "$_" })

        if ($httpSnap.Text -notmatch 'check must be a local address') {
            throw 'snapshot missing invalid-check message'
        }
        if ($direct.Count -gt 0) {
            throw ("outbound TCP to example.com: " + ($direct -join ', '))
        }
        $httpOk = $true
    }
    catch {
        $httpDetail = $_.Exception.Message
    }
    finally {
        Stop-Children
        Restore-Config
    }
    Write-Check 'http-local-only' $httpOk $httpDetail

    # 6. watch.txt over 1 MB is skipped; snapshot still works
    $hugeOk = $false
    $hugeDetail = ''
    try {
        $junk = New-Object byte[] (1MB + 1)
        (New-Object Random).NextBytes($junk)
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllBytes($ConfigFile, $junk)
        $hugeSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-huge-watch')
        if ($hugeSnap.Text -notmatch 'settings file too large') { throw 'missing too-large message' }
        $hugeOk = $true
    }
    catch {
        $hugeDetail = $_.Exception.Message
    }
    finally {
        Restore-Config
    }
    Write-Check 'huge-watch-txt' $hugeOk $hugeDetail

    # 7. Accept-review leaves a 250-line UTF-8 BOM watch.txt byte-for-byte unchanged
    $watchOk = $false
    $watchDetail = ''
    try {
        if (Test-Path -LiteralPath $ReviewFile) { Remove-Item -LiteralPath $ReviewFile -Force }
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add('First = first.exe')
        $long = 'Long = ' + ('x' * 600)
        $lines.Add($long)
        for ($i = 3; $i -le 250; $i++) { $lines.Add(("Line{0} = line{0}.exe" -f $i)) }
        $utf8Bom = New-Object System.Text.UTF8Encoding $true
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllLines($ConfigFile, $lines, $utf8Bom)
        $before = [System.IO.File]::ReadAllBytes($ConfigFile)
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-watch-intact') -AcceptReview
        $reviewText = Wait-ReviewFile -TimeoutSeconds 15
        $after = [System.IO.File]::ReadAllBytes($ConfigFile)
        if ($before.Length -ne $after.Length) { throw "length $($before.Length) -> $($after.Length)" }
        for ($i = 0; $i -lt $before.Length; $i++) {
            if ($before[$i] -ne $after[$i]) { throw "byte $i changed" }
        }
        if ($reviewText -notmatch '@reviewed\s*=') { throw 'review.txt missing @reviewed' }
        $watchOk = $true
    }
    catch {
        $watchDetail = $_.Exception.Message
    }
    finally {
        Restore-Config
    }
    Write-Check 'watch-txt-intact' $watchOk $watchDetail

    # 8. Default --snapshot folder creates vitals.txt and vitals.png on a fresh path
    $freshOk = $false
    $freshDetail = ''
    try {
        $defaultSnap = Join-Path $env:LOCALAPPDATA 'DesktopVitals\snapshot'
        if (Test-Path -LiteralPath $defaultSnap) { Remove-Item -LiteralPath $defaultSnap -Recurse -Force }
        $exe = Join-Path $Root 'bin\DesktopVitals.exe'
        $p = Start-Process -FilePath $exe -ArgumentList @('--snapshot') -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(40000)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'default snapshot exceeded 40 seconds'
        }
        if ($p.ExitCode -ne 0) { throw "default snapshot exit $($p.ExitCode)" }
        if (-not (Test-Path -LiteralPath (Join-Path $defaultSnap 'vitals.txt'))) { throw 'missing default vitals.txt' }
        if (-not (Test-Path -LiteralPath (Join-Path $defaultSnap 'vitals.png'))) { throw 'missing default vitals.png' }
        $freshOk = $true
    }
    catch {
        $freshDetail = $_.Exception.Message
    }
    Write-Check 'snapshot-fresh-profile' $freshOk $freshDetail

    # 9. check: http://[::1]:... is accepted as loopback (not "must be a local address")
    $v6Ok = $false
    $v6Detail = ''
    try {
        $v6Script = Join-Path $Work 'dv_smoke_v6.ps1'
        Set-Content -LiteralPath $v6Script -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $v6Script)
        Set-ConfigText "SmokeV6 = dv_smoke_v6.ps1 | check: http://[::1]:18765/health`n"
        Start-Sleep -Seconds 1
        $v6Snap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-v6')
        if ($v6Snap.Text -match 'check must be a local address') { throw 'IPv6 loopback rejected' }
        if ($v6Snap.Text -notmatch 'SmokeV6') { throw 'SmokeV6 bot row missing' }
        $v6Ok = $true
    }
    catch {
        $v6Detail = $_.Exception.Message
    }
    finally {
        Stop-Children
        Restore-Config
    }
    Write-Check 'http-ipv6-loopback' $v6Ok $v6Detail

    # 10. Long command lines (about 20k chars) are still visible to Watching
    $cmdOk = $false
    $cmdDetail = ''
    try {
        $marker = 'dvsmokeLongCmd' + ('Z' * 20000)
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "Start-Sleep -Seconds 60 # $marker")
        Set-ConfigText "LongCmd = dvsmokeLongCmd`n"
        Start-Sleep -Seconds 2
        $cmdSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-longcmd')
        if ($cmdSnap.Text -notmatch 'LongCmd') { throw 'LongCmd watching row missing' }
        $cmdOk = $true
    }
    catch {
        $cmdDetail = $_.Exception.Message
    }
    finally {
        Stop-Children
        Restore-Config
    }
    Write-Check 'long-command-line' $cmdOk $cmdDetail

    # 11. errors.log bounds and no foreign command lines
    $logOk = $false
    $logDetail = ''
    try {
        $logPath = Join-Path $ConfigDir 'errors.log'
        if (-not (Test-Path -LiteralPath $logPath)) {
            $logOk = $true
            $logDetail = 'absent'
        }
        else {
            $len = (Get-Item -LiteralPath $logPath).Length
            if ($len -ge 256KB) { throw "errors.log is $len bytes (cap 256 KB)" }
            $text = [System.IO.File]::ReadAllText($logPath)
            if (Test-HasCommandLines $text) { throw 'errors.log contains command-line-like text' }
            $logOk = $true
            $logDetail = "$len bytes"
        }
    }
    catch {
        $logDetail = $_.Exception.Message
    }
    Write-Check 'errors-log' $logOk $logDetail
}
finally {
    Stop-Children
    Restore-Config
    try { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

if ($script:FailCount -gt 0) {
    Write-Host ("Smoke finished with {0} failure(s)." -f $script:FailCount)
    exit 1
}
Write-Host 'Smoke finished: all checks passed.'
exit 0

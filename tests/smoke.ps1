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
$PrefsFile = Join-Path $ConfigDir 'prefs.txt'
$ConfigBackup = Join-Path $Work 'watch.txt.bak'
$ReviewBackup = Join-Path $Work 'review.txt.bak'
$PrefsBackup = Join-Path $Work 'prefs.txt.bak'
$HadConfig = $false
$HadReview = $false
$HadPrefs = $false

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
        Remove-Item -LiteralPath $ConfigFile -Force
    }
    if ($script:HadReview -and (Test-Path -LiteralPath $ReviewBackup)) {
        Copy-Item -LiteralPath $ReviewBackup -Destination $ReviewFile -Force
    }
    elseif (Test-Path -LiteralPath $ReviewFile) {
        Remove-Item -LiteralPath $ReviewFile -Force
    }
    if ($script:HadPrefs -and (Test-Path -LiteralPath $PrefsBackup)) {
        Copy-Item -LiteralPath $PrefsBackup -Destination $PrefsFile -Force
    }
    elseif (Test-Path -LiteralPath $PrefsFile) {
        Remove-Item -LiteralPath $PrefsFile -Force
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
        [switch]$PrivacyTest
    )
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }
    $argList = [System.Collections.Generic.List[string]]::new()
    [void]$argList.Add('--snapshot')
    [void]$argList.Add($Dir)
    $prevPriv = $env:DESKTOPVITALS_PRIVACY_TEST
    try {
        if ($PrivacyTest) { $env:DESKTOPVITALS_PRIVACY_TEST = '1' }
        else { Remove-Item Env:\DESKTOPVITALS_PRIVACY_TEST -ErrorAction SilentlyContinue }
        $p = Start-Process -FilePath $exe -ArgumentList $argList.ToArray() -PassThru -WindowStyle Hidden
        # ARM x64 emulation can need well over 20s for the first WPF cold start.
        if (-not $p.WaitForExit(70000)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'snapshot exceeded 70 seconds'
        }
        if ($p.ExitCode -ne 0) { throw "snapshot exit $($p.ExitCode)" }
    }
    finally {
        if ([string]::IsNullOrEmpty($prevPriv)) { Remove-Item Env:\DESKTOPVITALS_PRIVACY_TEST -ErrorAction SilentlyContinue }
        else { $env:DESKTOPVITALS_PRIVACY_TEST = $prevPriv }
    }
    $txt = Join-Path $Dir 'vitals.txt'
    $png = Join-Path $Dir 'vitals.png'
    if (-not (Test-Path -LiteralPath $txt)) { throw 'missing vitals.txt' }
    if (-not (Test-Path -LiteralPath $png)) { throw 'missing vitals.png' }
    $priv = Join-Path $Dir 'privacy.txt'
    return @{
        Txt = $txt
        Png = $png
        Text = [System.IO.File]::ReadAllText($txt)
        Privacy = $(if (Test-Path -LiteralPath $priv) { [System.IO.File]::ReadAllText($priv) } else { $null })
    }
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

# README safety promises as failing CI checks (static source + manifest).
function Invoke-SafetyStaticChecks {
    $srcDir = Join-Path $Root 'src'
    $manifestPath = Join-Path $Root 'src\app.manifest'
    $srcFiles = @(Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' | Sort-Object Name)
    if ($srcFiles.Count -eq 0) { throw "missing $srcDir\*.cs" }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "missing $manifestPath" }
    $src = ($srcFiles | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
    $manifest = [System.IO.File]::ReadAllText($manifestPath)
    $srcLines = $src -split "`r?`n"

    # Process.Start only at editor, Explorer, loopback port, and the Frontier credit link.
    $startHits = @()
    for ($i = 0; $i -lt $srcLines.Length; $i++) {
        if ($srcLines[$i] -match 'Process\.Start\s*\(') {
            $startHits += [pscustomobject]@{ Line = ($i + 1); Text = $srcLines[$i].Trim() }
        }
    }
    if ($startHits.Count -ne 4) {
        throw ("Process.Start count {0} (want 4 editor/Explorer/loopback/frontier): {1}" -f $startHits.Count, (($startHits | ForEach-Object { "$($_.Line):$($_.Text)" }) -join ' | '))
    }
    $joined = ($startHits | ForEach-Object { $_.Text }) -join "`n"
    if ($joined -notmatch 'ProcessStartInfo\(Editor\[0\]') { throw 'missing editor Process.Start' }
    if ($joined -notmatch 'ProcessStartInfo\(explorer') { throw 'missing explorer Process.Start' }
    if ($joined -notmatch 'ProcessStartInfo\("http://127\.0\.0\.1:"') { throw 'missing loopback port Process.Start' }
    if ($joined -notmatch 'ProcessStartInfo\("https://frontierdeployments\.com"\)') { throw 'missing Frontier Deployments link' }
    $editorLine = ($startHits | Where-Object { $_.Text -match 'Editor\[0\]' } | Select-Object -First 1).Text
    $explorerLine = ($startHits | Where-Object { $_.Text -match 'ProcessStartInfo\(explorer' } | Select-Object -First 1).Text
    $frontierLine = ($startHits | Where-Object { $_.Text -match 'frontierdeployments' } | Select-Object -First 1).Text
    if ($editorLine -notmatch 'UseShellExecute\s*=\s*false') { throw 'editor Process.Start must UseShellExecute=false' }
    if ($explorerLine -notmatch 'UseShellExecute\s*=\s*false') { throw 'explorer Process.Start must UseShellExecute=false' }
    if ($frontierLine -notmatch 'UseShellExecute\s*=\s*true') { throw 'Frontier link must UseShellExecute=true' }

    # Nothing launches cmd/powershell/pwsh/schtasks as a process image.
    $badLaunch = [regex]::Matches($src, '(?i)ProcessStartInfo\s*\(\s*"(cmd\.exe|powershell\.exe|pwsh\.exe|schtasks\.exe)"')
    if ($badLaunch.Count -gt 0) { throw ("forbidden launcher: " + $badLaunch[0].Value) }
    if ($src -match '(?i)Process\.Start\s*\(\s*"(cmd|powershell|pwsh|schtasks)') {
        throw 'forbidden Process.Start string launcher'
    }

    # No kill / close / service or task control APIs.
    foreach ($pat in @(
            'Process\.Kill\s*\(',
            'CloseMainWindow\s*\(',
            'ServiceController',
            '(?i)\bsc\.exe\b',
            '(?i)\btaskkill\b',
            '(?i)\bnet\.exe\b',
            '(?i)schtasks\.exe'
        )) {
        if ($src -match $pat) { throw "forbidden control API match: $pat" }
    }

    # Registry: read only (OpenSubKey / GetValue*). No writes.
    foreach ($pat in @('CreateSubKey\s*\(', 'SetValue\s*\(', 'DeleteSubKey(?:Tree)?\s*\(')) {
        if ($src -match $pat) { throw "registry write API: $pat" }
    }
    if ($src -notmatch 'OpenSubKey\s*\(') { throw 'expected registry OpenSubKey reads' }

    # No sockets, HTTP clients, or name lookups. Loopback opens go through the shell, not a client.
    foreach ($pat in @(
            'System\.Net\.Sockets',
            '\bTcpClient\b',
            '\bUdpClient\b',
            '\bSocket\b',
            '\bHttpClient\b',
            '\bWebClient\b',
            '\bDns\.GetHost',
            'FtpWebRequest',
            'HttpWebRequest',
            'WebRequest\.Create'
        )) {
        if ($src -match $pat) { throw "forbidden network API: $pat" }
    }

    # No hardcoded outbound product URLs. RFC 2606 example.* (and loopback) may appear in
    # privacy/redaction sample strings; those are not network destinations.
    $urlHits = [regex]::Matches($src, 'https?://[^\s"<>]+')
    foreach ($m in $urlHits) {
        $u = $m.Value
        if ($u -match '(?i)(^https?://([^/@]*@)?(example\.(com|org|net)|localhost|127\.0\.0\.1|\[::1\])(:|/|$))') {
            continue
        }
        # Gear credit link. The only product URL the window may open.
        if ($u -match '(?i)^https://frontierdeployments\.com/?$') { continue }
        throw ("hardcoded outbound URL in src: " + $u)
    }

    # No telemetry / auto-update hooks.
    foreach ($pat in @(
            '(?i)\btelemetry\b',
            '(?i)ApplicationInsights',
            '(?i)\bSentry\b',
            '(?i)auto.?update',
            '(?i)CheckForUpdate',
            '(?i)Sparkle'
        )) {
        if ($src -match $pat) { throw "telemetry/update match: $pat" }
    }

    # Writes stay under APPDATA\DesktopVitals, LOCALAPPDATA\DesktopVitals, or the snapshot dir.
    # Snapshot path comes from --snapshot (caller-chosen). Config/review/errors use ApplicationData.
    if ($src -notmatch 'SpecialFolder\.ApplicationData\),\s*"DesktopVitals"') {
        throw 'expected APPDATA\\DesktopVitals config/error roots'
    }
    if ($src -notmatch 'SpecialFolder\.LocalApplicationData\),\s*"DesktopVitals"') {
        throw 'expected LOCALAPPDATA\\DesktopVitals state/snapshot roots'
    }

    # Manifest: asInvoker, never requireAdministrator.
    if ($manifest -notmatch 'requestedExecutionLevel\s+level="asInvoker"') {
        throw 'app.manifest missing asInvoker'
    }
    if ($manifest -match 'requireAdministrator') { throw 'app.manifest must not requireAdministrator' }
    if ($manifest -notmatch 'uiAccess="false"') { throw 'app.manifest uiAccess must be false' }

    return ("starts={0}; registry-read; asInvoker" -f $startHits.Count)
}

New-Item -ItemType Directory -Force -Path $Work, $ResultsDir | Out-Null
Save-Config

try {
    # 0. README safety promises as static CI gates
    $staticOk = $false
    $staticDetail = ''
    try {
        $staticDetail = Invoke-SafetyStaticChecks
        $staticOk = $true
    }
    catch {
        $staticDetail = $_.Exception.Message
    }
    Write-Check 'safety-static' $staticOk $staticDetail

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

    # 1b. Built exe embeds asInvoker; session need not be admin (GHA runners often are).
    $elevOk = $false
    $elevDetail = ''
    if (-not $buildOk) {
        Write-Check 'asInvoker-non-admin' $false 'skipped: build failed'
    }
    else {
        try {
            $exe = Join-Path $Root 'bin\DesktopVitals.exe'
            if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }
            $bytes = [System.IO.File]::ReadAllBytes($exe)
            $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
            if ($ascii -notmatch 'asInvoker') { throw 'DesktopVitals.exe missing asInvoker in embedded manifest' }
            if ($ascii -match 'requireAdministrator') { throw 'DesktopVitals.exe embeds requireAdministrator' }
            $id = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($id)
            $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if ($isAdmin) { $elevDetail = 'asInvoker embedded; runner is elevated (app does not request admin)' }
            else { $elevDetail = 'asInvoker embedded; non-admin session' }
            $elevOk = $true
        }
        catch {
            $elevDetail = $_.Exception.Message
        }
        Write-Check 'asInvoker-non-admin' $elevOk $elevDetail
    }

    # 1c. Short runtime outbound + write-root gate (also see tests\safety-runtime.ps1)
    $rtOk = $false
    $rtDetail = ''
    if (-not $buildOk) {
        Write-Check 'safety-runtime' $false 'skipped: build failed'
    }
    else {
        try {
            $rtScript = Join-Path $Root 'tests\safety-runtime.ps1'
            if (-not (Test-Path -LiteralPath $rtScript)) { throw "missing $rtScript" }
            # Child process so safety-runtime's exit code cannot end this smoke session.
            $shell = (Get-Command pwsh -ErrorAction SilentlyContinue)
            if (-not $shell) { $shell = Get-Command powershell }
            $argList = @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $rtScript,
                '-Seconds', '45', '-ResultsDir', $ResultsDir, '-SkipBuild'
            )
            $p = Start-Process -FilePath $shell.Source -ArgumentList $argList -Wait -PassThru -NoNewWindow
            if ($p.ExitCode -ne 0) {
                $log = Join-Path $ResultsDir 'safety-runtime.log'
                $tail = ''
                if (Test-Path -LiteralPath $log) {
                    $tail = (Get-Content -LiteralPath $log -Tail 8 | Out-String).Trim()
                }
                throw ("safety-runtime exit $($p.ExitCode) $tail")
            }
            $rtDetail = '45s gate; see safety-runtime.log'
            $rtOk = $true
        }
        catch {
            $rtDetail = $_.Exception.Message
            if ($rtDetail.Length -gt 240) { $rtDetail = $rtDetail.Substring(0, 240) }
        }
        Write-Check 'safety-runtime' $rtOk $rtDetail
    }

    # 2. Snapshot writes vitals.txt and vitals.png
    $snapOk = $false
    $snapDetail = ''
    try {
        $snapDir = Join-Path $Work 'snap-basic'
        $basic = Invoke-Snapshot -Dir $snapDir
        if ($basic.Text -notmatch 'Desktop Vitals') { throw 'vitals.txt missing product name' }
        if ((Get-Item -LiteralPath $basic.Png).Length -lt 32) { throw 'vitals.png is empty' }
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
        foreach ($name in $expect) {
            if ($scriptsSnap.Text -notmatch [regex]::Escape($name)) { throw "missing $name" }
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

    # 5. A normal snapshot makes no outbound TCP to example.com
    $httpOk = $false
    $httpDetail = ''
    try {
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

    # 6. prefs.txt over 1 MB is left alone; snapshot still works
    $hugeOk = $false
    $hugeDetail = ''
    try {
        $junk = New-Object byte[] (1MB + 1)
        (New-Object Random).NextBytes($junk)
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        [System.IO.File]::WriteAllBytes($PrefsFile, $junk)
        $hugeSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-huge-prefs')
        if ($hugeSnap.Text -notmatch 'Desktop Vitals') { throw 'snapshot text missing' }
        $after = (Get-Item -LiteralPath $PrefsFile).Length
        if ($after -le 1MB) { throw "prefs.txt shrank to $after" }
        $hugeOk = $true
    }
    catch {
        $hugeDetail = $_.Exception.Message
    }
    finally {
        Restore-Config
    }
    Write-Check 'huge-watch-txt' $hugeOk $hugeDetail

    # 7. One snapshot copies hides and pins out of watch.txt, then renames it
    $watchOk = $false
    $watchDetail = ''
    $migWatch = Join-Path $ConfigDir 'watch.txt.migrated'
    $migReview = Join-Path $ConfigDir 'review.txt.migrated'
    $stashWatch = $null
    $stashReview = $null
    try {
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        if (Test-Path -LiteralPath $PrefsFile) { Remove-Item -LiteralPath $PrefsFile -Force }
        if (Test-Path -LiteralPath $migWatch) {
            $stashWatch = Join-Path $Work 'watch.migrated.stash'
            Move-Item -LiteralPath $migWatch -Destination $stashWatch -Force
        }
        if (Test-Path -LiteralPath $migReview) {
            $stashReview = Join-Path $Work 'review.migrated.stash'
            Move-Item -LiteralPath $migReview -Destination $stashReview -Force
        }
        $sample = @"
@hide = HiddenSmoke
KeepMe = notepad.exe
Checked = something | check: http://example.com
@kind = HiddenSmoke | Agent
@temperature-file = C:\not-a-sensor.json
@reviewed = 2020-01-01
@models = C:\no-extra-models
"@
        [System.IO.File]::WriteAllText($ConfigFile, $sample, [System.Text.UTF8Encoding]::new($false))
        $migSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-migrate')
        if (-not (Test-Path -LiteralPath $PrefsFile)) { throw 'prefs.txt was not written' }
        $prefsText = [System.IO.File]::ReadAllText($PrefsFile)
        if ($prefsText -notmatch '(?m)^hide=HiddenSmoke\s*$') { throw 'hide was not copied' }
        if ($prefsText -notmatch 'KeepMe' -or $prefsText -notmatch 'notepad\.exe') { throw 'pin was not copied' }
        if ($prefsText -notmatch 'migratedFromWatch=1') { throw 'migrated flag missing' }
        foreach ($bad in @('temperature', 'reviewed', 'example.com', 'check:', '@kind', '@models')) {
            if ($prefsText.IndexOf($bad, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "prefs stored $bad" }
        }
        if (Test-Path -LiteralPath $ConfigFile) { throw 'watch.txt was not renamed' }
        if (-not (Test-Path -LiteralPath $migWatch)) { throw 'watch.txt.migrated missing' }
        $shown = $migSnap.Text -match 'Imported hides and pins'
        $kept = $prefsText -match 'importNote=1'
        if (-not $shown -and -not $kept) { throw 'import note was not shown or kept' }
        $watchOk = $true
    }
    catch {
        $watchDetail = $_.Exception.Message
    }
    finally {
        if (Test-Path -LiteralPath $migWatch) { Remove-Item -LiteralPath $migWatch -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $migReview) { Remove-Item -LiteralPath $migReview -Force -ErrorAction SilentlyContinue }
        if ($stashWatch -and (Test-Path -LiteralPath $stashWatch)) { Move-Item -LiteralPath $stashWatch -Destination $migWatch -Force }
        if ($stashReview -and (Test-Path -LiteralPath $stashReview)) { Move-Item -LiteralPath $stashReview -Destination $migReview -Force }
        Restore-Config
    }
    Write-Check 'migration-once' $watchOk $watchDetail

    # 8. Default --snapshot folder creates vitals.txt and vitals.png on a fresh path
    $freshOk = $false
    $freshDetail = ''
    try {
        $defaultSnap = Join-Path $env:LOCALAPPDATA 'DesktopVitals\snapshot'
        if (Test-Path -LiteralPath $defaultSnap) { Remove-Item -LiteralPath $defaultSnap -Recurse -Force }
        $exe = Join-Path $Root 'bin\DesktopVitals.exe'
        $p = Start-Process -FilePath $exe -ArgumentList @('--snapshot') -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(70000)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'default snapshot exceeded 70 seconds'
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

    # 9. A script file shows up without a watch.txt label
    $v6Ok = $false
    $v6Detail = ''
    try {
        $v6Script = Join-Path $Work 'dv_smoke_v6.ps1'
        Set-Content -LiteralPath $v6Script -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $v6Script)
        Start-Sleep -Seconds 1
        $v6Snap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-v6')
        if ($v6Snap.Text -notmatch 'dv_smoke_v6\.ps1') { throw 'dv_smoke_v6.ps1 missing' }
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

    # 10. A long argument still leaves the script name visible
    $cmdOk = $false
    $cmdDetail = ''
    try {
        $longScript = Join-Path $Work 'dvsmokeLongCmd.ps1'
        Set-Content -LiteralPath $longScript -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $marker = 'Z' * 20000
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $longScript, $marker)
        Start-Sleep -Seconds 2
        $cmdSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-longcmd')
        if ($cmdSnap.Text -notmatch 'dvsmokeLongCmd') { throw 'dvsmokeLongCmd missing' }
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

    # 12. Perflib locale map (fixture de-DE/ja-JP + live registry + PerfName source gate)
    $localeOk = $false
    $localeDetail = ''
    try {
        $localeScript = Join-Path $Root 'tests\locale-perflib.ps1'
        $localeOut = & $localeScript *>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw ("exit $LASTEXITCODE; " + ($localeOut -replace '\s+', ' ').Trim()) }
        $localeOk = $true
        $n = @($localeOut -split "`r?`n" | Where-Object { $_ -match '^PASS ' }).Count
        $localeDetail = if ($n -gt 0) { "$n subchecks" } else { 'nested exit 0' }
    }
    catch {
        $localeDetail = $_.Exception.Message
    }
    Write-Check 'locale-perflib' $localeOk $localeDetail

    # 13. Models scan must not hang on a junction/symlink loop (BFS + reparse skip)
    $loopOk = $false
    $loopDetail = ''
    $modelsHome = Join-Path $env:USERPROFILE 'models'
    $loopRoot = Join-Path $modelsHome ('dv-smoke-loop-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force -Path $loopRoot | Out-Null
        $a = Join-Path $loopRoot 'a'
        $b = Join-Path $loopRoot 'b'
        New-Item -ItemType Directory -Force -Path $a, $b | Out-Null
        # Directory junctions (no admin). a\to-b -> b, b\to-a -> a.
        cmd.exe /c "mklink /J `"$a\to-b`" `"$b`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "mklink a->b exit $LASTEXITCODE" }
        cmd.exe /c "mklink /J `"$b\to-a`" `"$a`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "mklink b->a exit $LASTEXITCODE" }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-models-loop')
        $sw.Stop()
        $loopOk = $true
        $loopDetail = ("{0} ms" -f $sw.ElapsedMilliseconds)
    }
    catch {
        $loopDetail = $_.Exception.Message
    }
    finally {
        try {
            # Remove junctions before the subfolder. Leave the models folder itself.
            $j1 = Join-Path $loopRoot 'a\to-b'
            $j2 = Join-Path $loopRoot 'b\to-a'
            if (Test-Path -LiteralPath $j1) { cmd.exe /c "rmdir `"$j1`"" | Out-Null }
            if (Test-Path -LiteralPath $j2) { cmd.exe /c "rmdir `"$j2`"" | Out-Null }
            if (Test-Path -LiteralPath $loopRoot) { Remove-Item -LiteralPath $loopRoot -Recurse -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    Write-Check 'models-symlink-loop' $loopOk $loopDetail

    # 14. Secrets in process args never appear in snapshot text, privacy tips, png bytes, or errors.log
    $privOk = $false
    $privDetail = ''
    try {
        $secrets = @(
            'sk-dvsmokeTESTKEY0001',
            'ghp_dvsmokeTESTTOKEN0123456789',
            'dvsmokePass99',
            'dvsmokeBearerTok99',
            'dvsmokeUrlPass',
            'dvsmokeEnv99'
        )
        $privScript = Join-Path $Work 'dv_smoke_priv.ps1'
        Set-Content -LiteralPath $privScript -Value 'Start-Sleep -Seconds 60' -Encoding UTF8
        $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $privScript,
            'dvsmokePrivMarker',
            '--api-key', 'sk-dvsmokeTESTKEY0001',
            '--token', 'ghp_dvsmokeTESTTOKEN0123456789',
            'password=dvsmokePass99',
            'Bearer', 'dvsmokeBearerTok99',
            'https://u:dvsmokeUrlPass@example.com/x',
            'API_KEY=dvsmokeEnv99'
        )
        Start-Sleep -Seconds 2
        $privSnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-privacy') -PrivacyTest
        if ($privSnap.Text -notmatch 'dv_smoke_priv\.ps1' -and $privSnap.Text -notmatch 'dvsmokePrivMarker') { throw 'privacy script missing' }
        if (-not $privSnap.Privacy) { throw 'missing privacy.txt' }
        if ($privSnap.Privacy -notmatch '(?m)^show=') { throw 'privacy.txt missing show=' }
        if ($privSnap.Privacy -notmatch '(?m)^log=') { throw 'privacy.txt missing log=' }
        if ($privSnap.Privacy -notmatch 'Users\\<user>') { throw 'privacy.txt missing username-agnostic path' }
        if ($privSnap.Privacy -notmatch '<redacted>') { throw 'privacy.txt missing <redacted>' }
        $blobs = @(
            $privSnap.Text,
            $privSnap.Privacy,
            [System.IO.File]::ReadAllText($privSnap.Txt),
            [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($privSnap.Png))
        )
        $logPath = Join-Path $ConfigDir 'errors.log'
        if (Test-Path -LiteralPath $logPath) {
            $blobs += [System.IO.File]::ReadAllText($logPath)
        }
        foreach ($secret in $secrets) {
            foreach ($blob in $blobs) {
                if ($blob.IndexOf($secret, [System.StringComparison]::Ordinal) -ge 0) {
                    throw "secret leaked: $secret"
                }
            }
        }
        $privOk = $true
        $privDetail = 'sample+tips redacted'
    }
    catch {
        $privDetail = $_.Exception.Message
    }
    finally {
        Stop-Children
        Restore-Config
    }
    Write-Check 'privacy-secrets' $privOk $privDetail
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

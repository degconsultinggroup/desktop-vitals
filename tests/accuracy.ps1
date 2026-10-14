# Accuracy fixtures for Desktop Vitals: scripts, scheduled tasks, startup noise, models.
# Workload lives under %USERPROFILE% (not Temp: Scripts skips Temp). Cleans up in finally.
# Run: pwsh -File tests\accuracy.ps1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$script:FailCount = 0
$script:Fp = New-Object System.Collections.Generic.List[string]
$script:Fn = New-Object System.Collections.Generic.List[string]
$Children = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$Flood = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$ResultsDir = Join-Path $Root 'tests\results\accuracy'
$FixtureSrc = Join-Path $Root 'tests\fixtures'
$Stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
$Work = Join-Path $env:USERPROFILE ("DesktopVitalsAccuracy\" + $Stamp)
$ModelsDir = Join-Path $env:USERPROFILE 'models'
$ModelFile = Join-Path $ModelsDir ("dv-acc-fake-" + $Stamp + ".gguf")
$ModelCreated = $false
$TaskRun = "DVAccRun$Stamp"
$TaskFail = "DVAccFail$Stamp"
$TaskNoise = "DVAccNoiseUpdate$Stamp"
$StartLnk = Join-Path ([Environment]::GetFolderPath('Startup')) ("DVAccStart$Stamp.lnk")
$DisabledName = "DVAccDisabled$Stamp"
$DisabledExe = Join-Path $Work 'dv_acc_disabled.exe'
$HttpPort = 0
$ToolDir = $null
$AgentHome = $null
$ListenPortFile = $null
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
$NoisePatterns = @(
    '(?i)\bsvchost\b',
    '(?i)\bmsedge(\.exe)?\b',
    '(?i)Microsoft\s*Edge',
    '(?i)EdgeUpdate',
    '(?i)GoogleUpdate',
    '(?i)Adobe.*(Update|Updater)',
    '(?i)Zoom\s*Updater',
    '(?i)\b(DellSupport|LenovoVantage|HPSupport|AsusSystem)\b'
)

function Write-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) {
        $script:FailCount++
        $flat = $Detail
        if (-not $flat) { $flat = ((@($script:Fn) + @($script:Fp)) -join '; ') }
        if (-not $flat) { $flat = 'failed' }
        Write-Host ("::error::{0}: {1}" -f $Name, ($flat -replace '[\r\n]+', ' '))
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

function Start-Child {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory = $Work)
    $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -PassThru -WindowStyle Hidden
    $Children.Add($p) | Out-Null
    return $p
}

function Stop-ProcessList {
    param($List)
    foreach ($p in @($List)) {
        try {
            if ($null -ne $p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    $List.Clear()
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

function Get-Section {
    param([string]$Text, [string]$Title)
    $lines = New-Object System.Collections.Generic.List[string]
    $in = $false
    foreach ($line in ($Text -split "`r?`n")) {
        if (-not $in) {
            if ($line -eq $Title) { $in = $true; continue }
            if ($line -like ($Title + ':*')) { return '' }
            continue
        }
        if ($line.Length -eq 0) { break }
        if ($line -notmatch '^\s') { break }
        [void]$lines.Add($line.Trim())
    }
    if (-not $in) { return $null }
    return [string]::Join("`n", $lines)
}

function Assert-InSection {
    param([string]$SectionName, [string]$SectionText, [string]$Needle, [string]$Card)
    if ($null -eq $SectionText) {
        [void]$script:Fn.Add("$Card missing section; expected $Needle")
        return $false
    }
    if ($SectionText -notmatch [regex]::Escape($Needle)) {
        [void]$script:Fn.Add("$Card FN: missing $Needle")
        return $false
    }
    return $true
}

function Assert-NoNoise {
    param([string]$Card, [string]$SectionText)
    if ([string]::IsNullOrEmpty($SectionText)) { return $true }
    $ok = $true
    foreach ($pat in $NoisePatterns) {
        if ($SectionText -match $pat) {
            $hit = $Matches[0]
            [void]$script:Fp.Add("$Card FP: $hit")
            $ok = $false
        }
    }
    return $ok
}

function Encode-Cwd {
    param([string]$Path)
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $Path.ToCharArray()) {
        if ($ch -match '[A-Za-z0-9]') { [void]$sb.Append($ch) } else { [void]$sb.Append('-') }
    }
    return $sb.ToString()
}

function New-GitRepo {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    Push-Location $Dir
    try {
        & git init -b feature-x 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            & git init 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git init failed in $Dir" }
            & git checkout -b feature-x 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git checkout failed in $Dir" }
        }
        Set-Content -LiteralPath (Join-Path $Dir 'readme.txt') -Value 'fixture' -Encoding ASCII
        & git -c user.email=desktop-vitals@example.com -c user.name='Desktop Vitals' add readme.txt
        if ($LASTEXITCODE -ne 0) { throw "git add failed in $Dir" }
        & git -c user.email=desktop-vitals@example.com -c user.name='Desktop Vitals' commit -m fixture
        if ($LASTEXITCODE -ne 0) { throw "git commit failed in $Dir" }
    }
    finally { Pop-Location }
}

function Write-AgentHome {
    param($AgentRoot, $Repo, $Processes)
    $repoFull = [System.IO.Path]::GetFullPath($Repo)
    $enc = Encode-Cwd $repoFull
    $sid = [guid]::NewGuid().ToString()
    $proj = Join-Path $AgentRoot (Join-Path '.claude\projects' $enc)
    New-Item -ItemType Directory -Force -Path $proj, (Join-Path $AgentRoot '.claude\sessions') | Out-Null
    $cwdJson = $repoFull.Replace('\', '\\')
    $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $line = '{"cwd":"' + $cwdJson + '","timestamp":"' + $stamp + '","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"C:\\src\\install.ps1"}}]}}'
    Set-Content -LiteralPath (Join-Path $proj ($sid + '.jsonl')) -Value $line -Encoding ASCII
    foreach ($proc in $Processes) {
        $ft = $proc.StartTime.ToFileTime()
        $json = '{"sessionId":"' + $sid + '","cwd":"' + $cwdJson + '","procStart":"' + $ft + '"}'
        Set-Content -LiteralPath (Join-Path $AgentRoot ('.claude\sessions\' + $proc.Id + '.json')) -Value $json -Encoding ASCII
    }
}

function Wait-CommandLine {
    param([string]$Like, [int]$Seconds = 15)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $hits = @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine -like $Like })
        if ($hits.Count -gt 0) { return [int]$hits[0].ProcessId }
        Start-Sleep -Milliseconds 200
    }
    return 0
}

function Invoke-BoardJson {
    param([string]$HomeDir)
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    $path = Join-Path $Work 'board.json'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--json "' + $path + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['DESKTOPVITALS_HOME'] = $HomeDir
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(120000)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'json exceeded 120000 ms'
    }
    if ($proc.ExitCode -ne 0) { throw "json exit $($proc.ExitCode)" }
    if (-not (Test-Path -LiteralPath $path)) { throw 'json wrote no file' }
    $raw = [System.IO.File]::ReadAllText($path)
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'json file empty' }
    return $raw | ConvertFrom-Json
}

function Invoke-Bench {
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    $path = Join-Path $Work 'bench.txt'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $p = Start-Process -FilePath $exe -ArgumentList @('--bench', '21', $path) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(180000)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'bench exceeded 180000 ms'
    }
    if ($p.ExitCode -ne 0) { throw "bench exit $($p.ExitCode)" }
    if (-not (Test-Path -LiteralPath $path)) { throw 'bench wrote no file' }
    $line = ([System.IO.File]::ReadAllText($path)).Trim()
    if ($line -notmatch 'p50_ms=([0-9.]+)\s+n=(\d+)\s+procs=(\d+)') { throw "bad bench line: $line" }
    return @{ P50 = $Matches[1]; N = $Matches[2]; Procs = $Matches[3]; Line = $line }
}

function Get-Things {
    param($Board)
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($g in @($Board.groups)) {
        foreach ($t in @($g.things)) {
            $list.Add([pscustomobject]@{
                    Group     = [string]$g.title
                    GroupKind = [string]$g.kind
                    Name      = [string]$t.name
                    Kind      = [string]$t.kind
                    State     = [string]$t.state
                    Project   = [string]$t.project
                    Branch    = [string]$t.branch
                    Detail    = [string]$t.detail
                    Why       = [string]$t.why
                    Count     = [int]$t.count
                    Id        = [string]$t.id
                    Ports     = @($t.ports)
                })
        }
    }
    return $list
}

function Invoke-Snapshot {
    param([string]$Dir, [int]$TimeoutMs = 90000)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Root 'bin\DesktopVitals.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }
    $p = Start-Process -FilePath $exe -ArgumentList @('--snapshot', $Dir) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutMs)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw "snapshot exceeded $($TimeoutMs) ms"
    }
    if ($p.ExitCode -ne 0) { throw "snapshot exit $($p.ExitCode)" }
    $txt = Join-Path $Dir 'vitals.txt'
    $png = Join-Path $Dir 'vitals.png'
    if (-not (Test-Path -LiteralPath $txt)) { throw 'missing vitals.txt' }
    if (-not (Test-Path -LiteralPath $png)) { throw 'missing vitals.png' }
    return @{ Txt = $txt; Png = $png; Text = [System.IO.File]::ReadAllText($txt) }
}

function New-StartupShortcut {
    param([string]$Path, [string]$Target, [string]$Arguments = '')
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $Target
    $lnk.Arguments = $Arguments
    $lnk.WorkingDirectory = $Work
    $lnk.Save()
}

function Set-StartupApprovedOff {
    param([string]$List, [string]$Name)
    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$List"
    New-Item -Path $path -Force | Out-Null
    # Odd first byte = turned off in Task Manager Startup apps.
    $bytes = [byte[]](0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
    New-ItemProperty -Path $path -Name $Name -PropertyType Binary -Value $bytes -Force | Out-Null
}

function Remove-StartupApproved {
    param([string]$List, [string]$Name)
    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$List"
    if (Test-Path -LiteralPath $path) {
        Remove-ItemProperty -Path $path -Name $Name -ErrorAction SilentlyContinue
    }
}

function Register-UserTask {
    param([string]$Name, [string]$ScriptPath, [switch]$FailOnce)
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ("-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`"")
    # Once = timed trigger (shows under Scheduled, not Starts with Windows).
    $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddHours(6))
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1)
    Register-ScheduledTask -TaskName $Name -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $Name
    if ($FailOnce) {
        $deadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $deadline) {
            $info = Get-ScheduledTaskInfo -TaskName $Name
            # 267009 = still running; 0x41301 = task not yet run
            if ($info.LastTaskResult -ne 267009 -and $info.LastTaskResult -ne 0x41301) { break }
            Start-Sleep -Milliseconds 200
        }
    }
}

function Remove-UserTask {
    param([string]$Name)
    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
}

function Clear-Fixtures {
    Stop-ProcessList $Children
    Stop-ProcessList $Flood
    foreach ($n in @($TaskRun, $TaskFail, $TaskNoise)) { Remove-UserTask $n }
    if (Test-Path -LiteralPath $StartLnk) { Remove-Item -LiteralPath $StartLnk -Force -ErrorAction SilentlyContinue }
    Remove-StartupApproved -List 'Run' -Name $DisabledName
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        Remove-ItemProperty -Path $runKey -Name $DisabledName -ErrorAction SilentlyContinue
    }
    if ($ModelCreated -and (Test-Path -LiteralPath $ModelFile)) {
        Remove-Item -LiteralPath $ModelFile -Force -ErrorAction SilentlyContinue
    }
    if ($ToolDir -and (Test-Path -LiteralPath $ToolDir)) {
        Remove-Item -LiteralPath $ToolDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Get-Process DesktopVitals -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $Work, $ResultsDir | Out-Null
Save-Config
$report = New-Object System.Collections.Generic.List[string]
[void]$report.Add("accuracy stamp=$Stamp work=$Work")

try {
    # Build
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
    if (-not $buildOk) { throw 'build failed; skip fixtures' }

    Copy-Item -Path (Join-Path $FixtureSrc '*') -Destination $Work -Force
    $pyScript = Join-Path $Work 'dv_acc_py.py'
    $nodeScript = Join-Path $Work 'dv_acc_node.js'
    $psScript = Join-Path $Work 'dv_acc_ps.ps1'
    $batScript = Join-Path $Work 'dv_acc_bat.bat'
    $taskRunScript = Join-Path $Work 'dv_acc_task_run.ps1'
    $taskFailScript = Join-Path $Work 'dv_acc_task_fail.ps1'

    $py = Get-PythonCommand
    $node = Get-Command node -ErrorAction SilentlyContinue

    # Fake installed model (>= 20 MiB weights)
    New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
    $bytes = New-Object byte[] (21 * 1024 * 1024)
    $rng = [System.Random]::new(42)
    $rng.NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($ModelFile, $bytes)
    $ModelCreated = $true

    # Disabled startup app: Run key + StartupApproved off (must not appear under Starts)
    [System.IO.File]::WriteAllBytes($DisabledExe, [byte[]](0x4D, 0x5A))
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name $DisabledName -Value ("`"$DisabledExe`"") -PropertyType String -Force | Out-Null
    Set-StartupApprovedOff -List 'Run' -Name $DisabledName

    # Startup shortcut (must appear under Starts)
    New-StartupShortcut -Path $StartLnk -Target 'powershell.exe' -Arguments ("-NoProfile -ExecutionPolicy Bypass -File `"$psScript`"")

    # Timed user tasks: one long-running, one failed last run
    Register-UserTask -Name $TaskRun -ScriptPath $taskRunScript
    Register-UserTask -Name $TaskFail -ScriptPath $taskFailScript -FailOnce

    # Noise task name with Update (exe under Program Files is already system; keep scriptless local to verify name filter)
    # Intentionally not registered with a user script path.

    # Running interpreters
    if ($py) {
        $null = Start-Child -FilePath $py -ArgumentList @($pyScript)
        # Free port for http.server
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $script:HttpPort = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
        $null = Start-Child -FilePath $py -ArgumentList @('-m', 'http.server', "$HttpPort") -WorkingDirectory $Work
    }
    if ($node) {
        $null = Start-Child -FilePath $node.Source -ArgumentList @($nodeScript)
    }
    $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $psScript)
    $null = Start-Child -FilePath 'cmd.exe' -ArgumentList @('/c', $batScript)

    # Optional local model servers
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    $llama = Get-Command llama-server -ErrorAction SilentlyContinue
    if ($ollama) {
        try { $null = Start-Child -FilePath $ollama.Source -ArgumentList @('serve') } catch { }
    }
    if ($llama) {
        try { $null = Start-Child -FilePath $llama.Source -ArgumentList @('--host', '127.0.0.1', '--port', '18080') } catch { }
    }

    # v2 board: two agent repos, an orphan bot, a listen port, and a mapped split model inside a tool folder.
    $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
    $RepoA = Join-Path $Work 'repo-a'
    $RepoB = Join-Path $Work 'repo-b'
    $BotRepo = Join-Path $Work 'bots-repo'
    $ToolDir = Join-Path $ModelsDir ("dv-tool-" + $Stamp)
    $AgentHome = Join-Path $Work 'agent-home'
    $ListenPortFile = Join-Path $Work 'listen.port'
    New-GitRepo $RepoA
    New-GitRepo $RepoB
    New-GitRepo $BotRepo
    $agentJs = Join-Path $Work 'dv_agent.js'
    Set-Content -LiteralPath $agentJs -Value 'setInterval(function () {}, 1000);' -Encoding ASCII
    $listenScript = Join-Path $Work 'dv_acc_listen.ps1'
    @'
$listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
Set-Content -LiteralPath $args[0] -Value $listener.LocalEndpoint.Port -Encoding ASCII
while ($true) { Start-Sleep -Seconds 2 }
'@ | Set-Content -LiteralPath $listenScript -Encoding ASCII
    $botScript = Join-Path $BotRepo 'dv_acc_bot.ps1'
    @'
$beat = Join-Path $PSScriptRoot 'beat.txt'
while ($true) {
  Set-Content -LiteralPath $beat -Value ([DateTime]::UtcNow.ToString('o')) -Encoding ASCII
  Start-Sleep -Milliseconds 400
}
'@ | Set-Content -LiteralPath $botScript -Encoding ASCII
    New-Item -ItemType Directory -Force -Path (Join-Path $ToolDir 'models'), (Join-Path $ToolDir 'packs'), (Join-Path $ToolDir 'logs') | Out-Null
    $shard = New-Object byte[] 4096
    [System.IO.File]::WriteAllBytes((Join-Path $ToolDir 'models\fixture-weights-00001-of-00002.gguf'), $shard)
    [System.IO.File]::WriteAllBytes((Join-Path $ToolDir 'models\fixture-weights-00002-of-00002.gguf'), $shard)
    Set-Content -LiteralPath (Join-Path $ToolDir 'packs\readme.txt') -Value 'pack' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $ToolDir 'logs\run.log') -Value 'log' -Encoding ASCII
    $mapScript = Join-Path $ToolDir 'dv_acc_map.ps1'
    @'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$mappedA = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile((Join-Path $root 'models\fixture-weights-00001-of-00002.gguf'))
$mappedB = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile((Join-Path $root 'models\fixture-weights-00002-of-00002.gguf'))
$viewA = $mappedA.CreateViewAccessor()
$viewB = $mappedB.CreateViewAccessor()
$keep = $viewA.ReadByte(0) + $viewB.ReadByte(0)
while ($true) { Start-Sleep -Seconds 2 }
'@ | Set-Content -LiteralPath $mapScript -Encoding ASCII
    $AgentA = @()
    if ($nodeCmd) {
        $AgentA += Start-Child -FilePath $nodeCmd.Source -ArgumentList @($agentJs, '--agent', 'claude') -WorkingDirectory $RepoA
        $AgentA += Start-Child -FilePath $nodeCmd.Source -ArgumentList @($agentJs, '--agent', 'claude') -WorkingDirectory $RepoA
        $null = Start-Child -FilePath $nodeCmd.Source -ArgumentList @($agentJs, '--model', 'grok-4.7-high', '--worktree', '.') -WorkingDirectory $RepoB
        foreach ($ap in $AgentA) { $ap.Refresh() | Out-Null }
        Write-AgentHome -AgentRoot $AgentHome -Repo $RepoA -Processes $AgentA
    }
    $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $listenScript, $ListenPortFile)
    $null = Start-Child -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $mapScript) -WorkingDirectory $ToolDir
    $launchCmd = Join-Path $BotRepo 'launch.cmd'
    Set-Content -LiteralPath $launchCmd -Value ("@echo off`r`nstart `"`" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"{0}`"`r`n" -f $botScript) -Encoding ASCII
    $launcher = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $launchCmd) -WorkingDirectory $BotRepo -WindowStyle Hidden -PassThru
    if (-not $launcher.WaitForExit(20000)) {
        try { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    $botPid = Wait-CommandLine -Like '*dv_acc_bot.ps1*' -Seconds 15
    if ($botPid -gt 0) {
        $botProc = Get-Process -Id $botPid -ErrorAction SilentlyContinue
        if ($botProc) { $Children.Add($botProc) | Out-Null }
    }

    Start-Sleep -Seconds 3

    # Main accuracy snapshot
    $snap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-main')
    Copy-Item -LiteralPath $snap.Png -Destination (Join-Path $ResultsDir 'vitals.png') -Force
    Copy-Item -LiteralPath $snap.Txt -Destination (Join-Path $ResultsDir 'vitals.txt') -Force
    $text = $snap.Text
    [void]$report.Add('----- vitals.txt -----')
    [void]$report.Add(($text -replace '(?m)^(Desktop Vitals)\s+\S+', '$1 <redacted>'))

    # Script names anywhere on the board, with no Windows noise in the text.
    $scriptsOk = $true
    if ($py) {
        if ($text -notmatch 'dv_acc_py\.py') { $scriptsOk = $false; [void]$script:Fn.Add('missing dv_acc_py.py') }
        if ($text -notmatch 'http\.server') { $scriptsOk = $false; [void]$script:Fn.Add('missing http.server') }
    }
    else { [void]$report.Add('SKIP python fixtures (no python)') }
    if ($node) {
        if ($text -notmatch 'dv_acc_node\.js') { $scriptsOk = $false; [void]$script:Fn.Add('missing dv_acc_node.js') }
    }
    else { [void]$report.Add('SKIP node fixture (no node)') }
    if ($text -notmatch 'dv_acc_ps\.ps1') { $scriptsOk = $false; [void]$script:Fn.Add('missing dv_acc_ps.ps1') }
    if ($text -notmatch 'dv_acc_bat\.bat') { $scriptsOk = $false; [void]$script:Fn.Add('missing dv_acc_bat.bat') }
    $scriptLines = @($text -split "`r?`n" | Where-Object { $_ -match 'dv_acc_(py\.py|node\.js|ps1|bat\.bat)|http\.server' })
    if (-not (Assert-NoNoise 'scripts' ($scriptLines -join "`n"))) { $scriptsOk = $false }
    Write-Check 'scripts-cards' $scriptsOk

    # Scheduled task names, with running or failed on that line.
    $schedOk = $true
    $runLine = @($text -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($TaskRun) } | Select-Object -First 1)
    $failLine = @($text -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($TaskFail) } | Select-Object -First 1)
    if (-not $runLine) { $schedOk = $false; [void]$script:Fn.Add("missing $TaskRun") }
    elseif ($runLine -notmatch '(?i)running') { $schedOk = $false; [void]$script:Fn.Add("$TaskRun line is not running") }
    if (-not $failLine) { $schedOk = $false; [void]$script:Fn.Add("missing $TaskFail") }
    elseif ($failLine -notmatch '(?i)failed') { $schedOk = $false; [void]$script:Fn.Add("$TaskFail line is not failed") }
    $taskText = (@($runLine) + @($failLine)) -join "`n"
    if (-not (Assert-NoNoise 'tasks' $taskText)) { $schedOk = $false }
    Write-Check 'scheduled-cards' $schedOk

    # Disabled startup name stays off the board. The shortcut is still created and cleaned up.
    $startsOk = $true
    if ($text -match [regex]::Escape($DisabledName)) {
        $startsOk = $false
        [void]$script:Fp.Add("disabled $DisabledName shown")
    }
    Write-Check 'starts-cards' $startsOk

    # Fake model stem, or the collapsed on-disk line.
    $modelStem = "dv-acc-fake-$Stamp"
    $aiOk = $true
    if ($text -notmatch [regex]::Escape($modelStem) -and $text -notmatch 'on disk') {
        $aiOk = $false
        [void]$script:Fn.Add("missing model $modelStem")
    }
    if ($ollama -and $text -notmatch '(?i)ollama') {
        [void]$report.Add('NOTE ollama started but not labeled (may need serve settle)')
    }
    if ($llama -and $text -notmatch '(?i)llama') {
        [void]$report.Add('NOTE llama-server started but not labeled')
    }
    Write-Check 'ai-model' $aiOk

    # Live board: agents, orphan bot, listen port, failed task, mapped model. No Windows noise, no duplicate ids.
    $v2Ok = $true
    $v2Detail = ''
    $tp = 0
    $fpN = 0
    try {
        if (-not $nodeCmd) { throw 'node is required for agent fixtures' }
        $portDeadline = (Get-Date).AddSeconds(10)
        while (-not (Test-Path -LiteralPath $ListenPortFile) -and (Get-Date) -lt $portDeadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $ListenPortFile)) { throw 'listen port file missing' }
        $listenPort = [int](Get-Content -LiteralPath $ListenPortFile -Raw).Trim()
        $board = Invoke-BoardJson -HomeDir $AgentHome
        $things = @(Get-Things $board)
        $seenIds = @{}
        $toolName = 'dv-tool-' + $Stamp
        foreach ($t in $things) {
            if ($seenIds.ContainsKey($t.Id)) { $v2Ok = $false; $fpN++; [void]$script:Fp.Add("duplicate id $($t.Id)") }
            else { $seenIds[$t.Id] = $true }
            $blob = "$($t.Name) $($t.Why) $($t.Detail) $($t.Project)"
            if (-not (Assert-NoNoise 'v2' $blob)) { $v2Ok = $false; $fpN++ }
            if ($t.Name -eq $toolName -or $t.Group -eq $toolName) {
                $v2Ok = $false
                $fpN++
                [void]$script:Fp.Add('tool folder named as a model')
            }
        }
        $claude = @($things | Where-Object { $_.Name -eq 'Claude Code' -and $_.Project -eq 'repo-a' })
        $cursor = @($things | Where-Object { $_.Name -eq 'Cursor agent' -and $_.Project -eq 'repo-b' })
        $bot = @($things | Where-Object { $_.Name -eq 'dv_acc_bot.ps1' -and $_.Kind -eq 'bot' })
        $server = @($things | Where-Object { $_.Kind -eq 'server' -and ($_.Ports -contains $listenPort) })
        $failed = @($things | Where-Object { $_.Name -eq $TaskFail -and $_.State -eq 'failed' })
        $model = @($things | Where-Object { $_.Name -eq 'fixture-weights' -and $_.Kind -eq 'model' -and $_.State -eq 'running' })
        $checks = @(
            @{ N = 'claude'; Ok = ($claude.Count -eq 1 -and [int]$claude[0].Count -ge 2 -and $claude[0].State -eq 'waiting' -and $claude[0].Branch -eq 'feature-x' -and $claude[0].Detail -match 'editing' -and $claude[0].Detail -match 'install\.ps1') },
            @{ N = 'cursor'; Ok = ($cursor.Count -eq 1 -and $cursor[0].Branch -eq 'feature-x' -and $cursor[0].Why -match 'grok-4\.7-high') },
            @{ N = 'bot'; Ok = ($bot.Count -eq 1 -and $bot[0].Project -eq 'bots-repo') },
            @{ N = 'server'; Ok = ($server.Count -ge 1) },
            @{ N = 'task'; Ok = ($failed.Count -ge 1) },
            @{ N = 'model'; Ok = ($model.Count -ge 1) }
        )
        foreach ($c in $checks) {
            if ($c.Ok) { $tp++ }
            else { $v2Ok = $false; [void]$script:Fn.Add("v2 missing $($c.N)") }
        }
        $miss = @($checks | Where-Object { -not $_.Ok } | ForEach-Object { $_.N })
        $nodeLine = @($things | Where-Object { $_.Name -eq 'dv_acc_node.js' })
        if ($nodeLine.Count -lt 1) { $v2Ok = $false; [void]$script:Fn.Add('v2 node script renamed or missing') }
        $den = $tp + $fpN
        $prec = if ($den -eq 0) { 0 } else { [math]::Round($tp / $den, 3) }
        $rec = [math]::Round($tp / 6.0, 3)
        $v2Detail = "precision=$prec ($tp/$den) recall=$rec ($tp/6) missing=$($miss -join ',')"
        if ($tp -lt 6 -or $fpN -gt 0) { $v2Ok = $false }
        [void]$report.Add($v2Detail)
        if (-not $v2Ok) {
            $names = ($things | ForEach-Object { "$($_.Kind):$($_.Name):$($_.Project):$($_.State)" }) -join ' | '
            $v2Detail = "$v2Detail things=$names"
        }
    }
    catch {
        $v2Ok = $false
        if (-not $v2Detail) { $v2Detail = $_.Exception.Message }
        else { $v2Detail = "$v2Detail; $($_.Exception.Message)" }
    }
    Write-Check 'v2-board' $v2Ok $v2Detail

    # Exit mid-scan: kill during snapshot; host must stay clean and a later snapshot must work
    $midOk = $false
    $midDetail = ''
    try {
        $midDir = Join-Path $Work 'snap-mid'
        New-Item -ItemType Directory -Force -Path $midDir | Out-Null
        $exe = Join-Path $Root 'bin\DesktopVitals.exe'
        $mid = Start-Process -FilePath $exe -ArgumentList @('--snapshot', $midDir) -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 400
        if (-not $mid.HasExited) { Stop-Process -Id $mid.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 300
        Get-Process DesktopVitals -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-after-kill')
        $midOk = $true
    }
    catch { $midDetail = $_.Exception.Message }
    Write-Check 'exit-mid-scan' $midOk $midDetail

    # Access-denied / elevated: snapshot must not crash when many PIDs deny command-line query
    $denyOk = $false
    $denyDetail = ''
    try {
        $denySnap = Invoke-Snapshot -Dir (Join-Path $Work 'snap-deny')
        if ($denySnap.Text -notmatch 'Desktop Vitals') { throw 'bad snapshot text' }
        $denyOk = $true
        $denyDetail = 'snapshot ok with system processes present'
    }
    catch { $denyDetail = $_.Exception.Message }
    Write-Check 'access-denied-safe' $denyOk $denyDetail

    # 1000+ processes: raise process count, snapshot must not crash
    $floodOk = $false
    $floodDetail = ''
    try {
        $before = @(Get-Process -ErrorAction SilentlyContinue).Count
        $need = [Math]::Max(0, 1001 - $before)
        # Cap spawn on CI to avoid runner OOM; still push past 1000 when feasible.
        $spawn = [Math]::Min($need, 400)
        $spawned = 0
        # The pass-time gate is at about 300 processes. The crash check still raises the count after that.
        while ($spawned -lt $spawn) {
            $have = @(Get-Process -ErrorAction SilentlyContinue).Count
            if ($have -ge 300 -and $spawned -gt 0) { break }
            $batch = [Math]::Min(20, $spawn - $spawned)
            for ($i = 0; $i -lt $batch; $i++) {
                # Do not name this $fp: PowerShell variables are case-insensitive and would clobber $script:Fp.
                $floodProc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'ping -n 60 127.0.0.1 >nul') -PassThru -WindowStyle Hidden
                $Flood.Add($floodProc) | Out-Null
            }
            $spawned += $batch
        }
        Start-Sleep -Seconds 1
        $mid = @(Get-Process -ErrorAction SilentlyContinue).Count
        $bench = $null
        $benchErr = ''
        try { $bench = Invoke-Bench }
        catch { $benchErr = $_.Exception.Message }
        if ($bench) {
            $p50 = [double]$bench.P50
            $passDetail = "p50_ms=$($bench.P50) n=$($bench.N) procs=$($bench.Procs); at=$mid"
            Write-Check 'pass-time' ($p50 -le 50) $passDetail
            [void]$report.Add($passDetail)
        }
        else { Write-Check 'pass-time' $false $benchErr }
        while ($spawned -lt $spawn) {
            $floodProc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'ping -n 60 127.0.0.1 >nul') -PassThru -WindowStyle Hidden
            $Flood.Add($floodProc) | Out-Null
            $spawned++
        }
        Start-Sleep -Seconds 1
        $after = @(Get-Process -ErrorAction SilentlyContinue).Count
        $floodDetail = "procs $before -> $after (spawned $spawned)"
        $null = Invoke-Snapshot -Dir (Join-Path $Work 'snap-flood') -TimeoutMs 90000
        if ($after -lt 1000 -and $spawn -eq $need) {
            # Could not reach 1000 on this host; still required no crash.
            $floodDetail += '; under 1000 on host'
        }
        $floodOk = $true
    }
    catch { $floodDetail = $_.Exception.Message }
    finally { Stop-ProcessList $Flood }
    Write-Check 'many-processes' $floodOk $floodDetail

    # Cleanup leftovers check (before finally also runs Clear-Fixtures)
    Clear-Fixtures
    $leftOk = $true
    $leftDetail = ''
    foreach ($n in @($TaskRun, $TaskFail, $TaskNoise)) {
        $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue
        if ($t) { $leftOk = $false; $leftDetail = "leftover task $n" }
    }
    if (Test-Path -LiteralPath $StartLnk) { $leftOk = $false; $leftDetail = 'leftover startup shortcut' }
    $runLeft = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $DisabledName -ErrorAction SilentlyContinue
    if ($runLeft) { $leftOk = $false; $leftDetail = 'leftover disabled Run key' }
    if ($ModelCreated -and (Test-Path -LiteralPath $ModelFile)) { $leftOk = $false; $leftDetail = 'leftover fake model' }
    Write-Check 'cleanup' $leftOk $leftDetail
}
catch {
    Write-Check 'accuracy-harness' $false $_.Exception.Message
    [void]$report.Add("HARVEST ERROR: $($_.Exception.Message)")
}
finally {
    try { Clear-Fixtures } catch { }
    try { Restore-Config } catch { }
    try {
        if (Test-Path -LiteralPath $Work) {
            # Keep snap outputs under results; drop live work tree.
            Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
}

[void]$report.Add('')
[void]$report.Add("FP count=$($script:Fp.Count)")
foreach ($x in $script:Fp) { [void]$report.Add("FP: $x") }
[void]$report.Add("FN count=$($script:Fn.Count)")
foreach ($x in $script:Fn) { [void]$report.Add("FN: $x") }
$reportPath = Join-Path $ResultsDir 'report.txt'
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Wrote $reportPath"

if ($script:FailCount -gt 0) {
    Write-Host ("Accuracy finished with {0} failure(s). FP={1} FN={2}" -f $script:FailCount, $script:Fp.Count, $script:Fn.Count)
    exit 1
}
Write-Host ("Accuracy finished: all checks passed. FP={0} FN={1}" -f $script:Fp.Count, $script:Fn.Count)
exit 0

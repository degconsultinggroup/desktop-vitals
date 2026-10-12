# Registers "\DEG\DEG Desktop Status" for the interactive user and creates the desktop shortcut.
# Safe to re-run. If the task already belongs to an admin, registration needs an elevated PowerShell.
#
# The old task had only a logon trigger. Its 2-minute repetition had no duration and
# StopAtDurationEnd was true, so after one start the board never came back.
# This script registers two triggers: logon, plus a clock that starts one minute
# from registration and repeats every 2 minutes for 3650 days with StopAtDurationEnd false.
# The task starts the overlay and leaves it running. PT0S is an unlimited execution time.
# IgnoreNew means a later tick does nothing while that process is still up.
param(
    [string]$EmitXml,
    [string]$UserId,
    [string]$RegisteredAt,
    [string]$ExistingXml,
    [string]$BackupDir,
    [switch]$SimulateAccessDenied
)

$ErrorActionPreference = 'Stop'
if ($SimulateAccessDenied) {
    Write-Host 'Access is denied. Run this script from an elevated PowerShell.'
    exit 1
}
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$run = Join-Path $d 'run-desktop-status.vbs'
if (-not $UserId) { $UserId = "$env:USERDOMAIN\$env:USERNAME" }
if ($RegisteredAt) {
    $registered = [datetime]::ParseExact($RegisteredAt, 'yyyy-MM-ddTHH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
} else {
    $registered = Get-Date
}
$start = $registered.AddMinutes(1)
$startText = $start.ToString('yyyy-MM-ddTHH:mm:ss')
# About ten years. An empty duration is what made the old repetition stop immediately.
$repeatEvery = 'PT2M'
$repeatFor = 'P3650D'

function Escape-Xml([string]$s) {
    if ($null -eq $s) { return '' }
    return ($s -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;' -replace '"', '&quot;' -replace "'", '&apos;')
}

$userXml = Escape-Xml $UserId
$runXml = Escape-Xml $run
$dirXml = Escape-Xml $d
$argXml = Escape-Xml ('"' + $run + '"')

$xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts the desktop status overlay. A later tick does nothing while it is running.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$userXml</UserId>
    </LogonTrigger>
    <TimeTrigger>
      <Repetition>
        <Interval>$repeatEvery</Interval>
        <Duration>$repeatFor</Duration>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>$startText</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$userXml</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>wscript.exe</Command>
      <Arguments>$argXml</Arguments>
      <WorkingDirectory>$dirXml</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

function Backup-OldTask([string]$destDir) {
    $source = $null
    $exported = $null
    if ($ExistingXml) {
        if (-not (Test-Path -LiteralPath $ExistingXml)) { throw "ExistingXml not found: $ExistingXml" }
        $source = $ExistingXml
    } elseif (-not $EmitXml) {
        $task = Get-ScheduledTask -TaskPath '\DEG\' -TaskName 'DEG Desktop Status' -ErrorAction SilentlyContinue
        if ($task) {
            $exported = Export-ScheduledTask -TaskPath '\DEG\' -TaskName 'DEG Desktop Status'
        }
    }
    if (-not $source -and -not $exported) { return $null }
    if (-not $destDir) { $destDir = Join-Path $d 'runtime\task-backups' }
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }
    $dest = Join-Path $destDir ("DEG-Desktop-Status-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + ".xml")
    if ($source) {
        Copy-Item -LiteralPath $source -Destination $dest
    } else {
        Set-Content -LiteralPath $dest -Value $exported -Encoding Unicode
    }
    return $dest
}

# Back up first, then replace. A second run writes the same definition again.
$backedUp = Backup-OldTask $BackupDir

if ($EmitXml) {
    $parent = Split-Path -Parent $EmitXml
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $full = [System.IO.Path]::GetFullPath($EmitXml)
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($full, $xml.Trim(), $utf8)
    Write-Host "Wrote task XML:" $full
    if ($backedUp) { Write-Host "Backed up previous task:" $backedUp }
    exit 0
}

if (-not (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue)) {
    throw 'Register-ScheduledTask is not available. Re-run this on Windows.'
}
try {
    Register-ScheduledTask -Xml $xml.Trim() -TaskPath '\DEG\' -TaskName 'DEG Desktop Status' -Force -ErrorAction Stop | Out-Null
} catch {
    $denied = $false
    $cursor = $_.Exception
    while ($null -ne $cursor) {
        $message = [string]$cursor.Message
        $code = 0
        if ($cursor.HResult) { $code = [int]$cursor.HResult }
        if ($message -like '*Access is denied*' -or $code -eq 0x80070005) { $denied = $true }
        $cursor = $cursor.InnerException
    }
    if (-not $denied) { throw }
    Write-Host 'Access is denied. Run this script from an elevated PowerShell.'
    exit 1
}
$runtime = Join-Path $d 'runtime'
if (-not (Test-Path -LiteralPath $runtime)) { New-Item -ItemType Directory -Force -Path $runtime | Out-Null }
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PC Status.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = Join-Path $runtime 'status.html'
$s.IconLocation = 'shell32.dll,167'
$s.Description = 'What is going on with my PC'
$s.Save()
Write-Host 'Registered. Shortcut:' $lnk
if ($backedUp) { Write-Host "Backed up previous task:" $backedUp }

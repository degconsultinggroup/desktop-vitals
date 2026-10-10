# Builds Desktop Vitals and adds a Start menu shortcut (Ctrl+Alt+P) and a desktop shortcut.
# Safe to re-run. Nothing starts at logon; open it when you want it.
$ErrorActionPreference = 'Stop'
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $d 'build.ps1')
$exe = Join-Path $d 'bin\DesktopVitals.exe'
$shell = New-Object -ComObject WScript.Shell
function Make-Shortcut([string]$path, [string]$hotkey) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Join-Path $d 'bin'
    $s.IconLocation = "$exe,0"
    $s.Description = 'What this PC is doing: system load, scripts, scheduled tasks, bots'
    if ($hotkey) { $s.Hotkey = $hotkey }
    $s.Save()
    Write-Host 'Shortcut:' $path
}
Make-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Desktop Vitals.lnk') 'CTRL+ALT+P'
Make-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desktop Vitals.lnk') $null

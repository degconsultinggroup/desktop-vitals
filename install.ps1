# Builds Desktop Vitals and adds a Start menu shortcut (Ctrl+Alt+P) and a desktop shortcut.
# Safe to re-run. Nothing starts at logon.
$ErrorActionPreference = 'Stop'
$uninstall = $env:DV_UNINSTALL -eq '1'
foreach ($a in @($args)) { if ($a -eq '-Uninstall' -or $a -eq '/Uninstall') { $uninstall = $true } }
if ($uninstall) {
    foreach ($p in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Desktop Vitals.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desktop Vitals.lnk'))) {
        if ($p -and (Test-Path -LiteralPath $p)) { Remove-Item -LiteralPath $p -Force }
    }
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\DesktopVitals'
    if (Test-Path -LiteralPath $installed) { Remove-Item -LiteralPath $installed -Recurse -Force }
    return
}
function Make-Shortcut([string]$path, [string]$exe, [string]$hotkey) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Split-Path -Parent $exe
    $s.IconLocation = "$exe,0"
    $s.Description = 'What you started on this PC'
    if ($hotkey) { $s.Hotkey = $hotkey }
    $s.Save()
    Write-Host 'Shortcut:' $path
}
function Add-Shortcuts([string]$exe) {
    try { Make-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Desktop Vitals.lnk') $exe 'CTRL+ALT+P' }
    catch { Write-Warning "Start menu shortcut failed: $_" }
    try { Make-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desktop Vitals.lnk') $exe $null }
    catch { Write-Warning "Desktop shortcut failed: $_" }
}
if (-not $MyInvocation.MyCommand.Path) {
    $root = Join-Path $env:LOCALAPPDATA 'Programs\DesktopVitals'
    $src = Join-Path $root 'src'
    $zip = Join-Path $env:TEMP 'desktop-vitals.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/degconsultinggroup/desktop-vitals/archive/refs/heads/main.zip' -OutFile $zip
    if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $root -Force
    $unpacked = Join-Path $root 'desktop-vitals-main'
    if (-not (Test-Path -LiteralPath $unpacked)) { throw 'download did not include the source' }
    Rename-Item -LiteralPath $unpacked -NewName 'src'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1')
    $exe = Join-Path $root 'DesktopVitals.exe'
    Copy-Item (Join-Path $src 'bin\DesktopVitals.exe') $exe -Force
    Remove-Item -LiteralPath $src -Recurse -Force
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Add-Shortcuts $exe
    Start-Process -FilePath $exe
    return
}
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $d 'build.ps1')
$exe = Join-Path $d 'bin\DesktopVitals.exe'
Add-Shortcuts $exe

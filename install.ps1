# Installs the latest Desktop Vitals GitHub Release for the current user.
# No admin. Safe to re-run. Removes only what it added with -Uninstall.
# Works as: irm <url> | iex, and as powershell -File .\install.ps1
# Piped iex does not receive param(); switches also come from DV_UNINSTALL=1 and DV_NOLAUNCH=1.
# DV_INSTALL_DIR overrides the install folder and skips the Start menu shortcut (tests).
# DV_DOWNLOAD_URL overrides the download base: an http(s) URL, or a local folder that holds DesktopVitals.exe and SHA256SUMS.txt.
$script:DvPrevEap = $ErrorActionPreference
$ErrorActionPreference = 'Stop'

$script:DvAsFile = $false
if ($MyInvocation.MyCommand.Path -and (Test-Path -LiteralPath $MyInvocation.MyCommand.Path)) {
    $script:DvAsFile = $true
}

function Test-DvSwitch {
    param([string]$Name)
    foreach ($a in $script:DvArgList) {
        $t = [string]$a
        if ($t -match ('^(?i)(-|/|--)' + [regex]::Escape($Name) + '$')) { return $true }
    }
    return $false
}

$script:DvArgList = @($args)

$script:DvUninstall = (Test-DvSwitch 'Uninstall') -or ($env:DV_UNINSTALL -eq '1')
$script:DvNoLaunch = (Test-DvSwitch 'NoLaunch') -or ($env:DV_NOLAUNCH -eq '1')

function Get-DvInstallDir {
    if (-not [string]::IsNullOrWhiteSpace($env:DV_INSTALL_DIR)) { return $env:DV_INSTALL_DIR }
    return (Join-Path $env:LOCALAPPDATA 'Programs\DesktopVitals')
}

function Get-DvShortcut {
    $programs = [Environment]::GetFolderPath('Programs')
    if ([string]::IsNullOrWhiteSpace($programs)) { return $null }
    return (Join-Path $programs 'Desktop Vitals.lnk')
}

function Test-DvDefaultDir {
    return [string]::IsNullOrWhiteSpace($env:DV_INSTALL_DIR)
}

function Stop-DvInstalled {
    param([string]$ExePath)
    if (-not (Test-Path -LiteralPath $ExePath)) { return }
    $full = [System.IO.Path]::GetFullPath($ExePath)
    $procs = @(Get-Process -Name 'DesktopVitals' -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        $path = $null
        try { $path = $p.Path } catch { }
        if (-not $path) { continue }
        try { $path = [System.IO.Path]::GetFullPath($path) } catch { continue }
        if ($path -eq $full) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Read-DvHash {
    param([string]$SumsPath)
    foreach ($raw in @(Get-Content -LiteralPath $SumsPath)) {
        $line = ([string]$raw).Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $parts = $line -split '\s+', 2
        if ($parts.Count -lt 2) { continue }
        $name = $parts[1].Trim()
        if ($name.StartsWith('*')) { $name = $name.Substring(1) }
        $name = [System.IO.Path]::GetFileName($name.Trim().Trim('"'))
        if ($name -ne 'DesktopVitals.exe') { continue }
        return $parts[0].Trim()
    }
    return $null
}

function Copy-DvPayload {
    param([string]$DestExe, [string]$DestSums)
    $override = $env:DV_DOWNLOAD_URL
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        $base = $override.Trim().TrimEnd('\', '/')
        if ($base -match '^(?i)file://') {
            $base = $base -replace '^(?i)file:///', ''
            $base = $base -replace '^(?i)file://', ''
            $base = $base -replace '/', '\'
        }
        if ($base -match '^(?i)https?://') {
            Save-DvUrl (Join-Url $base 'DesktopVitals.exe') $DestExe
            Save-DvUrl (Join-Url $base 'SHA256SUMS.txt') $DestSums
            return
        }
        if (-not (Test-Path -LiteralPath $base)) { throw 'DV_DOWNLOAD_URL is not a folder or an http(s) address.' }
        $srcExe = Join-Path $base 'DesktopVitals.exe'
        $srcSums = Join-Path $base 'SHA256SUMS.txt'
        if (-not (Test-Path -LiteralPath $srcExe)) { throw 'DV_DOWNLOAD_URL has no DesktopVitals.exe.' }
        if (-not (Test-Path -LiteralPath $srcSums)) { throw 'DV_DOWNLOAD_URL has no SHA256SUMS.txt.' }
        Copy-Item -LiteralPath $srcExe -Destination $DestExe -Force
        Copy-Item -LiteralPath $srcSums -Destination $DestSums -Force
        return
    }
    $base = 'https://github.com/degconsultinggroup/desktop-vitals/releases/latest/download'
    Save-DvUrl (Join-Url $base 'DesktopVitals.exe') $DestExe
    Save-DvUrl (Join-Url $base 'SHA256SUMS.txt') $DestSums
}

function Join-Url {
    param([string]$Base, [string]$Name)
    return ($Base.TrimEnd('/') + '/' + $Name)
}

function Save-DvUrl {
    param([string]$Url, [string]$Dest)
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $prev = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing -Headers @{ 'User-Agent' = 'DesktopVitals-install' }
    }
    catch {
        throw 'Could not download Desktop Vitals. Check the latest GitHub Release.'
    }
    finally {
        $ProgressPreference = $prev
    }
}

function Assert-DvHash {
    param([string]$ExePath, [string]$SumsPath)
    $expect = Read-DvHash $SumsPath
    if (-not $expect -or $expect -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'SHA256SUMS.txt has no hash for DesktopVitals.exe. Install aborted.'
    }
    $actual = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    if (-not $actual.Equals($expect, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SHA256 mismatch for DesktopVitals.exe. Install aborted.'
    }
}

function Install-DvShortcut {
    param([string]$ExePath)
    $lnk = Get-DvShortcut
    if (-not $lnk) { throw 'Could not find the Start menu folder.' }
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $ExePath
    $s.WorkingDirectory = Split-Path -Parent $ExePath
    $s.IconLocation = "$ExePath,0"
    $s.Description = 'What this PC is doing'
    $s.Hotkey = 'CTRL+ALT+P'
    $s.Save()
}

function Remove-DvInstall {
    $dir = Get-DvInstallDir
    $exe = Join-Path $dir 'DesktopVitals.exe'
    if (Test-DvDefaultDir) {
        $lnk = Get-DvShortcut
        if ($lnk -and (Test-Path -LiteralPath $lnk)) { Remove-Item -LiteralPath $lnk -Force }
    }
    if (Test-Path -LiteralPath $exe) { Stop-DvInstalled $exe }
    if (Test-DvDefaultDir) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    }
    elseif (Test-Path -LiteralPath $exe) {
        Remove-Item -LiteralPath $exe -Force
    }
    Write-Host 'Removed Desktop Vitals'
}

function Install-Dv {
    $work = Join-Path $env:TEMP ('dv-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $gotExe = Join-Path $work 'DesktopVitals.exe'
    $gotSums = Join-Path $work 'SHA256SUMS.txt'
    try {
        Copy-DvPayload $gotExe $gotSums
        Assert-DvHash $gotExe $gotSums
        $dir = Get-DvInstallDir
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $exe = Join-Path $dir 'DesktopVitals.exe'
        if (Test-Path -LiteralPath $exe) { Stop-DvInstalled $exe }
        Copy-Item -LiteralPath $gotExe -Destination $exe -Force
        if (Test-DvDefaultDir) { Install-DvShortcut $exe }
        if (-not $script:DvNoLaunch) { Start-Process -FilePath $exe }
        Write-Host 'Installed Desktop Vitals'
    }
    finally {
        if (Test-Path -LiteralPath $work) {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    if ($script:DvUninstall) { Remove-DvInstall }
    else { Install-Dv }
    if ($script:DvAsFile) { exit 0 }
    $global:LASTEXITCODE = 0
}
catch {
    $msg = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($msg)) { $msg = 'Desktop Vitals install failed.' }
    if ($script:DvAsFile) {
        Write-Host $msg
        exit 1
    }
    $global:LASTEXITCODE = 1
    throw $msg
}
finally {
    $ErrorActionPreference = $script:DvPrevEap
}

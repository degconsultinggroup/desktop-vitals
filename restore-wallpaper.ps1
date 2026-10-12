# restore-wallpaper.ps1 - puts the original wallpaper back (from backup\original-wallpaper.jpg) and turns the live status off.
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $d 'unregister-desktop-status.ps1')
$cfg = Get-Content (Join-Path $d 'backup\wallpaper-settings.json') -Raw | ConvertFrom-Json
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool SystemParametersInfo(int a,int b,string c,int d);'
$img = Join-Path $d 'backup\original-wallpaper.jpg'
[void][W.U]::SystemParametersInfo(0x14, 0, $img, 3)
Set-ItemProperty 'HKCU:\Control Panel\Desktop' WallpaperStyle ([string]$cfg.WallpaperStyle)
Set-ItemProperty 'HKCU:\Control Panel\Desktop' TileWallpaper ([string]$cfg.TileWallpaper)
Write-Host 'Original wallpaper restored; live status task removed.'

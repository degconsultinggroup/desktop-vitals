# Builds bin\DesktopVitals.exe with the C# compiler that ships with Windows. No SDK needed.
$ErrorActionPreference = 'Stop'
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw @"
Desktop Vitals needs the in-box .NET Framework 4.x C# compiler at:
  $csc
Install or repair .NET Framework 4.8 (Windows Features / offline installer), then retry.
Windows on ARM: use the x64 (Framework64) compiler under Prism / x64 emulation.
"@
}
$wpf = Join-Path $fw 'WPF'
$bin = Join-Path $d 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Get-Process DesktopVitals -ErrorAction SilentlyContinue | Stop-Process -Force
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Drawing.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\Microsoft.CSharp.dll"
) | ForEach-Object { "/r:$_" }
$cscOut = & $csc /nologo /target:winexe /optimize+ /warnaserror+ /platform:x64 `
    "/out:$bin\DesktopVitals.exe" "/win32icon:$d\assets\vitals.ico" "/win32manifest:$d\src\app.manifest" `
    @refs "$d\src\Native.cs" "$d\src\Engine.cs" "$d\src\Window.cs" "$d\src\App.cs" 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw ("Build failed.`r`n" + $cscOut) }
if ($cscOut) { Write-Output $cscOut }
Write-Host "Built $bin\DesktopVitals.exe"

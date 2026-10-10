# Builds bin\DesktopVitals.exe with the C# compiler that ships with Windows. No SDK needed.
$ErrorActionPreference = 'Stop'
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$bin = Join-Path $d 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Get-Process DesktopVitals -ErrorAction SilentlyContinue | Stop-Process -Force
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Drawing.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\Microsoft.CSharp.dll"
) | ForEach-Object { "/r:$_" }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 `
    "/out:$bin\DesktopVitals.exe" "/win32icon:$d\assets\vitals.ico" "/win32manifest:$d\src\app.manifest" `
    @refs "$d\src\DesktopVitals.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $bin\DesktopVitals.exe"

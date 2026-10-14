# Builds bin\v2-proto.exe with the C# compiler that ships with Windows. No SDK, no NuGet.
$ErrorActionPreference = 'Stop'
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $d)
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw @"
v2-proto needs the in-box .NET Framework 4.x C# compiler at:
  $csc
Install or repair .NET Framework 4.8, then retry.
"@
}
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$refs = @(
    "$fw\System.dll", "$fw\System.Core.dll", "$fw\Microsoft.CSharp.dll"
) | ForEach-Object { "/r:$_" }
& $csc /nologo /target:exe /optimize+ /warnaserror+ /platform:x64 `
    "/out:$bin\v2-proto.exe" @refs "$d\proto.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $bin\v2-proto.exe"

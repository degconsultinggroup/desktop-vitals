# Builds tools\v2-agents\bin\agents.exe with the in-box C# 5 compiler. No SDK, no NuGet.
$ErrorActionPreference = 'Stop'
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Missing in-box csc: $csc" }
$bin = Join-Path $d 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
& $csc /nologo /target:exe /optimize+ /warnaserror+ /platform:x64 `
    "/out:$bin\agents.exe" `
    "/r:$fw\System.dll" "/r:$fw\System.Core.dll" `
    "$d\agents.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $bin\agents.exe"

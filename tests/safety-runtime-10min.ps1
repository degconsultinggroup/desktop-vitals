# Local 10 minute outbound + write-root proof for Desktop Vitals.
# CI uses the shorter gate in tests\safety-runtime.ps1 (default 45s) from smoke.ps1.
# Run on a Windows box: pwsh -File tests\safety-runtime-10min.ps1
# Optional: -Seconds 600 to override.
param(
    [int]$Seconds = 600,
    [string]$ResultsDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtime = Join-Path $here 'safety-runtime.ps1'
if (-not (Test-Path -LiteralPath $runtime)) { throw "missing $runtime" }

Write-Host ("safety-runtime-10min: forwarding Seconds={0} (local soak; not the default CI gate)" -f $Seconds)
$arg = @{
    Seconds = $Seconds
    SkipBuild = $SkipBuild
}
if ($ResultsDir) { $arg.ResultsDir = $ResultsDir }
& $runtime @arg
exit $LASTEXITCODE

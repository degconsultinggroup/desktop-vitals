# Proves the Perflib\009 -> CurrentLanguage counter-name map used by GpuCounters.PerfName.
# Runs on any Windows host (no GPU / no localized OS required): fixture + live registry + source gate.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$script:FailCount = 0

function Write-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:FailCount++ }
    if ($Detail) { Write-Host ("{0} {1} ({2})" -f $tag, $Name, $Detail) }
    else { Write-Host ("{0} {1}" -f $tag, $Name) }
}

# Same algorithm as GpuCounters.PerfName (src/DesktopVitals.cs): index from 009, name from CurrentLanguage.
function Get-PerfLocalName {
    param(
        [string]$English,
        [string[]]$EnList,
        [string[]]$CurList
    )
    $local = $English
    if ($null -eq $EnList -or $null -eq $CurList) { return $local }
    $index = $null
    for ($i = 0; $i + 1 -lt $EnList.Length; $i += 2) {
        if ([string]::Equals($EnList[$i + 1], $English, [StringComparison]::OrdinalIgnoreCase)) {
            $index = $EnList[$i]
            break
        }
    }
    if ($null -eq $index) { return $local }
    for ($i = 0; $i + 1 -lt $CurList.Length; $i += 2) {
        if ($CurList[$i] -eq $index) {
            $local = $CurList[$i + 1]
            break
        }
    }
    return $local
}

# Fixture: English 009 pairs + German-style CurrentLanguage pairs (de-DE shaped names).
# Indexes are synthetic; only the map shape matters.
$enFixture = @(
    '2188', 'GPU Engine',
    '2198', 'Utilization Percentage',
    '2236', 'GPU Adapter Memory',
    '2244', 'Dedicated Usage',
    '2268', 'GPU Process Memory',
    '2276', 'Local Usage'
)
$deFixture = @(
    '2188', 'GPU-Modul',
    '2198', 'Auslastung in Prozent',
    '2236', 'GPU-Adapterspeicher',
    '2244', 'Dedizierte Nutzung',
    '2268', 'GPU-Prozessspeicher',
    '2276', 'Lokale Nutzung'
)
$jaFixture = @(
    '2188', 'GPU エンジン',
    '2198', '使用率',
    '2236', 'GPU アダプター メモリ',
    '2244', '専用使用',
    '2268', 'GPU プロセス メモリ',
    '2276', 'ローカル使用'
)

$need = @(
    @{ En = 'GPU Engine'; De = 'GPU-Modul'; Ja = 'GPU エンジン' },
    @{ En = 'Utilization Percentage'; De = 'Auslastung in Prozent'; Ja = '使用率' },
    @{ En = 'GPU Adapter Memory'; De = 'GPU-Adapterspeicher'; Ja = 'GPU アダプター メモリ' },
    @{ En = 'Dedicated Usage'; De = 'Dedizierte Nutzung'; Ja = '専用使用' },
    @{ En = 'GPU Process Memory'; De = 'GPU-Prozessspeicher'; Ja = 'GPU プロセス メモリ' },
    @{ En = 'Local Usage'; De = 'Lokale Nutzung'; Ja = 'ローカル使用' }
)

$deOk = $true
$jaOk = $true
$deDetail = ''
$jaDetail = ''
foreach ($row in $need) {
    $gotDe = Get-PerfLocalName -English $row.En -EnList $enFixture -CurList $deFixture
    if ($gotDe -ne $row.De) { $deOk = $false; $deDetail = "$($row.En) -> $gotDe (want $($row.De))" }
    $gotJa = Get-PerfLocalName -English $row.En -EnList $enFixture -CurList $jaFixture
    if ($gotJa -ne $row.Ja) { $jaOk = $false; $jaDetail = "$($row.En) -> $gotJa (want $($row.Ja))" }
}
Write-Check 'perflib-fixture-de-DE' $deOk $deDetail
Write-Check 'perflib-fixture-ja-JP' $jaOk $jaDetail

# Missing CurrentLanguage falls back to English (must not throw).
$fallback = Get-PerfLocalName -English 'GPU Engine' -EnList $enFixture -CurList $null
Write-Check 'perflib-fallback-english' ($fallback -eq 'GPU Engine') $fallback

# Live registry on this host: map must resolve without throwing; on English OS local == English.
$liveOk = $false
$liveDetail = ''
try {
    $enKey = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009' -ErrorAction Stop
    $curKey = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage' -ErrorAction Stop
    $enList = @($enKey.Counter)
    $curList = @($curKey.Counter)
    $mapped = @()
    foreach ($name in @('GPU Engine', 'GPU Process Memory', 'Local Usage', 'Utilization Percentage')) {
        $local = Get-PerfLocalName -English $name -EnList $enList -CurList $curList
        if ([string]::IsNullOrWhiteSpace($local)) { throw "empty map for $name" }
        $mapped += ("{0}=>{1}" -f $name, $local)
    }
    $liveOk = $true
    $ui = (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name PreferredUILanguages -ErrorAction SilentlyContinue).PreferredUILanguages
    if (-not $ui) { $ui = (Get-Culture).Name }
    $liveDetail = ("ui={0}; {1}" -f ($ui -join ','), ($mapped -join '; '))
}
catch {
    $liveDetail = $_.Exception.Message
}
Write-Check 'perflib-live-registry' $liveOk $liveDetail

# Comma-decimal cultures must not change invariant formatters (mirror Theme.Pct / GPU GB strings).
$invOk = $false
$invDetail = ''
try {
    $prev = [System.Globalization.CultureInfo]::CurrentCulture
    try {
        [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('de-DE')
        $s = (12.5).ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture)
        $bad = (12.5).ToString('0.0')
        if ($s -ne '12.5') { throw "invariant produced $s" }
        if ($bad -ne '12,5') { $invDetail = "de-DE ToString gave $bad (expected 12,5); invariant=$s" }
        else { $invDetail = "de-DE culture uses comma ($bad); invariant keeps dot ($s)" }
        $invOk = $true
    }
    finally {
        [System.Globalization.CultureInfo]::CurrentCulture = $prev
    }
}
catch {
    $invDetail = $_.Exception.Message
}
Write-Check 'invariant-decimals-under-de-DE' $invOk $invDetail

# Source gate: every GPU PerformanceCounterCategory construction must go through PerfName.
$src = Join-Path $Root 'src\DesktopVitals.cs'
$srcText = [System.IO.File]::ReadAllText($src)
$rawHits = [regex]::Matches($srcText, 'new PerformanceCounterCategory\(\s*"(GPU [^"]+)"')
$perfHits = [regex]::Matches($srcText, 'new PerformanceCounterCategory\(\s*(?:GpuCounters\.)?PerfName\(')
$srcOk = ($rawHits.Count -eq 0) -and ($perfHits.Count -ge 3)
$srcDetail = ("rawEnglish={0}; PerfNameCalls={1}" -f $rawHits.Count, $perfHits.Count)
if ($rawHits.Count -gt 0) {
    $srcDetail += '; leftover: ' + (($rawHits | ForEach-Object { $_.Groups[1].Value }) -join ', ')
}
Write-Check 'gpu-counters-use-PerfName' $srcOk $srcDetail

if ($script:FailCount -gt 0) {
    Write-Host ("locale-perflib finished with {0} failure(s)." -f $script:FailCount)
    exit 1
}
Write-Host 'locale-perflib finished: all checks passed.'
exit 0

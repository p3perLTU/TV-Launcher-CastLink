# Scrolls the phone for -Seconds while the cast runs and averages the TV's stats lines.
#   .\measure.ps1 -Phone <adb serial> [-Tv <TV IP>:5555] [-Seconds 30]
# The cast must already be running (phone "CastLink" -> TV "CastLink").
# The patterns below match the receiver's stats log line, which is in Lithuanian.
# -ParseOnly averages the stats already in the TV's log without scrolling again.
# Patterns stay ASCII: Windows PowerShell 5 reads this file in the ANSI code page.
param([Parameter(Mandatory = $true)][string]$Phone, [string]$Tv, [int]$Seconds = 30,
    [switch]$ParseOnly)

# without -Tv: the address in ..\tv-ip.txt (git-ignored, so it never reaches the repository)
$IpFile = Join-Path (Split-Path $PSScriptRoot) "tv-ip.txt"
if (-not $Tv -and (Test-Path $IpFile)) { $Tv = (Get-Content $IpFile -TotalCount 1).Trim() + ":5555" }
if (-not $Tv) { Write-Host "ERROR: give -Tv <TV IP>:5555 (or write the IP into $IpFile)" -ForegroundColor Red; exit 1 }

if (-not $ParseOnly) {
    adb -s $Phone shell am start -a android.settings.SETTINGS | Out-Null
    Start-Sleep 2
    adb -s $Tv logcat -c
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        adb -s $Phone shell input swipe 540 1800 540 600 250
        adb -s $Phone shell input swipe 540 600 540 1800 250
    }
}
$lines = adb -s $Tv logcat -d -s tvcast:I | Select-String "stats "
$cols = [ordered]@{ "decoded fps" = '(\d+) kadr\./s \S+ rodyta'; "shown fps" = 'rodyta (\d+)'; "Mb/s" = '([\d,]+) Mb/s';
    "-> decoded ms" = 'koduota (\d+) ms'; "-> screen ms" = 'ekrane (\d+) ms'; "decoding ms" = 'dekodavimas (\d+) ms'; "ping ms" = 'ping (\d+) ms' }
$lostFirst = $null; $lostLast = $null
foreach ($l in $lines) { if ($l -match 'prarasta \S+ (\d+)') { if ($null -eq $lostFirst) { $lostFirst = [int]$Matches[1] }; $lostLast = [int]$Matches[1] } }
"samples: $($lines.Count)"
foreach ($k in $cols.Keys) {
    $v = foreach ($l in $lines) { if ($l -match $cols[$k]) { [double]($Matches[1] -replace ',', '.') } }
    if ($v) { $m = $v | Measure-Object -Average -Minimum -Maximum; "{0,-14} avg {1,6:N1}  min {2,6:N1}  max {3,6:N1}" -f $k, $m.Average, $m.Minimum, $m.Maximum }
}
if ($null -ne $lostFirst) { "lost frames during run: $($lostLast - $lostFirst)" }

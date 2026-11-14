# Puts everything back after a Sony firmware update or a factory reset:
# installs the launcher, grants its permissions, disables Google TV / setupwraith / ads,
# makes Home open the launcher. Safe to run again: steps already done are skipped.
#
#   powershell -ExecutionPolicy Bypass -File .\after-update.ps1 -Ip <TV IP>
#   powershell -ExecutionPolicy Bypass -File .\after-update.ps1             (IP from tv-ip.txt)
#   powershell -ExecutionPolicy Bypass -File .\after-update.ps1 -DryRun     (only shows)
#
# Before: TV Settings -> System -> Developer options -> USB debugging ON.
# After:  turn USB debugging OFF again.
param(
    [string]$Ip,
    [switch]$DryRun
)

$ErrorActionPreference = "Continue"
$Root = $PSScriptRoot
# the TV's address stays out of the repository: -Ip, or one line in tv-ip.txt (git-ignored)
$IpFile = Join-Path $Root "tv-ip.txt"
if (-not $Ip -and (Test-Path $IpFile)) { $Ip = (Get-Content $IpFile -TotalCount 1).Trim() }
if (-not $Ip) {
    Write-Host "ERROR: give the TV address: -Ip <TV IP> (or write it into $IpFile)" -ForegroundColor Red
    exit 1
}
$Dev = "${Ip}:5555"
$Pkg = "lt.tvcast.launcher"

# Disabled in this order; the stock launcher last, so Home has somewhere to go.
# Same list as $HOME\tvbackup\restore.ps1 (which re-enables them).
$ToDisable = @(
    "tv.samba.ssm",
    "tv.samba.ssm.ui",
    "com.sony.dtv.promos",
    "com.sony.dtv.sonyselect",
    "com.google.android.tungsten.setupwraith",
    "com.google.android.apps.tv.launcherx"
)
$Grants = @(
    "android.permission.ACCESS_FINE_LOCATION",   # Wi-Fi name on the pill
    "android.permission.READ_EXTERNAL_STORAGE"   # background photo / video list
)

function Step($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   OK  $t" -ForegroundColor Green }
function Skip($t) { Write-Host "   --  $t" -ForegroundColor DarkGray }
function Warn($t) { Write-Host "   !!  $t" -ForegroundColor Yellow }
function Fail($t) { Write-Host "   ERROR: $t" -ForegroundColor Red; exit 1 }
# stderr dropped: "grep -m1" closes the pipe early and dumpsys complains about it
function Sh($cmd) { (adb -s $Dev shell $cmd 2>$null | Out-String).Trim() }
function Run($desc, $cmd) {
    if ($DryRun) { Write-Host "   [dry run] $desc  ->  adb shell $cmd" -ForegroundColor Magenta; return "" }
    $r = Sh $cmd
    Write-Host "   $desc  $r"
    return $r
}

if ($DryRun) { Write-Host "DRY RUN: nothing will be changed, only shown." -ForegroundColor Magenta }

# ---------------------------------------------------------------- connection
Step "Connecting to the TV $Dev"
adb connect $Dev | Out-Null
Start-Sleep 2
$state = (adb -s $Dev get-state 2>$null | Out-String).Trim()
if ($state -ne "device") {
    Write-Host "   The TV does not answer or did not allow the connection (state: '$state')." -ForegroundColor Red
    Write-Host "   1) TV: Settings -> System -> Developer options -> turn on USB debugging"
    Write-Host "   2) Confirm 'Allow USB debugging' on the TV screen (Always allow)"
    Write-Host "   3) If the IP changed: TV Settings -> Network, then  -Ip <new IP>"
    exit 1
}
Ok "connected"

# ---------------------------------------------------------------- launcher
Step "Launcher"
$apk = Join-Path $Root "build\tvlauncher.apk"
if (-not (Test-Path $apk)) {
    Write-Host "   No APK yet, building..."
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "build.ps1")
    if (-not (Test-Path $apk)) { Fail "could not build the APK" }
}
$installed = Sh "pm list packages $Pkg"
if ($installed -match [regex]::Escape("package:$Pkg")) {
    Skip "launcher already installed (settings and background are kept)"
} elseif ($DryRun) {
    Write-Host "   [dry run] adb install $apk" -ForegroundColor Magenta
} else {
    adb -s $Dev install $apk
    if ($LASTEXITCODE -ne 0) { Fail "adb install failed" }
    Ok "installed"
}

# ---------------------------------------------------------------- permissions
Step "Permissions"
$dump = Sh "dumpsys package $Pkg"
foreach ($p in $Grants) {
    if ($dump -match [regex]::Escape("${p}: granted=true")) { Skip "$p already granted"; continue }
    Run "pm grant $p" "pm grant $Pkg $p" | Out-Null
}

# ---------------------------------------------------------------- Wi-Fi safety check
# After a factory reset the network joined during setup belongs to setupwraith; disabling
# setupwraith can then delete it and the TV (and this ADB link) drops off the network.
Step "Wi-Fi check before disabling setupwraith"
$wifi = Sh "dumpsys wifi | grep -m1 'mostRecentlyConnected=true'"
$wifiOwnedBySetup = $wifi -match "setupwraith"
if ($wifiOwnedBySetup) {
    Warn "The current Wi-Fi network was saved by setupwraith. Disabling it may drop the TV off the network."
    Warn "TV: Settings -> Network -> pick the network -> Forget, then join it again (with the password)."
    Warn "Then run this script once more. setupwraith and Google TV are NOT disabled for now."
} else {
    Ok "the Wi-Fi network does not belong to setupwraith"
}

# ---------------------------------------------------------------- disable
Step "Disabling apps"
$disabled = Sh "pm list packages -d"
foreach ($p in $ToDisable) {
    if ($wifiOwnedBySetup -and ($p -match "setupwraith|launcherx")) { Skip "$p skipped (see the Wi-Fi check)"; continue }
    if (-not ((Sh "pm list packages $p") -match [regex]::Escape("package:$p"))) { Skip "$p is not on this TV"; continue }
    if ($disabled -match ("package:" + [regex]::Escape($p) + "(\r?\n|$)")) { Skip "$p already disabled"; continue }
    Run "disabling $p" "pm disable-user --user 0 $p" | Out-Null
    if (-not $DryRun) {
        Start-Sleep 2
        if ((adb -s $Dev get-state 2>$null | Out-String).Trim() -ne "device") {
            Fail "the TV went offline after disabling $p. To undo: $HOME\tvbackup\restore.ps1"
        }
    }
}

# ---------------------------------------------------------------- result
Step "Check"
if (-not $DryRun) {
    adb -s $Dev shell input keyevent KEYCODE_HOME
    Start-Sleep 3
}
$homeAct = Sh "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME"
if ($homeAct -match [regex]::Escape("$Pkg/")) {
    Ok "Home opens the launcher"
} else {
    Warn "Home still opens: $(($homeAct -split "`n")[-1])"
}
$focused = Sh "dumpsys activity activities | grep -m1 mFocusedApp"
Write-Host "   on screen: $focused"

Write-Host ""
Write-Host "Done. Remember to turn USB debugging off on the TV." -ForegroundColor Green
Write-Host "Back to Google TV completely: $HOME\tvbackup\restore.ps1 and .\undo.ps1"

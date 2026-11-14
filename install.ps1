# Installs the launcher on the TV, makes it the Home app and shows which app has focus.
$apk = Join-Path $PSScriptRoot "build\tvlauncher.apk"
if (-not (Test-Path $apk)) { Write-Host "Run build.ps1 first" -ForegroundColor Red; exit 1 }

adb devices
adb install -r $apk
adb shell cmd package set-home-activity lt.tvcast.launcher/.MainActivity
# lets the Wi-Fi pill show the network name (Android hides the SSID without it)
adb shell pm grant lt.tvcast.launcher android.permission.ACCESS_FINE_LOCATION
# lets Background -> Photo list pictures from USB sticks / Pictures (the TV has no file picker)
adb shell pm grant lt.tvcast.launcher android.permission.READ_EXTERNAL_STORAGE
adb shell input keyevent KEYCODE_HOME
Start-Sleep -Seconds 2
Write-Host "--- on screen now:"
adb shell "dumpsys activity activities | grep -E 'mFocusedApp|topResumedActivity'"

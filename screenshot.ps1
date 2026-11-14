# Takes a screenshot of the TV into the project folder: .\screenshot.ps1 name
# The copy on the TV is deleted afterwards, otherwise the media scanner picks it up and it
# shows in the launcher's Background -> Photo list.
param([string]$Name = "shot")
adb shell screencap -p /sdcard/$Name.png
adb pull /sdcard/$Name.png (Join-Path $PSScriptRoot "$Name.png")
adb shell rm /sdcard/$Name.png

# Removes the launcher and gives the Home key back to the stock Google TV launcher.
# launcherx and setupwraith are disabled while this launcher is in use, so they are
# re-enabled first; otherwise Home would land on the empty FallbackHome screen.
adb shell pm enable com.google.android.apps.tv.launcherx
adb shell pm enable com.google.android.tungsten.setupwraith
adb shell pm uninstall lt.tvcast.launcher
adb shell input keyevent KEYCODE_HOME

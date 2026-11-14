# Installation

The release contains:

| File | Install on | What it is |
|---|---|---|
| `TVLauncher-<version>.apk` | Android TV / Google TV, Android 8+ | Home screen: clock, weather, app tiles, background (waves, photo or video) |
| `CastLink-TV-<version>.apk` | Android TV / Google TV, Android 11+ | "CastLink": shows a phone's or PC's screen and sound on the TV |
| `CastLink-Phone-<version>.apk` | Android phone, Android 10+ | "CastLink": sends the phone's screen and sound to the TV |
| `CastLink-PC-<version>.zip` | Windows 10/11, 64-bit | Sends the PC's screen and sound to the TV or an iPad; portable, no install: unzip and run `CastLink.exe` |

The apps are not from the Play Store, so the TV and the phone ask for permission to install
apps from unknown sources: allow it for the app you install with.

## 1. Installing an APK on the TV (pick one way)

**A) USB stick**
1. Copy the APK to a USB stick and plug it into the TV.
2. Install a file manager on the TV from the Play Store (e.g. "File Commander" or "X-plore"),
   open the USB stick and select the APK.
3. Allow installing from unknown sources for the file manager when asked.

**B) "Send Files to TV" or "Downloader"**: install the app on the TV and on your phone / PC,
send the APK and open it on the TV.

**C) From a PC with ADB**
1. TV: **Settings -> System -> Developer options -> USB debugging** on.
2. PC: `adb connect <TV IP>:5555`, then `adb install TVLauncher-<version>.apk`.
3. Turn USB debugging off again afterwards.

## 2. TV Launcher: first start

- **TV Launcher** appears in the TV's app list: open it.
- It asks for two permissions, both optional:
  - *location*: only so the Wi-Fi pill can show the network name;
  - *files*: for background photos and videos from USB / Pictures / Movies.
- Hold **OK** on a tile to choose which app it shows or to move it.
- Weather city: **Apps -> Weather** tile: pick a city or **Search city...**. The default
  is Vilnius.
- Letter in the circle next to the greeting: focus the circle, hold **OK**, ◀ ▶ picks a letter
  (or the person icon), **OK** saves.

## 3. Making the Home button open the launcher

Many TVs allow it directly: **Settings -> Apps -> Default apps -> Home app -> TV Launcher**.

Google TV (Sony, TCL, Chromecast and others) has no such setting. There the stock Google TV
launcher has to be disabled over ADB. See `docs/launcher.md` and `after-update.ps1` in the
project. First make sure the Wi-Fi network was saved by the TV's settings and not by the
setup wizard (setupwraith), otherwise the TV may lose its network.
To undo: `adb shell pm enable com.google.android.apps.tv.launcherx`

## 4. Updating and removing

- A new version installs over the old one, and the settings are kept. It must be signed with
  the same key (all releases are).
- CastLink 1.10 and newer encrypt every cast and do not talk to older versions: update the
  TV app, the phone app and the PC app together. The TV shows "Update CastLink on the other
  device" when an old sender knocks.
- The first time two devices meet, the receiver (TV or phone) shows a **6-digit code** and the
  sender (PC or phone) asks whether it is the same. Answer **Yes** only when it matches; both
  then remember each other and later casts start without the code.
- Remove: **Settings -> Apps -> TV Launcher -> Uninstall** (if the Google TV launcher was
  disabled, enable it again first).

## 5. Phone screen on the TV

1. Install `CastLink-TV-<version>.apk` on the TV ("CastLink"; with the launcher it
   is a tile on the **Apps** page) and `CastLink-Phone-<version>.apk` on the phone
   ("CastLink").
2. Open **CastLink** on the TV, and on the phone **CastLink** -> the TV's name ->
   share the entire screen.
3. The first time, the phone asks for **Display over other apps**, so that it does not lock
   while casting.

TV remote: **OK** shows statistics, **◀ ▶** changes the picture size, **Back** exits.

## 6. PC screen on the TV

- Unzip `CastLink-PC-<version>.zip` into any folder (also on a USB stick) and run
  `CastLink.exe`. Keep the `lib` folder next to it: the iPad's fast mode (WebRTC) needs the
  libraries in it; their licences are in `lib\THIRD-PARTY-NOTICES.md`. Updating: unzip the new
  version over the old one. The app saves its settings next to itself
  (`CastLink-settings.txt`), or in `%APPDATA%\TVCast` if that folder is read-only. Settings of
  older versions (`TVCast-PC-settings.txt`) are taken over.
- Open **CastLink** on the TV and start `CastLink.exe`. The TV is found by itself
  (or type its address); press **Show on TV**. The sound is whatever the PC plays. The window is in
  English, or in Lithuanian when Windows is set to Lithuanian.
- The **120 frames/s** box is only for TVs with a 120 Hz panel. On a 60 Hz TV it doubles the
  delay.
- The PC and the TV should be on the **5 GHz** Wi-Fi (usually the network whose name ends in
  "_5G"); on 2.4 GHz the picture may stall every few seconds.
- Uses the graphics card's H.264 encoder (NVIDIA / AMD / Intel), otherwise a software one.
  HDR must be off.
- Laptop with two graphics cards: if the app says Windows runs it on the wrong card, answer
  **Yes**. It sets itself to "Power saving" (Settings → Display → Graphics) and restarts.

## 7. PC screen on an iPad (with touch control)

Nothing to install on the iPad.

1. In `CastLink.exe` press **Show on iPad / Mac**. The window shows an address such as
   `http://<PC address>:47900` and a 6-digit **code**. The first time, Windows asks whether
   to allow the app on the network: allow it.
2. On the iPad open that address in **Safari**, type the code and press **Connect**.
   Tip: Safari → Share → **Add to Home Screen** opens it full screen, and the code is
   remembered.
3. Control: tap = click, drag = drag, long press or two-finger tap = right click, two-finger
   swipe = scroll; the ⌨︎ button opens the keyboard, the ⤢ button switches the picture size
   (Stretch: whole iPad screen, nothing cut / Fit: with black bars / Fill: sides cut), the
   ≈ button (**Smoother**) keeps a little picture in reserve so Wi-Fi hiccups don't show
   (control then reacts a bit later). Untick **Allow control from the iPad / Mac** on the PC to
   only watch.

Picture and sound go over **WebRTC (UDP)** when possible: lost Wi-Fi packets are sent again
without stopping the picture. The PC window then says **WebRTC (UDP)** next to "Connected". If the
`lib` folder is missing, or WebRTC cannot connect within 8 s, the page uses the slower
WebSocket way by itself.

The iPad and the PC must be on the same Wi-Fi. If the home Wi-Fi is busy and the picture
stutters, switch on Windows **Mobile hotspot** on the PC and connect the iPad to it; the
window then also shows a second address: the PC's address on the hotspot. If the iPad
cannot open the page, turn its Wi-Fi off and on again.

**Keep the iPad screen on (HTTPS, once):** the page on `http://` shows these steps as an illustrated guide (simplified pictures of the iPad screens).

1. Tap **Download the certificate** and allow the profile download.
2. iPad **Settings → Profile Downloaded**. Before installing, tap **More Details**: the
   **SHA-256** must start with the numbers the PC app window shows ("Certificate SHA-256
   starts with ..."). If they differ, do not install. Then **Install**.
3. **Settings → General → About → Certificate Trust Settings** → turn on **TV Cast local CA**
   (without this, Safari says "This Connection Is Not Private").
4. Open the `https://` address the page links to. The info line (ⓘ) then says
   "screen kept on", and the stream is encrypted.

**On a Mac:** the same address in Safari works too: mouse, trackpad and the Mac's keyboard
control the PC. The `http://` page shows the Mac version of the certificate steps (Keychain
Access → **Always Trust**). A keyboard on an iPad (Magic Keyboard, Bluetooth) also types
straight to the PC.

**Full screen without Safari's bars:** on the `https://` page tap Share → **Add to Home Screen**,
then open the **CastLink** icon (type the code once more there).

The certificate is made by the PC app itself and is valid only for local network addresses,
not for any website.

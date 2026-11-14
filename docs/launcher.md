# TV Launcher

A light home screen for Android TV / Google TV: greeting, clock and date, an account circle,
the **Apps / Inputs / Wi-Fi / Settings** pills, two 2x2 app blocks with the weather between
them, and a separate **All apps** page. It uses ~20 MB of private memory, no blur and no
network polling (the weather is fetched at most once an hour while the launcher is visible).

Names in this document are the English UI texts; on a Lithuanian TV they are Lithuanian.

## Using it with the remote

| Where | Key | What happens |
|---|---|---|
| Home tile | OK | Opens the app |
| Home tile | hold OK (~0.5 s) | Menu: **Change app** (pick any installed app; picking one that is already on the home screen swaps the two) or **Move** (arrows move the tile, OK done) |
| Home | Up from the top row | To the pills |
| **Apps** pill | OK | All apps page (alphabetical, scrolls; Back or Home closes it) |
| **Inputs** pill | OK | HDMI inputs and one **Live TV** entry (duplicate input names get the port added, e.g. "AV (HDMI 1)") |
| **Wi-Fi** pill | OK | Network settings; the pill shows the network name (needs the location permission) |
| Account circle | OK | The TV's account settings |
| Account circle | hold OK | ◀ ▶ picks a letter or the person icon, OK saves |

The launcher remembers the last focused tile, also after Android stopped it. Home while the
launcher is already on top jumps to the first tile.

### Special tiles on the All apps page

- **Weather**: pick a city from the list, or **Search city…** (TV keyboard, any city in the
  world, results in the TV's language). The default city is Vilnius. Data:
  [Open-Meteo](https://open-meteo.com), free, no account.
- **Clear RAM**: closes background apps that are in the launcher's own app list. System,
  Sony and Google services, the remote, voice search and Chromecast are never touched;
  music playing in the background keeps playing. Android frees memory by itself when needed,
  so this is mostly cosmetic.
- **Background**: ◀ ▶ changes, OK selects, Back closes:
  - **Theme**: 5 wave palettes, 3 gradients, **My photo**, **My video**;
  - **Brightness**: 100 / 80 / 60 / 40 %;
  - **Picture position** (photo / video): Fill / Fit / Stretch / Center;
  - **Text**: Automatic (dark text and frosted glass on a light photo) / Light / Dark;
  - **Photo / video**: lists pictures and MP4 videos from USB sticks, Pictures and Movies
    (videos marked ▶). The picked file is copied into the launcher, so the USB stick can be
    removed. A video plays muted in a loop and only while the home screen is visible.
    Videos over 500 MB are refused, and 300 MB of free space is always kept.

To get a picture onto the TV without USB:

```
adb push C:\path\photo.jpg /sdcard/Pictures/
adb shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///sdcard/Pictures/photo.jpg
```

(the same with `/sdcard/Movies/` for a video).

### Default home tiles

Until changed with the remote, the tiles are `DOCK_PKGS` in
`src/lt/tvcast/launcher/MainActivity.java`: left block Go3, YouTube / Plex, Spotify; right
block Netflix, Twitch / YouTube Music, Play Store. Missing apps are skipped. To find a package
name: `adb shell pm list packages | findstr /i netflix`.

### Languages

Texts are in `res/values/strings.xml` (English, used for every other language) and
`res/values-lt/strings.xml` (Lithuanian). Dates follow the system locale and the clock the
system 12/24-hour setting. A new language: copy `values` to `values-<code>` (e.g.
`values-de`) and translate.

## Permissions

| Permission | Why | Without it |
|---|---|---|
| `ACCESS_FINE_LOCATION` | Android hides the Wi-Fi network name otherwise | The pill says "Wi-Fi" |
| `READ_EXTERNAL_STORAGE` | List photos and videos (the TV has no file picker) | The Photo / video row shows a message |
| `INTERNET` | Weather | No weather |
| `KILL_BACKGROUND_PROCESSES` | Clear RAM | - |

Both runtime permissions are asked once on the first start. Over ADB:

```
adb shell pm grant lt.tvcast.launcher android.permission.ACCESS_FINE_LOCATION
adb shell pm grant lt.tvcast.launcher android.permission.READ_EXTERNAL_STORAGE
```

## Making Home open the launcher

Most Android TVs: **Settings -> Apps -> Default apps -> Home app -> TV Launcher**.

Google TV (Sony, TCL, Chromecast...) has no such setting, and `cmd package set-home-activity`
or `cmd role add-role-holder` do nothing there. The only working way is to disable the stock
launcher over ADB. On this Sony these packages are disabled with
`pm disable-user --user 0` (any of them can be re-enabled at any time):

| Package | What it is |
|---|---|
| `com.google.android.apps.tv.launcherx` | Google TV home screen |
| `com.google.android.tungsten.setupwraith` | Google TV setup / recovery screen; otherwise it takes the Home key back |
| `tv.samba.ssm`, `tv.samba.ssm.ui` | Samba TV (viewing tracking / ads) |
| `com.sony.dtv.promos`, `com.sony.dtv.sonyselect` | Sony ads and recommendations |

**Wi-Fi warning:** after a factory reset, the Wi-Fi network joined during setup is owned by
setupwraith. Disabling setupwraith can then delete that network and the TV drops off Wi-Fi.
Forget the network in **Settings -> Network** and join it again first. `after-update.ps1`
checks this and stops before disabling setupwraith if needed.

Never disable core Android packages, TV input / tuner services, the remote or Bluetooth,
the keyboard, or the Play Store / Play Services.

Re-enable one package: `adb shell pm enable <package>`, or without a PC: **Settings -> Apps ->
See all apps -> Show system apps -> the app -> Enable**. All disabled packages:
`adb shell pm list packages -d` (the firmware itself already disables some regional ones;
leave those alone).

## Connecting to the TV over ADB

1. TV: **Settings -> System -> Developer options -> USB debugging** on. (No Developer options?
   **Settings -> System -> About**, press **Build** 7 times.)
2. PC: `adb connect <TV IP>:5555` and confirm the prompt on the TV. The TV's address is in
   **Settings -> Network**, and the cast receiver shows it too. For the scripts, the address
   can go into `tv-ip.txt` in the project folder.
3. When done, turn USB debugging off again: ADB gives full control of the TV to anyone on
   the network.

## After a firmware update or a factory reset

If Home opens Google TV again:

```
powershell -ExecutionPolicy Bypass -File .\after-update.ps1 -Ip <TV IP>
powershell -ExecutionPolicy Bypass -File .\after-update.ps1 -DryRun
```

The first command installs the launcher if it is missing, grants the permissions, runs the
Wi-Fi check, disables the packages above one at a time (checking that ADB still answers after
each), and checks that Home resolves to the launcher. Steps that are already done are skipped.
The `-DryRun` form only shows what would happen.

## Undo everything

```
powershell -ExecutionPolicy Bypass -File .\undo.ps1
```

This re-enables launcherx and setupwraith first, so Home does not land on an empty
FallbackHome screen, and then uninstalls the launcher. Re-enable the Samba / Sony packages
with `adb shell pm enable <package>` if wanted.

If ADB is not reachable and Home shows an empty screen: open Settings with the remote's gear
key -> **Apps -> Show system apps -> Google TV -> Enable**.

## Implementation notes

- Everything is one activity (`MainActivity`) with views built in code; no XML layouts.
- Tiles show the app's logo cut out of its TV banner (`cutOutBackground`: needs a uniform
  border colour, soft-keys it out and crops to the logo). Dark neutral lettering is turned
  white on the dark glass (and light lettering dark on the light glass) by `LogoPixels`.
  Banners without a flat background fall back to the app icon and name. (A soft gradient
  still counts as flat: CastLink's banner therefore carries its coloured icon and the word
  "CastLink" on a flat dark colour.)
- The app list is read at start. An installed or removed app rebuilds the screen; an update
  only re-reads that app (`refreshUpdated`): the old entry points into the deleted APK, so its
  banner and icon would no longer load (the tile showed the Android robot). A new name
  rebuilds the screen.
- Pictures load lazily; the All apps page is built when opened and released when closed or
  when the launcher is hidden. The photo background is kept as dithered RGB_565 (~4.6 MB) and
  dropped while another app is in front.
- Plain-Java helpers (`DockGrid`, `LogoPixels`, `WeatherCode`) hold logic that is unit
  tested without Android, see [development.md](development.md#tests).

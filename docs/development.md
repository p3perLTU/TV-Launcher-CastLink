# Development

## Requirements (Windows)

| Tool | Used for | Install |
|---|---|---|
| JDK 17 (`javac`, `java`, `keytool` on PATH) | Android apps, Java tests, signing key | e.g. `winget install EclipseAdoptium.Temurin.17.JDK` |
| Android SDK in `C:\Android` with `platforms;android-34` and `build-tools;34.0.0` | aapt2, d8, zipalign, apksigner | `sdkmanager "platforms;android-34" "build-tools;34.0.0"` |
| `adb` on PATH | talking to the TV / phone | `winget install Google.PlatformTools` |
| .NET Framework 4.x `csc.exe` | PC sender, C# tests | ships with Windows |
| Git, GitHub CLI `gh` | releases only | `winget install Git.Git GitHub.cli` |
| Chrome or Edge (optional) | the iPad page tests, run headless | usually already installed; without one that suite is skipped |

There is no Gradle. The Android apps use no third-party library: each build script runs aapt2
(resources and `R.java`), javac (`--release 8`), d8, adds the dex and assets to the zip,
zipaligns and signs. Only the PC sender has libraries, for WebRTC (see below).

### PC sender libraries (WebRTC)

`cast\pc\restore-packages.ps1` downloads **SIPSorcery 10.0.17** and its dependencies from
nuget.org at exact versions (26 packages), checks each package's SHA-512 against the NuGet
catalog and against the hash pinned in the script itself (either mismatch deletes it and
stops: a package changed on nuget.org later, or a lying catalog, is refused), and extracts the .NET Framework DLLs into
`cast\pc\lib`. It also writes `lib\THIRD-PARTY-NOTICES.md` (package, licence, copyright) and
copies every licence file to `lib\licenses`. `cast\pc\build.ps1` and `tests\run-tests.ps1` run
it by themselves when `lib` is missing; run it again after changing a version. `packages\`,
`cast\pc\lib\`, `*.dll` and `*.nupkg` are git-ignored: the repository has no binaries.

The build copies `lib` next to the exe (`cast\pc\build\lib`); `Main` loads assemblies from
there (`AssemblyResolve`, any version). All calls into the libraries go through `RtcGlue`
with the connection passed as `object`, so a missing `lib` folder only switches WebRTC off
instead of crashing the app. The C# 5 compiler cannot use `Span<T>`; Concentus is called
through its array overloads (marked obsolete, same code).

## Building

```
powershell -ExecutionPolicy Bypass -File .\build.ps1                       # build\tvlauncher.apk
powershell -ExecutionPolicy Bypass -File .\cast\build.ps1 -App receiver    # cast\receiver\build\receiver.apk
powershell -ExecutionPolicy Bypass -File .\cast\build.ps1 -App sender      # cast\sender\build\sender.apk
powershell -ExecutionPolicy Bypass -File .\cast\pc\build.ps1               # cast\pc\build\CastLink.exe
```

Install on a connected device with `adb install -r <apk>` (`install.ps1` does this for the
launcher and grants its permissions).

## Signing key

All three APKs are signed with `%USERPROFILE%\.tvlauncher\release.jks` (alias `tvlauncher`,
`CN=TV Launcher`), outside the project folder. `build.ps1` creates it on the first run with a
random password. The password is stored encrypted with Windows DPAPI in `release-pass.dpapi`
(only this Windows account on this PC can read it) and is passed to keytool / apksigner
through the `TVL_KS_PASS` environment variable, never on a command line.

- Show the password (to keep it in a password manager): `.\build.ps1 -ShowPassword`.
- Back up `release.jks` (and the password). Android only installs an update over an existing
  app if it is signed with the same key; with a new key the app has to be uninstalled first,
  which deletes its settings.
- `.gitignore` blocks every key and password file format, so a key cannot be committed by
  accident.

## Tests

```
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

| Suite | What is tested | Where |
|---|---|---|
| Java | wire format (`Proto`), frame reassembly with loss / reordering / duplicates / sender restart (`FrameAssembler`), LAN address filter, launcher grid navigation (`DockGrid`), logo recolouring and background un-mixing (`LogoPixels`), weather codes (`WeatherCode`) | `tests/java` |
| C# | PC sender: VIDEO header bytes (same golden bytes as the Java test), protocol constants equal to `Proto.java`, BGRA -> NV12 colours and scaling and pointer blending, mDNS answer parsing (including truncated and looping packets), TV address parsing, settings file lookup order, window texts in both languages. iPad mode: fMP4 init segment and fragments (box sizes, avcC, data offset, sync flags), Annex-B NAL splitting, frame durations, HTTP request parsing and limits, WebSocket accept key (RFC 6455 example), frame headers and client frames (masking, size limit, ping), Origin and Host checks (DNS rebinding), code guessing throttle and lockout, input message decoding and key whitelist, the local CA's Name Constraints bytes, DER lengths, fingerprint format, the embedded page and icon. WebRTC: ICE candidate filter (private IPv4 and `.local` only), SDP filter, NACK / PLI / FIR lines added to the offer, JSON strings, text-frame limit, H.264 RTP packetizing (single NAL and FU-A), reading NACK / PLI / FIR from compound RTCP | `tests/cs/Tests.cs` |
| iPad page | the page's JavaScript in headless Chrome / Edge, on the real `ipad.html` with the tests appended: touch to PC coordinates, input message bytes, jitter buffer, sound ring buffer, MP4 box reading, setup-guide texts and drawings, login card and toolbar (details in [ipad.md](ipad.md#tests)) | `tests/web/ipad-tests.js` |
| PowerShell | every `.ps1` in the repository parses | `tests/run-tests.ps1` |

The Java tests are compiled **without** `android.jar`, so they also prove that the classes
under test do not depend on Android. They use a tiny built-in runner
(`tests/java/lt/tvcast/testing`): every `static void test...()` method in a `*Test` class is
a test, and a test fails by throwing. The C# tests are compiled together with every
`cast/pc/*.cs` file and the embedded `web/ipad.html` (`/main:TVCast.Tests.TestProgram`), so they
can reach the internal classes. The PC build and the tests also reference
`System.Security.dll` (DPAPI for the CA key), `System.Web.Extensions.dll` (JSON),
`netstandard.dll` and the four library DLLs the code calls directly.

Not unit-testable but checked by hand: the iPad page on a real iPad (the page reports its
player state, sizes and errors to the PC window and `--log` every 5 s), the TLS chain (with
`openssl s_client -CAfile ... -verify_ip ...`) and the server's protections (a small attack
script: wrong codes, a `Host` name, 40 connections at once). WebRTC end to end: a test copy
with `--port 47901 --test-pattern --drop 50` viewed in Chromium; the page's
`RTCPeerConnection.getStats()` gives packets, NACKs, packets still lost and freezes.

How to keep code testable: put logic that needs no Android API into a plain-Java class
(package-private is fine; tests live in the same package) and keep the Activity / Service code
a thin caller. Examples: `FrameAssembler` used by `ReceiverActivity`, `DockGrid` used by the
launcher's key handling.

Not covered by unit tests (needs real hardware): MediaCodec / MediaProjection / audio, the
launcher's views, Desktop Duplication and the Media Foundation encoder. Check those on the
devices (see the measurements in [screen-cast.md](screen-cast.md)).

## Releases

New versions are published as GitHub releases of this repository, so everyone with access
finds them in one place.

1. Raise `android:versionCode` (and `versionName`) in the `AndroidManifest.xml` of every app
   that changed. Android refuses to install an update with the same or a lower versionCode.
2. Commit and push.
3. Run:

   ```
   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.1 -Notes "What changed"
   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.1 -DryRun
   ```

`release.ps1` refuses uncommitted or unpushed code and an existing tag, runs the tests, builds
all four apps, copies them to `dist` (`TVLauncher-<v>.apk`, `CastLink-TV-<v>.apk`,
`CastLink-Phone-<v>.apk`, `CastLink.exe` with `lib\`), packs the PC app as
`CastLink-PC-<v>.zip` (`CastLink.exe`, `lib\` with the DLLs and licences, `LICENSE`), and scans
every file it is about to upload, inside each APK and zip entry too, for private network addresses, e-mail addresses, private keys and API
tokens. It also checks that every APK is signed by `CN=TV Launcher`. It then creates release
`v<Version>` with these files and `dist/INSTALL.md`. If any check finds something, nothing is
uploaded.

Personal words to block as well (names, Wi-Fi names, device serials) go one per line into
`%USERPROFILE%\.tvlauncher\release-check-words.txt`. That file is outside the repository on
purpose: listing them in the script would publish them.

## Privacy rules for the repository

- No addresses of real devices: scripts take `-Ip` or read the git-ignored `tv-ip.txt`.
- No pictures or videos are committed (`.gitignore` excludes all image and video formats);
  icons are XML vectors.
- Commits use the GitHub noreply e-mail address.

## Code layout

| Path | Contents |
|---|---|
| `src/lt/tvcast/launcher/MainActivity.java` | the whole launcher UI (views built in code) |
| `src/lt/tvcast/launcher/{DockGrid,LogoPixels,WeatherCode}.java` | plain-Java launcher logic |
| `res/`, `assets/fonts/` | strings (en, lt), vector icons, the Outfit font |
| `cast/common/src/lt/tvcast/common/` | `Proto` (wire format), `FrameAssembler` |
| `cast/receiver/src/.../ReceiverActivity.java` | TV: network, decoder, audio, statistics |
| `cast/sender/src/.../MainActivity.java`, `CastService.java` | phone: UI, capture, encoder, audio |
| `cast/pc/TVCastPC.cs` | PC: capture, NV12, encoder, audio, discovery, window (own cards, buttons and check boxes that follow the Windows light / dark mode and accent colour, live), texts (en/lt). Native APIs are called through raw COM vtable slots (C# 5, no SDK); comments name each slot. Hybrid laptops: `DuplicateOutput1`, the wrong-GPU fix, encoder fallback |
| `cast/pc/WebCast.cs` | iPad mode: HTTP + WebSocket server on TCP 47900 (also TLS on the same port), pairing code and `AuthThrottle`, frame queue, WebRTC signalling |
| `cast/pc/WebRtc.cs` | iPad over WebRTC (UDP): `RtcGlue` (safe entry points), `RtcLink` (SIPSorcery peer connection, own H.264 RTP packetizer, NACK resend ring, pacer, RTCP reader, Opus) |
| `cast/pc/restore-packages.ps1` | fetches and verifies the WebRTC libraries into `cast/pc/lib` |
| `cast/pc/Fmp4.cs` | fragmented MP4 for Media Source: init segment, one fragment per frame |
| `cast/pc/RemoteInput.cs` | iPad touch / keyboard messages -> `SendInput`, key whitelist |
| `cast/pc/LocalTls.cs` | the PC's own name-constrained CA and server certificates (DPAPI-protected key) |
| `cast/pc/AppIcon.cs` | the app icon, drawn in code (blue-violet square, TV with cast waves): `build.ps1` compiles it alone with `/define:ICONTOOL` to write `build\app.ico`, which is embedded in the exe and the window; also the iPad home-screen icon |
| `cast/pc/web/ipad.html` | the iPad page: WebRTC / WebCodecs / Media Source players, smooth buffer, Web Audio ring buffer, touch gestures, wake lock, home-screen app |
| `tests/` | unit tests and the runner |

Comments explain *why* (hardware quirks, measurements); keep them when changing code.

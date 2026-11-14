# CastLink on an iPad or a Mac

The PC app (`CastLink.exe`) can show the PC's screen and sound on an **iPad**, a **Mac** (or any
tablet or computer with a modern browser), and the iPad can **control the PC** with touch. Nothing is
installed on the iPad: it is a web page served by the PC app. The technical side (transports,
packet resend, pacing, the certificate authority, the security review) is in
[screen-cast.md](screen-cast.md#pc--ipad-in-safari); this page is the user guide and the
test plan.

## Quick start

1. **PC:** start `CastLink.exe` and press **Show on iPad / Mac**. The window shows the address
   (`http://<PC address>:47900`), a 6-digit **code** and the start of the certificate's
   SHA-256. The first time, Windows asks whether to allow the app on the network: allow it.
2. **iPad:** open the address in **Safari**, type the code, tap **Connect**.
3. Optional, once: install the PC's certificate (below) so the screen stays on, then add
   the page to the home screen for full screen.

The iPad and the PC must be on the same network. If the page does not open, turn the iPad's
Wi-Fi off and on again; if the picture stutters on a busy home Wi-Fi, switch on Windows
**Mobile hotspot** and connect the iPad to it (the window then lists that address too).

## One-time setup: secure page, screen stays on, full screen

Safari keeps the screen on (Screen Wake Lock) only for secure (`https://`) pages, and a
page opened from the home screen has no Safari bars and no close button. The `http://`
page shows these steps as an **illustrated guide**, with a simplified drawing of each iPad
screen and the place to tap ringed in blue:

| # | On the iPad | Picture on the page |
|:-:|---|---|
| 1 | Tap **Download the certificate**, then **Allow** | Safari's "download a configuration profile" question |
| 2 | **Settings** → **Profile Downloaded** (top of the list) | the Settings list with the new entry |
| 3 | **More Details**: the **SHA-256** must start with the numbers the PC window shows. If not, do not install. Then **Install** | the profile with its SHA-256 next to "= in the PC window" |
| 4 | Settings → General → About → **Certificate Trust Settings** → turn on **TV Cast local CA** | the switch, turned on |
| 5 | Tap the `https://` button on the page | — |
| 6 | Safari → **Share** → **Add to Home Screen**, open the **CastLink** icon, type the code once more | the share sheet and the icon on the home screen |

Without step 4 Safari says "This Connection Is Not Private". The certificate is made by the
PC app itself and can only vouch for local network addresses, never for a real website. Its
name stays *TV Cast local CA* (the name from before CastLink), so iPads that already trust it
need nothing new.

## Using it

| On the iPad | On the PC |
|---|---|
| Tap | Left click (double tap = double click) |
| Drag | Drag with the left button held (select text, move windows) |
| Long press, or two-finger tap | Right click |
| Two-finger swipe | Scroll wheel |
| Mouse / trackpad (Magic Keyboard) | Mouse, right click, wheel |
| A keyboard (Magic Keyboard, Bluetooth) | Typing: characters, also Lithuanian letters, and the editing keys (Enter, Backspace, Tab, Esc, arrows, Home / End, Page Up / Down, Delete) go straight to the PC, no ⌨ button needed. ⌘ shortcuts stay with the iPad |

The buttons bottom right (on frosted glass, faint until touched):

| Button | What it does |
|---|---|
| ⌨ keyboard | Opens the iPad keyboard: text (also Lithuanian letters), Enter, Backspace, arrows, Esc, Tab |
| ≈ smoother | Keeps ~150 ms of picture in reserve, so Wi-Fi hiccups don't show; control reacts that much later. Remembered |
| ⤢ size | **Stretch** (default: whole iPad screen, nothing cut) / **Fit** (whole picture, black bars) / **Fill** (whole screen, sides cut). Remembered |
| ⓘ info | A line at the bottom left: transport, picture size, sound level and buffer, "screen kept on" |

The keyboard button is hidden while **Allow control from the iPad / Mac** is unticked on the PC, and on
a computer (its own keyboard types).

## On a Mac (Safari)

The same page works in Safari on a Mac (and in Chrome or Edge on any computer); the PC app
still runs on the Windows PC, the Mac only watches and controls.

- **Mouse and trackpad:** click, right click (or two-finger click), drag, scroll.
- **Keyboard:** typed straight to the PC, as with an iPad keyboard. ⌘ and Ctrl shortcuts
  (⌘Q, ⌘Tab, copy and paste) stay with the Mac.
- **Full screen:** the page goes full screen when you click **Connect**.
- **One-time setup:** the `http://` page shows the Mac version of the illustrated guide:

| # | On the Mac | Picture on the page |
|:-:|---|---|
| 1 | Click **Download the certificate**; double-click **ca.cer** in Downloads | the file in Downloads |
| 2 | **Keychain Access** opens: keychain **login**, **Add** | the Add Certificates dialog |
| 3 | Double-click **TV Cast local CA**: the **SHA-256** must start with the numbers in the PC window; if not, delete it | the certificate with its SHA-256 |
| 4 | **Trust** → "When using this certificate": **Always Trust**, close, enter the Mac's password | the Always Trust menu |
| 5 | Click the `https://` button on the page | — |
| 6 | Optional: Safari → **File** → **Add to Dock** (macOS 14+), an app of its own without Safari's bars | Safari's File menu |

The drawings use the English macOS labels; a Lithuanian macOS may show them translated.
Safari on a Mac keeps the screen on (wake lock) on the `https://` page from Safari 16.4.

### What the info line means

| Example | Meaning |
|---|---|
| `WebRTC (UDP)` | The best transport: picture and sound over UDP, lost packets are sent again |
| `57 fps · WebCodecs` or `… ms buffer` | WebSocket transport (fallback): frames per second and how far behind live |
| `1920×1080` | Picture size from the PC |
| `sound 35% (30 ms)` | Sound level and how much sound is buffered |
| `no sound arriving` | No sound packets: **Send sound** off on the PC? |
| `screen kept on` | The secure page holds the wake lock |

The PC window shows the state too: **Connected: \<iPad address\> · WebRTC (UDP) · frames/s ·
Mb/s**. The page's detailed player report (every 5 s) goes only to the log file when the PC
app is started with `--log <file>`.

## As a second screen

The iPad, a Mac or an **Android phone** (Chrome, same page) can be a **second monitor** of the
PC instead of a copy of the main one:

1. Install a virtual display driver once, for example the open-source, Microsoft-signed
   [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT).
   It needs administrator rights; set its resolution to the device's (a phone in landscape,
   for example 2340×1080). Windows then shows a second monitor in **Settings → Display**;
   place it next to the main one.
2. In the PC app choose it under **Screen to show** (the list is read again each time it is
   opened, so a monitor added meanwhile appears), then **Show on iPad / Mac**.
3. Drag windows onto that screen; touch, mouse and keyboard on the device control it.

Without such a driver there is only the main screen to show, so the device mirrors it: Windows
lets no app create a monitor without a display driver. The **Add a
screen…** button next to the list explains these steps and opens the driver's page; when the PC
already has a second screen it offers the Windows display settings ("Extend these displays")
instead. The CastLink phone app works the same way: pick the virtual screen, then **Show on TV**
with the phone chosen.

The TV works the same way (pick the virtual screen, then **Show on TV**). While a second
screen is cast, a window that becomes active within 4 s of a click from the TV, phone or iPad
is moved onto the shown screen, typed text and keys only reach a window on that screen, and
when the cast stops the screen's windows are moved back to the main one (`WindowMover.cs`):
normal, snapped, minimized and maximized ones (a maximized window is restored, moved and
maximized again, or Chrome sends it back after a minimize). While CastLink stays open and casts
nothing, a window that lands on that unseen screen again (Chrome leaving full screen returns to
where it was before) is sent home within a second. Windows refuses to move programs run as
administrator; the PC window then says how many stayed (move them with Win+Shift+←).

The choice is saved (line 6 of the settings file). Touches land on the screen that is shown
(`RemoteInput.ToVirtualDesk`: SendInput over the whole virtual desktop), also when it is left
of or above the main one. If the chosen monitor is gone, the main one is shown.

**USB instead of Wi-Fi (Android phone):** turn on **USB tethering** on the phone with the USB
cable plugged into the PC. The PC window then lists one more address (the USB network); open
that one on the phone. No Wi-Fi stalls, so the picture is steadier and a little faster. An
iPad can only do this if it has a SIM (Personal Hotspot over USB); otherwise it stays on Wi-Fi.

**Cable in, cable out (phone app):** when the PC casts to the CastLink phone app it keeps the
phone on the best link by itself. Plug the cable in (USB tethering on) during a Wi-Fi cast and it
moves to the cable within a few seconds, with the higher USB quality (80 Mb/s, up to 2560 x 1600,
and always 120 frames a second whatever the "120" box says: measured 120 fps decoded and shown,
16-19 ms from encoding to the phone's screen, 0 lost); pull the cable and it moves
back to Wi-Fi within about 3 seconds. How: the phone pings twice a second; a `route` thread on the
PC looks the phone up again by its name when it goes quiet for 3 s, or when a USB tethering
adapter appears. Android does not answer mDNS on USB tethering, so the PC also sends `WHO` to the
tethering network's gateway (that is the phone), which answers `NAME`. The phone follows the PC
to its new address at once because the new CONFIG carries the same random token. This needs the
PC to have picked the phone from the found list (a typed address has no name to look for).

**The cable is only for the picture, never for the PC's internet:** with USB tethering on,
Windows sends the PC's internet through the phone, even when Wi-Fi or a LAN has internet and the
phone has none (or limited mobile data): the phone's network gets metric 25, as good as Wi-Fi
(30) or a 1 Gb/s LAN (25), and Windows Connection Manager prefers a wired network (tethering looks
like Ethernet) whatever the metrics. And when Wi-Fi and LAN go away, a cable left plugged in
quietly spends the phone's data.

When a phone network appears that is not yet limited (`UsbRoute`, every 2 s), CastLink asks once;
with one Windows prompt it then:

- gives that network metric 9000 and sets the policy "Minimize the number of simultaneous
  connections" to 0 (`fMinimizeConnections`, Group Policy: Network > Windows Connection Manager),
  so Wi-Fi or LAN carry the internet;
- adds four Windows Firewall rules, group `CastLink USB`, only on that network: outbound to every
  public IPv4 address, to global IPv6 (`2000::/3`) and to DNS (port 53) is blocked.

The phone itself (a private address on the cable) stays reachable: picture, touches and the
`WHO` lookup still work. Tested on Wi-Fi and on a 1 Gb/s LAN: internet back on Wi-Fi / LAN; a
connection forced out through the phone fails; a cast over the cable runs at 60 fps. Another
phone or USB port shows up as another network and is asked about separately. To undo, in an
administrator PowerShell:

```powershell
Remove-NetFirewallRule -Group "CastLink USB"
Set-NetIPInterface -InterfaceAlias "<the phone's network>" -AutomaticMetric Enabled
reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy" /v fMinimizeConnections /f
```

## Troubleshooting

| Problem | Try |
|---|---|
| The page does not open | iPad Wi-Fi off and on; the address with the port; Windows Firewall allowed the app (first start asks) |
| "This Connection Is Not Private" | Step 4 above (the trust switch) |
| "Wrong code" | The code in the PC window; after 5 wrong codes it is locked for 5 minutes and a new code is made |
| Picture stutters | 5 GHz Wi-Fi or the PC's Mobile hotspot; turn on ≈ smoother |
| No sound | The iPad's volume, **Send sound** on the PC; tap the screen once (iPadOS may pause sound) |
| The screen turns off | Use the `https://` page (steps 1-5) |
| Black strip at the bottom in the home-screen app | iPadOS gives home-screen web apps a little less than the full screen; choose ⤢ Fit or Stretch |

## Tests

`tests/run-tests.ps1` checks the iPad mode on three levels:

| Level | Where | What |
|---|---|---|
| Page (JavaScript) | `tests/web/ipad-tests.js` | Runs **in headless Chrome or Edge** against the real page (the runner appends the tests to a copy of `ipad.html`, so they use its functions and state): MP4 box reading for WebCodecs, touch → PC coordinates for all three picture sizes and the black bars, the input message bytes (move, click, wheel, UTF-8 text, keys), nothing sent on a closed socket, picture-size CSS, the smooth jitter buffer (delay, even spacing, 40-frame cap, frames closed), PCM into the sound ring buffer, every guide text and drawing (iPad and Mac), escaping in the drawings, the login card and toolbar; the physical keyboard (which keys go to the PC, shortcuts stay, nothing while control is off), mouse help and no ⌨ button on a computer. Skipped (not failed) when neither browser is installed |
| PC server (C#) | `tests/cs/Tests.cs` | fMP4 muxer, HTTP and WebSocket parsing and limits, `Host` / `Origin` checks, code throttle and lockout, input decoding and key whitelist, the certificate's Name Constraints, WebRTC candidate and SDP filters, RTP packetizing, reading NACK / PLI / FIR, the embedded page and icon |
| By hand, real iPad | — | Picture, sound, touch, HTTPS and wake lock, home-screen app. Without an iPad: a test copy with `--ipad --port 47901 --no-ask --test-pattern [--drop 50]` opened in a desktop browser; `RTCPeerConnection.getStats()` shows packets, NACKs, packets still lost and freezes |

Tip for the hand test on the PC itself: mute the browser tab. Otherwise the PC captures the
page's own sound again and the level climbs to 100 %.

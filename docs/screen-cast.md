# CastLink: phone / PC -> TV or iPad

The three **CastLink** apps show a phone's or a PC's screen and sound on the TV with as little delay as
possible, on the home network only:

| App | Runs on | Source |
|---|---|---|
| Receiver "CastLink" (`lt.tvcast.receiver`) | Android TV 11+ | `cast/receiver` |
| Sender "CastLink" (`lt.tvcast.sender`) | Android phone 10+ | `cast/sender` |
| PC sender `CastLink.exe` | Windows 10/11 x64 | `cast/pc/TVCastPC.cs` |

> [!NOTE]
> Up to v1.4 they were called *Phone Screen* (TV), *Cast to TV* (phone) and *Cast to TV – PC*
> (`TVCast-PC.exe`). The package ids (`lt.tvcast.*`), the mDNS type (`_tvcast._udp`) and the
> certificate name *TV Cast local CA* stay the same, so updates install over the old apps,
> they still find each other and an iPad that trusts the certificate needs nothing new.

The receiver and the phone sender share `cast/common` (protocol and frame assembly). The PC
sender repeats the protocol constants in C#, and a unit test checks that they match the Java
ones.

## Using it

1. TV: open **CastLink**. It shows its address and waits.
2. Phone: open **CastLink**, pick the TV (found automatically) or type its address, press
   **Start**, then choose to share the whole screen. The first time, the phone asks for
   **Display over other apps**: this keeps the phone from locking while casting.
3. PC: start `CastLink.exe`; the TV is found automatically (or type its address), then
   press **Show**. The window is in English, or in Lithuanian when Windows is.

TV remote in the receiver: **OK** shows statistics, **◀ ▶** cycles the picture size
(Fit / Fill / Stretch; a portrait stream is always shown as Fit), **Back** exits.

Both devices must be on the same network, preferably the **5 GHz** Wi-Fi: on a busy 2.4 GHz
network the picture stalls every few seconds.

## Design

```
phone  MediaProjection -> VirtualDisplay -> H.264 encoder (hardware) ┐
PC     DXGI Desktop Duplication -> NV12 -> Media Foundation H.264 ───┼─ UDP 47800 ─> TV
sound  AudioPlaybackCapture / WASAPI loopback -> 48 kHz PCM, 5 ms ───┘
TV     reassemble -> MediaCodec (hardware) -> SurfaceView;  PCM -> AudioTrack (low latency)
```

- **No buffering on purpose.** The TV renders each decoded frame immediately, as the newest
  frame (`releaseOutputBuffer(i, System.nanoTime())`); audio queues at most 50 ms.
- **Loss handling.** Frames are split into 1400-byte chunks. If anything is missing, the TV
  drops frames up to the next keyframe and sends KEYREQ (`FrameAssembler`). The senders send
  a keyframe every 2 s and on request.
- **Still screens.** The phone's encoder emits nothing while the screen does not change. When
  a keyframe is requested and nothing was sent for 100 ms, the sender detaches and re-attaches
  the virtual display surface, so the system composes one fresh frame (`redrawDisplay`).
- **MediaTek decoder fix.** The TV appends an access unit delimiter after every frame: the
  MTK decoder only starts decoding a frame once it sees the next start code. This saved a
  whole frame (decoder 28 -> 12 ms).
- **Game picture mode.** `android:appCategory="game"` + `android:isGame="true"` make the Sony
  switch to its Game picture mode (least processing) while the receiver is open, and back
  when it closes.
- **Discovery.** The receiver registers the mDNS service `_tvcast._udp`; the phone uses
  Android NSD, and the PC sends one legacy-unicast PTR query.
- **Clock sync.** The TV sends PING twice a second, the sender answers PONG with its own
  clock; the shortest round trip gives the clock offset, so the TV can show real
  capture-to-screen latency.

## Protocol

UDP on port 47800, big-endian. Documented in
[`Proto.java`](../cast/common/src/lt/tvcast/common/Proto.java) and
[`SecureLink.java`](../cast/common/src/lt/tvcast/common/SecureLink.java) (the PC side:
[`SecureLink.cs`](../cast/pc/SecureLink.cs)).

A cast starts with a handshake in the clear; after it **every packet is encrypted and
authenticated** (AES-128-GCM) and the packets below travel inside SEALED:

| Packet | Direction | Contents |
|---|---|---|
| HELLO (20) | sender -> receiver, every second until answered | version, nonce, ephemeral P-256 key, static P-256 key, name |
| HELLO_OK (21) | receiver -> sender | version, nonce, ephemeral key, static key, the sender's nonce, "I know you", name |
| SEALED (22) | both | counter, AES-128-GCM of one packet below, 16-byte tag |
| PAIR_SHOW (24, sealed) | sender -> receiver | please show the pairing code |

Inside SEALED:

| Packet | Direction | Contents |
|---|---|---|
| CONFIG (1) | sender -> TV, every second | width, height, sample rate, channels |
| VIDEO (2) | sender -> TV | flags (1 = keyframe), frame id, chunk, chunk count, pts (µs), H.264 bytes |
| AUDIO (3) | sender -> TV | sequence, 16-bit little-endian interleaved PCM |
| KEYREQ (10) | TV -> sender | please send a keyframe |
| BYE (11) | sender -> TV | cast stopped |
| PING (12) / PONG (13) | TV -> sender / back | TV clock / TV clock + sender clock |

## Measured (Sony KD-50X80J, Samsung S23 FE, Ryzen 7 laptop with RTX 4060)

60 fps, 1080p, both devices on 5 GHz Wi-Fi, while scrolling:

| | Phone -> TV | PC -> TV |
|---|---|---|
| capture -> decoded on the TV | ~31 ms | ~28 ms |
| capture -> on screen | ~58-61 ms | ~59 ms |
| TV decoder time | ~12 ms | ~13 ms |
| lost frames | 0 | 0 |
| bitrate | 16 Mb/s | 20 Mb/s |

Most of what is left is the TV itself (decoder plus ~26 ms from decoded to the panel).

### 120 fps

Both senders have a **120 frames/s** option (off by default). It only helps on a TV with a
120 Hz panel *and* a decoder that keeps up. The KD-50X80J offers apps a single 3840x2160 @ 60
mode, and its decoder manages ~90-100 fps, so at 120 the delay doubles (~110 ms on screen)
and frames get lost. Use 60 there.

## Security model

- The receiver listens on UDP 47800 **only while it is open**, and accepts packets only from
  private (home network) IPv4 addresses, the ranges reserved for local networks; everything from IPv6 or
  the internet is dropped (`Proto.isLanV4`).
- While a cast runs, only the device whose CONFIG started it is accepted; other devices'
  packets are counted as `rejected` in the statistics and ignored.
- The senders listen on nothing: they use a connected UDP socket, which ignores other
  addresses. No UPnP / NAT-PMP, so no router ports are ever opened.
- The PC app loads native DLLs from System32 only (protection against planted DLLs).
- **Encrypted.** Picture, sound, typed text, mouse and touch input all travel encrypted and
  authenticated (AES-128-GCM, a fresh counter per packet, replayed or changed packets are
  dropped). Session keys come from two ECDH P-256 agreements: the per-cast (ephemeral) keys
  keep old recordings safe even if a device key leaks later; the long-term (static) keys
  prove it is the device that was confirmed before. Points off the curve are refused
  (protection against invalid-curve attacks).
- **Pairing once.** The first time two devices meet, both screens show the same 6-digit code;
  the user confirms on the sending device (PC or phone), and both remember each other. Someone
  in the middle would see a different code on each screen. Remembered devices: on the PC
  `%APPDATA%\CastLink\trusted.txt` (the PC's own key is DPAPI-protected in `identity.dat`),
  on Android in the app's private storage. Delete those to forget the devices.
- **No old versions.** A receiver drops unencrypted packets and shows "update CastLink on the
  other device"; a sender whose HELLO gets no answer says the receiver is too old.
- While a cast runs, only the sender with this cast's keys is accepted; it may move between
  addresses (USB cable <-> Wi-Fi) because only it can seal packets. New handshakes are
  limited to 4 a second.
- A fake `_tvcast` mDNS entry from another device on the network could appear in the
  sender's TV list.

## PC → iPad (in Safari)

The PC app can also send its screen to an **iPad**, or any tablet or computer with a modern
browser, and the iPad can **control the PC** with touch. Nothing is installed on the iPad: a
native iPad app would need a Mac with Xcode to build.

1. PC: **Show on iPad / Mac**. The window shows an address (`http://<PC IP>:47900`) and a 6-digit
   **code**.
2. iPad: open the address in **Safari**, type the code, **Connect**. Tip: **Share → Add to
   Home Screen** opens it full screen next time; the code is remembered.

| On the iPad | On the PC |
|---|---|
| Tap | Left click (double tap = double click) |
| Drag | Drag with the left button held (select text, move windows) |
| Long press, or two-finger tap | Right click |
| Two-finger swipe | Scroll wheel (vertical and horizontal) |
| ⌨︎ button | iPad keyboard: text, Enter, Backspace, arrows, Esc, Tab |
| ⤢ button | Picture size: **Stretch** (default: whole iPad screen, nothing cut, a 16:9 PC screen a little stretched on a 4:3 iPad) / **Fit** (whole picture, black bars) / **Fill** (whole screen, sides cut by ~10 %); remembered |
| Mouse / trackpad (Magic Keyboard) | Mouse, right click, wheel |

| ≈ button | **Smoother**: keeps ~150 ms of picture in reserve, so Wi-Fi hiccups don't show (control reacts that much later); off = lowest delay; remembered |

How it works (`cast/pc/WebCast.cs`, `WebRtc.cs`, `Fmp4.cs`, `RemoteInput.cs`, `web/ipad.html`):

- The same capture and H.264 encoder as for the TV, 60 fps, **1920×1080 at 10 Mb/s** (the
  TV gets 20), **Baseline** profile for the browser. The encoder skips unchanged screens, so
  40-50 fps is common; a still screen is still sent at 10 fps, otherwise Safari stalls.
- The page connects over a WebSocket (HTTP / HTTPS on TCP 47900): code, control messages and,
  until something better is up, picture and sound. Right after the code it tries the three
  ways below, best first, and the window shows which one runs ("WebRTC (UDP)" or "WebSocket (TCP)" next to "Connected").

| Way | Transport | When |
|---|---|---|
| **WebRTC** (best) | **UDP**: H.264 in RTP, Opus sound, encrypted (DTLS-SRTP) | Always tried first; used once connected (within 8 s), otherwise the page stays on the WebSocket |
| WebCodecs | WebSocket (TCP) | HTTPS page with `VideoDecoder` (iPadOS 16.4+) |
| Media Source | WebSocket (TCP) | Fallback: fragmented MP4 into `ManagedMediaSource` / `MediaSource` |

- **Why UDP:** over TCP one lost Wi-Fi packet holds back everything behind it until it is
  sent again, so the whole picture stops; over UDP only that packet is late. The PC side
  uses the [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) library (ICE, DTLS,
  SRTP); see [Third-party libraries](#third-party-libraries-pc-app).
- **Lost packets** (`WebRtc.cs`): the PC packetizes H.264 itself (RFC 6184, FU-A, 1200-byte
  payloads) and keeps the last 1024 packets. The browser asks for missing ones with **NACK**
  and gets them again within one round trip; it asks for a new keyframe with PLI / FIR.
  SIPSorcery keeps only one feedback message per RTCP packet (and only the first entry of a
  NACK), so the PC reads the decrypted RTCP itself. Measured with every 50th packet thrown
  away on purpose (`--drop 50`): 0 packets lost for good, 0 freezes, 60 fps. Before that fix
  one lost packet froze the picture until the next keyframe (13 of 15 s frozen).
- **Pacing:** a fully changed screen (switching apps) is a frame of a few hundred KB. Sent in
  one burst, the Wi-Fi dropped part of it and the picture waited. Packets now leave evenly
  at 32 Mb/s, at most 48 KB at once. With the real iPad the stalls while switching apps went
  from frequent to rare.
- **Network:** ICE offers only the PC's private IPv4 addresses and accepts only private
  IPv4 and the browser's `.local` (mDNS) names, so no STUN / TURN server and no internet are
  involved. The UDP socket listens on IPv4 only.
- WebCodecs and Media Source: each frame becomes one fragmented-MP4 fragment (`moof` +
  `mdat`) whose duration is the real time since the previous frame. WebCodecs decodes it at
  once onto a canvas (optionally through the ~150 ms smooth buffer); Media Source plays it in
  `sequence` mode, 5-15 % faster when behind, a jump only when more than 1.5 s behind.
- Sound over the WebSocket: 20 ms blocks of 48 kHz PCM into one continuous Web Audio stream
  (a ring buffer read by a ScriptProcessor at 48 kHz; one node per block clicked at every
  seam on the iPad). After an underrun it waits for 80 ms before playing again.
  `navigator.audioSession.type = "playback"` keeps it audible when the iPad is in silent
  mode. Over WebRTC the sound is Opus (128 kb/s, in-band FEC) and plays with the picture.
- Input: small binary messages (move, button, wheel, text, key) replayed with `SendInput`.
  Only editing and navigation keys are accepted (no Windows key, no Alt, no shortcuts).
- If the Wi-Fi cannot keep up for 0.5 s (30 frames queued), the PC drops queued pictures
  (never sound, never a queued keyframe) and continues from the next keyframe.
- Every 5 s the page reports its player state (frames, buffer, stalls, sound, errors) to the
  PC; it goes to the `--log` file only (the window shows just the address, code and state).

Measured with a real iPad (Safari, 2026-11-01): smooth picture and sound with 0 dropped
frames when the Wi-Fi is calm; the remaining short stutters came from the Wi-Fi itself
(ping to the iPad up to 700 ms, also to the router, while the home network was busy).
Connecting the iPad straight to the PC's **Mobile hotspot** helped (the window lists that
address too). Over WebRTC with NACK and pacing: `lost=0`, only rare short stalls while
switching apps; Safari itself then kept ~270 ms of jitter buffer because of that Wi-Fi.

### HTTPS: keeping the iPad screen on

Safari only lets a page keep the screen on (Screen Wake Lock) on HTTPS. The same port also
serves `https://`: the first byte tells TLS from plain HTTP. The certificate comes from this
PC's own small certificate authority (`cast/pc/LocalTls.cs`), made once and kept in
`%APPDATA%\TVCast\local-ca.dat`, encrypted with Windows DPAPI for this Windows user. The
server certificate covers all current LAN addresses and is made again from the same CA when
a new address appears (e.g. the hotspot). Nothing has to be installed again on the iPad.

Once on the iPad (the `http://` page shows these steps as an illustrated guide, with simplified drawings of each iPad screen and links):

1. Open `http://<PC>:47900/ca.cer` and allow the profile download.
2. **Settings → Profile Downloaded → Install**.
3. **Settings → General → About → Certificate Trust Settings → turn on "TV Cast local CA"**.
   Without this switch Safari says "This Connection Is Not Private".
4. Open `https://<PC>:47900`.
5. For full screen: Safari → Share → **Add to Home Screen**, then open the **CastLink** icon.
   The page is a home-screen web app (`/manifest.webmanifest`, `/icon.png`): no Safari bars
   and no close button (X). It does not use the browser's element full screen on an iPad,
   because Safari then shows its X in the top-left corner. Screen Wake Lock works there too
   (checked: secure=true wake=on). The home-screen app keeps its own storage: the code is typed
   once more.

The CA has a critical **Name Constraints** extension: it can only vouch for private IPv4
ranges (the address ranges reserved for home networks, plus this PC itself) and DNS names under `.invalid`. Even
with its private key nobody could pose as a real website to the iPad. Checked with openssl:
the chain verifies for the PC's addresses, over TLS 1.2 with ECDHE-RSA-AES256-GCM.

**Security of the iPad mode** (reviewed and attacked on the PC on 2026-11-01):

| Threat | Protection | Checked live |
|---|---|---|
| Someone on the Wi-Fi guesses the code | At most one try every 2 s; after 5 wrong codes nobody can try for 5 minutes and a new code is made (`AuthThrottle`, for all addresses together). About 95 years for a 50 % chance | 2nd try at once: "wait 2 s"; 5th wrong code: locked 300 s, new code |
| A website opened on the iPad reaches the server through DNS rebinding | The `Host` header must be one of this PC's IPv4 addresses, never a name; the WebSocket `Origin` must equal it | `Host: evil.example` closed |
| Clickjacking / other origins | `X-Frame-Options: DENY`, `Content-Security-Policy` (no framing, no forms, connections only to this server), `Referrer-Policy: no-referrer` | headers present, page still runs |
| Connection flood | At most 24 connections; a failing connection can never take the app down | 40 at once: 16 refused, app fine |
| Swapped certificate during install (the CA is downloaded over plain HTTP) | The PC window shows the start of the CA's SHA-256; the install steps say to compare it with the iPad (profile, More Details) | — |
| Someone with the CA key | Name Constraints: private IPv4 and `.invalid` only, so no real website can be faked; key DPAPI-encrypted for this Windows user | openssl decodes the constraints |
| A drag cut off by the Wi-Fi | Buttons still held are released when the viewer leaves | — |
| Input beyond mouse and typing | Key whitelist: editing and navigation keys only (no Windows key, no Alt, no shortcuts) | unit tests |
| WebRTC before the code / from the internet | Signalling only after the code; ICE offers and accepts only private IPv4 and `.local` names; the stream starts only if the connected address really is a private IPv4 (an mDNS answer can come from any device) | unit tests |
| A viewer that floods (with the code) | At most 3 WebRTC starts per connection, at most 64 packets sent again per RTCP report, signalling messages up to 16 KB | unit tests |
| Typing from a Mac or a hardware keyboard | The same key whitelist on the PC: characters and editing / navigation keys only; ⌘ / Ctrl shortcuts are not even sent; Windows does not let injected input reach elevated windows (UAC prompts) | page tests |

Also: the server runs only while **Show on iPad / Mac** is on, answers private IPv4 addresses and the
PC itself only, one viewer at a time, input only while **Allow control from the iPad / Mac** is
ticked. Over `https://` everything is encrypted; over plain `http://` anyone with the Wi-Fi
password could in theory watch the stream and the code. The code is remembered in the
settings file next to the exe. The first start makes Windows ask whether to allow the app on
the network. WebRTC starts only after the code was accepted, its picture and sound are
always encrypted (DTLS-SRTP, the browser's certificate fingerprint comes over the already
authenticated WebSocket), and its signalling messages are limited to 16 KB.

### Third-party libraries (PC app)

WebRTC needs libraries the rest of the project does not: **SIPSorcery** and its dependencies
(26 DLLs, ~11 MB), in a `lib` folder next to `CastLink.exe`. The exe finds them there by
itself (`AssemblyResolve` in `Main`); without the folder everything works except WebRTC, and
the iPad stays on the WebSocket. `lib\THIRD-PARTY-NOTICES.md` lists every package, licence
and copyright; the licence texts are in `lib\licenses`.

> [!IMPORTANT]
> SIPSorcery is BSD-3-Clause **with an additional use restriction**: it must not be used,
> modified or distributed inside Israel and the Occupied Territories
> (`lib\licenses\SIPSorcery-LICENSE.md`). The other libraries are MIT, Apache-2.0,
> BSD-2-Clause or BSD-3-Clause.

### Laptops with two graphics cards

Desktop Duplication only works from the GPU that drives the screen. On a laptop in hybrid
mode (screen on the integrated GPU) whose NVIDIA settings put apps on "High performance",
capture fails with `DXGI_ERROR_UNSUPPORTED`. The app recognises this and offers to set
itself to **Power saving** (the same as **Settings → Display → Graphics**) and restart. The
encoder then falls back from NVIDIA to the integrated GPU's (AMD / Intel) or the software
one. On the test laptop: AMD H.264 encoder, 7 ms per frame.

## Diagnostics

- The receiver writes a statistics line to logcat (tag `tvcast`) every second while a cast
  runs. `cast/measure.ps1 -Phone <adb serial>` scrolls the phone for 30 s and averages them.
- PC sender test flags: `--tv <ip> --start --log <file> --size WxH --find --fps N --lang en|lt`,
  `--ipad` (start the iPad mode), `--control on|off`. For testing the iPad mode without a
  screen or a second PC instance in the way: `--port N` (instead of 47900), `--no-ask` (no
  GPU question), `--test-pattern` (a moving picture instead of the screen), `--drop N` (WebRTC:
  every Nth video packet is not sent the first time, as if the Wi-Fi lost it). With `--log`
  the WebRTC part also logs SIPSorcery's messages and, once a second, the biggest frame,
  the pacer queue, NACKs and resent packets.
- The PC sender keeps its settings in `CastLink-settings.txt` next to the exe (or in
  `%APPDATA%\TVCast\settings.txt` if that folder is read-only). Lines: TV, sound 1/0, fps,
  iPad control 1/0.
  Settings from versions up to 1.4 (`RodytiTV-nustatymai.txt`) are still read.
- The PC sender needs HDR off (it captures 8-bit BGRA) and uses the GPU's H.264 encoder
  (NVIDIA / AMD / Intel), with the Windows software encoder as a fallback.

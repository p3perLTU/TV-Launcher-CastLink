# CastLink for macOS

The macOS sender of CastLink: this Mac's screen, or a **virtual second screen**, shown on

- a TV or an Android phone / tablet with the CastLink app, over the same encrypted UDP protocol
  as `CastLink.exe` (SecureLink pairing, sealed packets; the packet types are in
  `cast/common/src/lt/tvcast/common/Proto.java`). A phone controls the Mac by touch, a TV by its
  mouse and keyboard. A virtual screen takes the receiver's shape, so the picture fills it.
- an iPad, a phone or another computer in Safari or Chrome: the same page as `CastLink.exe`
  (`cast/pc/web/ipad.html`), so nothing is installed on the iPad.

When the cast of a virtual screen ends, the windows left on it come back to the main screen.

## Build

Needs macOS 13 or newer and the Xcode Command Line Tools (`xcode-select --install`).

```
./build.sh          # runs the tests; builds build/CastLink.app, build/CastLink.dmg and ./castlink-mac
```

or double-click **Build CastLink.command** in Finder. `build.sh` signs with a self-signed
certificate it makes in the login keychain, so macOS keeps the permissions from build to build.

**Privacy for release builds:** Swift puts the absolute source paths (with your user name) into
the binary. For a build you hand out, map them away:

```
swift build -c release -Xswiftc -file-prefix-map -Xswiftc "$PWD"=.
```

## Use

```
./castlink-mac                    # a virtual second screen of 2160x1620 (iPad 10.2")
./castlink-mac --size 2360x1640   # the device's screen size in pixels (iPad Air 10.9")
./castlink-mac --mirror           # the main screen instead of a virtual one
./castlink-mac --help
```

or double-click **Start CastLink.command**. Then open the printed `http://<Mac>:47900` address in
Safari on the iPad and type the code. The first time macOS asks for:

| Permission | Why |
|---|---|
| Screen & System Audio Recording | the picture and the sound |
| Accessibility | touches, clicks and typing from the iPad, phone or TV; moving windows home |
| Local Network | the iPad reaching the page |

Each goes to CastLink.app, or to the app the command line version is started from (Terminal);
after allowing it, quit that app and start it again. The app also keeps its pairing key and the
list of trusted receivers in `~/Library/Application Support/CastLink`, and a debug log of remote
input in `~/Library/Logs/CastLink`; nothing is written into this folder.

The virtual screen appears in **System Settings → Displays** as *CastLink*; place it next to the
main one and drag windows onto it. It goes away when CastLink stops (Ctrl+C).

On the iPad: tap = click, drag = drag, long press or two-finger tap = right click, two-finger
swipe = scroll, ⌨ for the keyboard. A Magic Keyboard types directly.

## How it works

| Part | Windows (`cast/pc`) | macOS (`cast/mac`) |
|---|---|---|
| Virtual screen | a virtual display driver (VDD) | CoreGraphics' private `CGVirtualDisplay`, as DeskPad and BetterDisplay use |
| Capture | Desktop Duplication | ScreenCaptureKit (picture and sound) |
| Encoder | Media Foundation H.264 | VideoToolbox H.264 (the GPU's encoder where there is one) |
| Transport | WebRTC, WebSocket fallback | WebSocket (fMP4 + 16-bit PCM); WebRTC is answered with `rtc-off` |
| Input | `SendInput` | `CGEvent` |

`Sources/CastLinkCore` is the pure part (fMP4 boxes, WebSocket framing, HTTP checks, the code
throttle, input decoding), ported from the C# files and tested with the same cases as
`tests/cs` (`swift run CastLinkCoreTests`, run by `build.sh`).

Not yet on the Mac: the `https://` page (the PC's local certificate authority; without it the
page works over `http://` but cannot keep the iPad's screen on), WebRTC.

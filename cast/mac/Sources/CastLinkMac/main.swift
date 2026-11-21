// CastLink for macOS: this Mac's screen, or a virtual second screen, on an iPad, a phone or
// another computer, in its browser, with touch, mouse and keyboard control. The same page and
// protocol as CastLink.exe's "Show in a browser" (see docs/ipad.md), so nothing is installed on
// the iPad.
//
// Without arguments the window opens (App.swift). With arguments it runs in the terminal:
//   castlink-mac --virtual                 a virtual second screen of 2160x1620 (iPad 10.2")
//   castlink-mac --virtual --size 2360x1640
//   castlink-mac --mirror [N]              real screen N (1 = the main one)
//   --fps 60  --mbps 12  --no-audio  --no-control  --lodpi

import AppKit
import CastLinkCore
import CoreGraphics
import Foundation

func usage() -> Never {
    print("""
    CastLink for macOS: this Mac on an iPad, a phone or another computer, in Safari or Chrome.
    Without arguments the CastLink window opens. In the terminal:

      castlink-mac --virtual          a virtual second screen, 2160x1620 (iPad 10.2")
      castlink-mac --size WxH         the virtual screen's size in pixels, e.g. 2360x1640 (iPad Air)
      castlink-mac --mirror [N]       show real screen N (1 = the main one) instead
      --fps N        frames per second, 30..120 (default 60)
      --mbps N       picture bit rate in Mbit/s (default: from the size)
      --lodpi        the virtual screen without Retina scaling (smaller text, more room)
      --no-audio     no sound
      --no-control   watch only: touches and keys from the device are ignored
    """)
    exit(0)
}

func fail(_ s: String) -> Never {
    FileHandle.standardError.write(Data(("CastLink: " + s + "\n").utf8))
    exit(1)
}

func parse(_ args: [String]) -> CastConfig {
    var o = CastConfig()
    var a = args
    while !a.isEmpty {
        let arg = a.removeFirst()
        switch arg {
        case "--virtual": o.displayID = nil
        case "--mirror":
            var n = 1
            if let v = a.first.flatMap({ Int($0) }) { n = v; a.removeFirst() }
            let all = realScreens()
            if n < 1 || n > all.count { fail("there are \(all.count) screens; --mirror 1..\(all.count)") }
            o.displayID = all[n - 1]
        case "--size":
            guard let s = a.first, case let p = s.lowercased().split(separator: "x"), p.count == 2,
                  let w = Int(p[0]), let h = Int(p[1]), w >= 320, h >= 240, w <= 7680, h <= 4320 else { usage() }
            o.width = w & ~1; o.height = h & ~1
            a.removeFirst()
        case "--fps":
            guard let n = a.first.flatMap({ Int($0) }), n >= 30, n <= 120 else { usage() }
            o.fps = n; a.removeFirst()
        case "--mbps":
            guard let n = a.first.flatMap({ Int($0) }), n >= 1, n <= 100 else { usage() }
            o.mbps = n; a.removeFirst()
        case "--lodpi": o.hiDPI = false
        case "--no-audio": o.audio = false
        case "--no-control": o.control = false
        default: usage()
        }
    }
    return o
}

let session = CastSession()

func runInTerminal(_ cfg: CastConfig) async {
    if cfg.control && !MacInput.trusted(prompt: true) {
        print("! Control from the device needs Accessibility: System Settings -> Privacy & Security -> Accessibility,")
        print("  turn on Terminal (or the app CastLink runs in), then start CastLink again.")
    }
    session.onCodeChanged = { print("New code: \($0)") }
    session.onStopped = { fail("capture stopped: \($0)") }
    session.onEvent = { e in
        switch e {
        case .connected(let a): print("connected: \(a)")
        case .left(let a): print("viewer left: \(a)")
        case .slow: print("the viewer could not keep up and was disconnected")
        case .replaced: print("viewer replaced by a newer one")
        case .wrongCode(let a): print("wrong code from \(a)")
        case .locked(let a): print("5 wrong codes (last from \(a)): locked for 5 minutes, new code")
        case .page(let t): print("page: \(t)")
        }
    }
    let info: CastInfo
    do {
        info = try await session.start(cfg)
    } catch CastError.capture(let m) {
        fail("""
        cannot capture the screen (\(m)).
        Allow it: System Settings -> Privacy & Security -> Screen & System Audio Recording,
        turn on Terminal (or the app CastLink runs in), quit Terminal and start CastLink again.
        """)
    } catch {
        fail("\(error)")
    }
    print("")
    print("CastLink for macOS")
    print("  Showing: " + (info.virtual ? "a virtual second screen" : "a real screen")
          + " \(info.width)x\(info.height), \(info.fps) fps, \(info.mbps) Mbit/s, encoder: " + (info.gpu ? "GPU" : "processor"))
    if info.addresses.isEmpty { print("  No network: connect this Mac to the same Wi-Fi as the device.") }
    for a in info.addresses { print("  On the iPad, in Safari:  http://\(a):\(Web.port)") }
    print("  Code: \(info.code)")
    if info.virtual {
        print("  Arrange the screen in System Settings -> Displays; drag windows onto it.")
        print("  More room on it: Displays -> CastLink -> a larger size (up to \(info.width)x\(info.height), smaller text).")
    }
    print("  Ctrl+C stops (the virtual screen goes away).")
    print("")
}

let args = Array(CommandLine.arguments.dropFirst()).filter { !$0.hasPrefix("-psn_") } // Finder's process serial number
if args.isEmpty {
    CastLinkApp.main()
} else {
    if args.contains("--help") || args.contains("-h") { usage() }
    let cfg = parse(args)
    signal(SIGINT, SIG_IGN)
    let sigint = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
    sigint.setEventHandler {
        session.stop()
        print("\nCastLink stopped.")
        exit(0)
    }
    sigint.resume()
    Task { await runInTerminal(cfg) }
    NSApplication.shared.setActivationPolicy(.prohibited)
    dispatchMain()
}

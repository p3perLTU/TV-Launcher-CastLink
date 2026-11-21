// One "Show in a browser": the screen (a virtual one or a real one), the capture and encoder, the
// page server and the input, started and stopped together. Used by the window (App.swift) and by
// the command line (main.swift).

import CastLinkCore
import CoreGraphics
import Foundation

struct CastConfig {
    /// nil: a virtual second screen of width x height
    var displayID: CGDirectDisplayID?
    var width = 2160, height = 1620
    var hiDPI = true
    var fps = 60
    var mbps = 0
    var audio = true
    var control = true
}

struct CastInfo {
    let addresses: [String]
    let code: String
    let width: Int, height: Int, fps: Int, mbps: Int
    let gpu: Bool
    let virtual: Bool
}

enum CastError: Error {
    case noPage
    case virtualScreen(String)
    case capture(String)   // most often: no Screen Recording permission
    case server(String)
    case other(String)
}

final class CastSession {
    var onEvent: ((PageServer.Event) -> Void)?
    var onCodeChanged: ((String) -> Void)?
    var onStopped: ((String) -> Void)?

    private var source: ScreenSource?
    private var capture: ScreenCapture?
    private var server: PageServer?
    private var input: MacInput?

    var viewer: String? { return server?.viewer }
    var code: String? { return server?.code }
    var controlAllowed: Bool {
        get { return server?.allowControl ?? false }
        set { server?.allowControl = newValue }
    }

    /// the pairing code is kept, so a page that remembers it can come back after a restart
    static let settingsDir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/CastLink")
    static let codeFile = settingsDir.appendingPathComponent("code.txt")

    static func savedCode() -> String? {
        return (try? String(contentsOf: codeFile, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func saveCode(_ c: String) {
        try? FileManager.default.createDirectory(at: settingsDir, withIntermediateDirectories: true)
        try? c.write(to: codeFile, atomically: true, encoding: .utf8)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: codeFile.path)
    }

    func start(_ cfg: CastConfig) async throws -> CastInfo {
        guard let page = Data(base64Encoded: pageBase64) else { throw CastError.noPage }

        let src = try await ScreenSource.prepare(cfg)
        source = src
        if src.virtualScreen != nil { WindowMover.castStarted(src.displayID) }
        let displayID = src.displayID
        var (w, h) = (src.width, src.height)
        w &= ~1; h &= ~1 // H.264 wants even sizes
        let mbps = cfg.mbps > 0 ? cfg.mbps : max(4, min(30, w * h * cfg.fps / 10_000_000))

        let server = PageServer(page: page, code: CastSession.savedCode(), allowControl: cfg.control)
        CastSession.saveCode(server.code)
        server.width = w
        server.height = h
        server.audioRate = 48000
        server.onCodeChanged = { [weak self] c in CastSession.saveCode(c); self?.onCodeChanged?(c) }
        server.onEvent = { [weak self] e in self?.onEvent?(e) }

        let input = MacInput(displayID: displayID)
        server.onInput = { input.apply($0) }
        server.onRelease = { input.releaseButtons() }

        let capture: ScreenCapture
        do {
            capture = try ScreenCapture(displayID: displayID, width: w, height: h, fps: cfg.fps, audio: cfg.audio, bitrate: mbps * 1_000_000)
        } catch {
            stop()
            throw CastError.other(error.localizedDescription)
        }
        capture.encoder.onFrame = { data, key, us, sps, pps in server.video(data, key: key, ptsUs: us, sps: sps, pps: pps) }
        capture.onAudio = { server.audio($0) }
        capture.onStopped = { [weak self] e in self?.onStopped?(e.localizedDescription) }
        server.keyRequest = { [weak capture] in capture?.requestKey() }
        self.server = server
        self.capture = capture
        self.input = input

        do {
            try await capture.start()
        } catch {
            stop()
            throw CastError.capture(error.localizedDescription)
        }
        do {
            try server.start()
        } catch {
            stop()
            throw CastError.server(error.localizedDescription)
        }
        return CastInfo(addresses: PageServer.lanAddresses(), code: server.code, width: w, height: h, fps: cfg.fps, mbps: mbps,
                        gpu: capture.encoder.isHardware, virtual: cfg.displayID == nil)
    }

    func stop() {
        input?.releaseButtons()
        server?.stop()
        capture?.stop()
        server = nil
        capture = nil
        input = nil
        // its windows go to the main screen before a virtual screen goes away
        if let s = source, s.virtualScreen != nil { WindowMover.castEnding(s.displayID) }
        source = nil // a virtual screen goes away with it
    }
}

/// The screen a cast shows: a real one, or a virtual second screen made for it (it goes away
/// with this object). width x height: the pixels to capture (a virtual screen's are the device's,
/// whatever size is picked for it in Displays; macOS scales).
final class ScreenSource {
    let displayID: CGDirectDisplayID
    let width: Int, height: Int
    let virtualScreen: VirtualScreen?

    init(displayID: CGDirectDisplayID, width: Int, height: Int, virtualScreen: VirtualScreen?) {
        self.displayID = displayID
        self.width = width & ~1
        self.height = height & ~1
        self.virtualScreen = virtualScreen
    }

    static func prepare(_ cfg: CastConfig) async throws -> ScreenSource {
        if let id = cfg.displayID {
            let (w, h) = pixelSize(id)
            return ScreenSource(displayID: id, width: w, height: h, virtualScreen: nil)
        }
        let v: VirtualScreen
        do {
            v = try VirtualScreen(width: cfg.width, height: cfg.height, hiDPI: cfg.hiDPI)
        } catch {
            throw CastError.virtualScreen(error.localizedDescription)
        }
        // ScreenCaptureKit lists a new screen after a moment
        try? await Task.sleep(nanoseconds: 1_500_000_000)
        return ScreenSource(displayID: v.displayID, width: cfg.width, height: cfg.height, virtualScreen: v)
    }
}

/// The real screens: the main one first, then the others in macOS' order.
func realScreens() -> [CGDirectDisplayID] {
    let main = CGMainDisplayID()
    return [main] + activeDisplays().filter { $0 != main && CGDisplayIsInMirrorSet($0) == 0 && !isOurVirtualScreen($0) }
}

/// our own virtual screen (vendor 0x1A2B, product 0xCA57) is not offered as a real one
func isOurVirtualScreen(_ id: CGDirectDisplayID) -> Bool {
    return CGDisplayVendorNumber(id) == 0x1A2B && CGDisplayModelNumber(id) == 0xCA57
}

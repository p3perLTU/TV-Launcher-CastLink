// The CastLink window: the same layout as the Windows app (cast/pc/TVCastPC.cs): a sidebar with
// the pages, a card for the page, a status card at the bottom.

import AppKit
import ApplicationServices
import CastLinkCore
import CoreGraphics
import SwiftUI

// ---- the device sizes offered for the virtual screen -------------------------------------------

struct DeviceSize: Hashable {
    let name: String, width: Int, height: Int
}

let deviceSizes: [DeviceSize] = [
    DeviceSize(name: "iPad 10.2\" (7–9)", width: 2160, height: 1620),
    DeviceSize(name: "iPad 10.9\" / Air 11\"", width: 2360, height: 1640),
    DeviceSize(name: "iPad Pro 11\"", width: 2388, height: 1668),
    DeviceSize(name: "iPad Pro 12.9\" / Air 13\"", width: 2732, height: 2048),
    DeviceSize(name: "iPad mini 8.3\"", width: 2266, height: 1488),
    DeviceSize(name: "Android 2340×1080", width: 2340, height: 1080),
    DeviceSize(name: "Full HD 1920×1080", width: 1920, height: 1080),
    DeviceSize(name: "QHD 2560×1440", width: 2560, height: 1440),
    DeviceSize(name: "4K UHD 3840×2160", width: 3840, height: 2160),
]

// ---- the model -------------------------------------------------------------------------------------

enum Page: Hashable { case app, browser, screens }

enum StatusKind { case idle, waiting, connected, error }

@MainActor
final class AppModel: ObservableObject {
    let t = Texts.current
    let session = CastSession()
    private let defaults = UserDefaults.standard

    @Published var page: Page = .app

    // ---- the CastLink app (TV / phone) ----
    @Published var device: String { didSet { defaults.set(device, forKey: "device") } }
    @Published var fps120: Bool { didSet { defaults.set(fps120, forKey: "fps120") } }
    @Published var receivers: [Receiver] = []
    @Published var searching = false
    @Published var tvRunning = false
    @Published var tvStarting = false
    @Published var pairAsk = false
    @Published var pairCode = ""
    @Published var pairName = ""
    private var tvCast: TvCast?
    private var statsTimer: Timer?
    @Published var sound: Bool { didSet { defaults.set(sound, forKey: "sound") } }
    @Published var control: Bool {
        didSet {
            defaults.set(control, forKey: "control")
            session.controlAllowed = control
            tvCast?.allowControl = control
            if control && running { _ = MacInput.trusted(prompt: true) }
            checkPermissions()
        }
    }
    /// 0: the virtual screen; n: real screen n (1 = the main one)
    @Published var screenChoice: Int { didSet { defaults.set(screenChoice, forKey: "screen") } }
    /// an index into deviceSizes, or deviceSizes.count for a custom size
    @Published var sizeChoice: Int { didSet { defaults.set(sizeChoice, forKey: "size") } }
    @Published var customWidth: Int { didSet { defaults.set(customWidth, forKey: "customWidth") } }
    @Published var customHeight: Int { didSet { defaults.set(customHeight, forKey: "customHeight") } }
    @Published var hiDPI: Bool { didSet { defaults.set(hiDPI, forKey: "hiDPI") } }

    @Published var running = false
    @Published var starting = false
    @Published var info: CastInfo?
    @Published var code = ""
    @Published var status = ""
    @Published var statusKind: StatusKind = .idle
    @Published var detail = ""
    @Published var needScreenPermission = false
    @Published var needAccessibility = false
    @Published var screens: [CGDirectDisplayID] = []

    init() {
        defaults.register(defaults: ["sound": true, "control": true, "screen": 1, "size": 0,
                                     "customWidth": 2160, "customHeight": 1620, "hiDPI": true, "device": "", "fps120": false])
        device = defaults.string(forKey: "device") ?? ""
        fps120 = defaults.bool(forKey: "fps120")
        sound = defaults.bool(forKey: "sound")
        control = defaults.bool(forKey: "control")
        screenChoice = defaults.integer(forKey: "screen")
        sizeChoice = defaults.integer(forKey: "size")
        customWidth = defaults.integer(forKey: "customWidth")
        customHeight = defaults.integer(forKey: "customHeight")
        hiDPI = defaults.bool(forKey: "hiDPI")
        status = t.ready
        refreshScreens()
        checkPermissions()
        find()

        session.onEvent = { [weak self] e in Task { @MainActor in self?.event(e) } }
        session.onCodeChanged = { [weak self] c in Task { @MainActor in self?.code = c } }
        session.onStopped = { [weak self] _ in
            Task { @MainActor in
                guard let self = self, self.running else { return }
                self.stop()
                self.setStatus(self.t.captureStopped, .error)
            }
        }
        // screens plugged in or out
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.refreshScreens() }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.checkPermissions() }
        }
    }

    func refreshScreens() {
        screens = realScreens()
        if screenChoice > screens.count { screenChoice = 0 }
    }

    func checkPermissions() {
        needAccessibility = control && !MacInput.trusted(prompt: false)
    }

    func screenName(_ id: CGDirectDisplayID, index: Int) -> String {
        let (w, h) = pixelSize(id)
        var name = ""
        for s in NSScreen.screens {
            if let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber, n.uint32Value == id {
                name = s.localizedName
            }
        }
        return "\(index) · \(w)×\(h)" + (id == CGMainDisplayID() ? " · " + t.main : "") + (name.isEmpty ? "" : " · " + name)
    }

    var virtualSize: (Int, Int) {
        if sizeChoice < deviceSizes.count { return (deviceSizes[sizeChoice].width, deviceSizes[sizeChoice].height) }
        return (max(320, min(7680, customWidth)) & ~1, max(240, min(4320, customHeight)) & ~1)
    }

    func setStatus(_ s: String, _ k: StatusKind) {
        status = s
        statusKind = k
    }

    func event(_ e: PageServer.Event) {
        guard running else { return }
        switch e {
        case .connected(let a): setStatus(t.connected(a), .connected)
        case .left(let a): setStatus(t.left(a), .waiting)
        case .slow: setStatus(t.slow, .waiting)
        case .replaced: break
        case .wrongCode(let a): setStatus(t.wrongCode(a), .error)
        case .locked(let a): setStatus(t.locked(a), .error)
        case .page(let m): detail = "iPad: " + m
        }
    }

    func toggle() {
        if running || starting { stop() } else { start() }
    }

    /// the screen picked on the Screens page, and the options of the Browser / app pages
    func castConfig() -> CastConfig {
        var cfg = CastConfig()
        cfg.audio = sound
        cfg.control = control
        cfg.hiDPI = hiDPI
        if screenChoice > 0 && screenChoice <= screens.count {
            cfg.displayID = screens[screenChoice - 1]
        } else {
            (cfg.width, cfg.height) = virtualSize
        }
        return cfg
    }

    func start() {
        if tvRunning || tvStarting { stopTv() } // one cast at a time
        let cfg = castConfig()
        if control && !MacInput.trusted(prompt: true) { needAccessibility = true }
        starting = true
        needScreenPermission = false
        detail = ""
        setStatus(t.starting, .waiting)
        Task {
            do {
                let info = try await session.start(cfg)
                self.info = info
                self.code = info.code
                self.running = true
                self.starting = false
                self.detail = t.showing(info.width, info.height, info.fps, info.mbps, info.gpu)
                self.setStatus(info.addresses.isEmpty ? t.noNetwork : t.waiting, info.addresses.isEmpty ? .error : .waiting)
            } catch {
                self.starting = false
                self.running = false
                switch error {
                case CastError.capture:
                    needScreenPermission = true
                    setStatus(t.needScreen, .error)
                case CastError.server:
                    setStatus(t.portBusy, .error)
                case CastError.virtualScreen:
                    setStatus(t.virtualFailed, .error)
                default:
                    setStatus("\(error)", .error)
                }
            }
        }
    }

    func stop() {
        session.stop()
        running = false
        starting = false
        info = nil
        detail = ""
        setStatus(t.stopped, .idle)
    }

    // ---- the CastLink app (TV / phone) ----

    func find() {
        if searching { return }
        searching = true
        if !tvRunning { setStatus(t.searching, .waiting) }
        Task.detached {
            let found = Discovery.find(ms: 1500)
            await MainActor.run {
                self.searching = false
                self.receivers = found
                if self.device.isEmpty, let first = found.first { self.device = first.label }
                if !self.tvRunning && !self.tvStarting && !self.running {
                    self.setStatus(found.isEmpty ? self.t.notFound : self.t.found(found.count), found.isEmpty ? .idle : .idle)
                }
            }
        }
    }

    /// "Name (ip)" from the list, a typed IPv4 address, or a name found on the network
    func targetAddress() -> String? {
        let d = device.trimmingCharacters(in: .whitespaces)
        if let r = receivers.first(where: { $0.label == d || $0.name == d }) { return r.ip }
        if let open = d.lastIndex(of: "("), d.hasSuffix(")") {
            let ip = String(d[d.index(after: open)..<d.index(before: d.endIndex)])
            if Web.parseV4(ip) != nil { return ip }
        }
        return Web.parseV4(d) != nil ? d : nil
    }

    func toggleTv() {
        if tvRunning || tvStarting { stopTv() } else { startTv() }
    }

    func startTv() {
        guard let ip = targetAddress() else { setStatus(t.enterIp, .error); return }
        if running || starting { stop() } // one cast at a time
        let cfg = castConfig()
        if control && !MacInput.trusted(prompt: true) { needAccessibility = true }
        let cast = TvCast(target: ip, screen: cfg, fps: fps120 ? 120 : 60, audio: sound, control: control)
        cast.onState = { [weak self] st in Task { @MainActor in self?.tvState(st) } }
        cast.onStopped = { [weak self] _ in
            Task { @MainActor in
                guard let self = self, self.tvRunning else { return }
                self.stopTv()
                self.setStatus(self.t.captureStopped, .error)
            }
        }
        tvCast = cast
        tvStarting = true
        needScreenPermission = false
        detail = ""
        setStatus(t.connecting(device), .waiting)
        Task {
            do {
                try await cast.start()
                self.tvStarting = false
                self.tvRunning = true
                self.startStats()
            } catch {
                cast.stop()
                self.tvCast = nil
                self.tvStarting = false
                self.tvRunning = false
                switch error {
                case CastError.capture:
                    needScreenPermission = true
                    setStatus(t.needScreen, .error)
                case CastError.virtualScreen:
                    setStatus(t.virtualFailed, .error)
                default:
                    setStatus("\(error)", .error)
                }
            }
        }
    }

    func tvState(_ st: TvCast.State) {
        guard tvRunning || tvStarting else { return }
        switch st {
        case .connecting(let a): setStatus(t.connecting(a), .waiting)
        case .pairing(let code, let name):
            pairCode = code
            pairName = name
            pairAsk = true
            setStatus(t.pairWaiting(name), .waiting)
        case .showing(let name):
            pairAsk = false
            setStatus(t.showingOn(name), .connected)
        case .oldReceiver: setStatus(t.oldReceiver, .error)
        case .refused:
            stopTv()
            setStatus(t.pairRefused, .error)
        }
    }

    func answerPairing(_ yes: Bool) {
        pairAsk = false
        tvCast?.answerPairing(yes)
    }

    private func startStats() {
        statsTimer?.invalidate()
        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self = self, let c = self.tvCast, self.tvRunning else { return }
                let (f, b) = c.takeStats()
                let (w, h) = c.size
                if w > 0 { self.detail = self.t.liveStats(f, Double(b) * 8 / 1_000_000, w, h, c.gpu) }
            }
        }
    }

    func stopTv() {
        statsTimer?.invalidate()
        statsTimer = nil
        tvCast?.stop()
        tvCast = nil
        tvRunning = false
        tvStarting = false
        pairAsk = false
        detail = ""
        setStatus(t.stopped, .idle)
    }

    func openPrivacy(_ pane: String) {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?" + pane) { NSWorkspace.shared.open(u) }
    }

    func openDisplays() {
        if let u = URL(string: "x-apple.systempreferences:com.apple.Displays-Settings.extension") { NSWorkspace.shared.open(u) }
    }
}

// ---- the app -----------------------------------------------------------------------------------

final class AppDelegate: NSObject, NSApplicationDelegate {
    var onQuit: (() -> Void)?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // also when started as a bare binary, not from CastLink.app
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { return true }

    func applicationWillTerminate(_ notification: Notification) { onQuit?() }
}

struct CastLinkApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject var model = AppModel()

    var body: some Scene {
        Window("CastLink", id: "main") {
            ContentView()
                .environmentObject(model)
                .frame(width: 800, height: 540)
                .preferredColorScheme(.dark)
                .onAppear { delegate.onQuit = { [model] in model.session.stop(); model.stopTv() } }
        }
        .windowResizability(.contentSize)
    }
}

// ---- the look ----------------------------------------------------------------------------------

enum Look {
    static let window = Color(red: 0.125, green: 0.125, blue: 0.13)
    static let card = Color(red: 0.17, green: 0.17, blue: 0.18)
    static let line = Color.white.opacity(0.08)
    static let selected = Color.white.opacity(0.09)
    static let dim = Color.white.opacity(0.55)
    static let blue = Color(red: 0x38 / 255.0, green: 0x84 / 255.0, blue: 1)
    static let violet = Color(red: 0x7C / 255.0, green: 0x4D / 255.0, blue: 1)
    static let gradient = LinearGradient(colors: [blue, violet], startPoint: .topLeading, endPoint: .bottomTrailing)
}

/// the CastLink icon (the same drawing as docs/images/castlink.svg, 108x108 units)
struct CastIcon: View {
    var size: CGFloat = 40

    var body: some View {
        Canvas { ctx, sz in
            let k = sz.width / 108
            func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x * k, y: y * k) }
            let bg = Path(roundedRect: CGRect(x: 0, y: 0, width: sz.width, height: sz.height), cornerRadius: 24 * k)
            ctx.fill(bg, with: .linearGradient(Gradient(colors: [Look.blue, Look.violet]), startPoint: .zero, endPoint: CGPoint(x: sz.width, y: sz.height)))
            var screen = Path(roundedRect: CGRect(x: 31.7 * k, y: 35.3 * k, width: 44.6 * k, height: 30.2 * k), cornerRadius: 5 * k)
            screen.move(to: p(54, 67.8)); screen.addLine(to: p(54, 72.7))
            screen.move(to: p(43.9, 73.4)); screen.addLine(to: p(64.1, 73.4))
            ctx.stroke(screen, with: .color(.white), style: StrokeStyle(lineWidth: 4.5 * k, lineCap: .round, lineJoin: .round))
            var waves = Path()
            waves.addArc(center: p(39.6, 58), radius: 7.9 * k, startAngle: .degrees(-90), endAngle: .degrees(0), clockwise: false)
            waves.move(to: p(39.6, 43.6))
            waves.addArc(center: p(39.6, 58), radius: 14.4 * k, startAngle: .degrees(-90), endAngle: .degrees(0), clockwise: false)
            ctx.stroke(waves, with: .color(.white), style: StrokeStyle(lineWidth: 3.6 * k, lineCap: .round))
            ctx.fill(Path(ellipseIn: CGRect(x: (39.6 - 2.5) * k, y: (58 - 2.5) * k, width: 5 * k, height: 5 * k)), with: .color(.white))
        }
        .frame(width: size, height: size)
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) { content }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 10).fill(Look.card))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Look.line))
    }
}

// ---- the window --------------------------------------------------------------------------------

struct ContentView: View {
    @EnvironmentObject var m: AppModel

    var body: some View {
        HStack(spacing: 0) {
            Sidebar()
                .frame(width: 210)
            VStack(spacing: 12) {
                Card {
                    switch m.page {
                    case .app: AppPage()
                    case .browser: BrowserPage()
                    case .screens: ScreensPage()
                    }
                }
                .frame(maxHeight: .infinity)
                StatusCard()
            }
            .padding(14)
        }
        .background(Look.window)
        .alert(m.t.pairTitle, isPresented: $m.pairAsk) {
            Button(m.t.yes) { m.answerPairing(true) }
            Button(m.t.no, role: .cancel) { m.answerPairing(false) }
        } message: {
            Text(m.t.pairAsk(m.pairName, m.pairCode))
        }
    }
}

struct Sidebar: View {
    @EnvironmentObject var m: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 12) {
                CastIcon(size: 38)
                Text("CastLink").font(.system(size: 20, weight: .semibold))
            }
            .padding(.top, 18)
            Text(m.t.subtitle)
                .font(.system(size: 11))
                .foregroundColor(Look.dim)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 8)
                .padding(.bottom, 18)
            item(.app, "iphone", m.t.appSection)
            item(.browser, "globe", m.t.browser)
            item(.screens, "display.2", m.t.screens)
            Spacer()
        }
        .padding(.horizontal, 14)
    }

    func item(_ p: Page, _ icon: String, _ title: String) -> some View {
        Button { m.page = p } label: {
            HStack(spacing: 12) {
                RoundedRectangle(cornerRadius: 2)
                    .fill(m.page == p ? Look.blue : Color.clear)
                    .frame(width: 3, height: 16)
                Image(systemName: icon).frame(width: 20)
                Text(title).font(.system(size: 13))
                Spacer()
            }
            .padding(.vertical, 9)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 6).fill(m.page == p ? Look.selected : Color.clear))
        }
        .buttonStyle(.plain)
    }
}

struct AppPage: View {
    @EnvironmentObject var m: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(m.t.appSection).font(.system(size: 17, weight: .semibold))
            Text(m.t.appHint).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 8)
                .fixedSize(horizontal: false, vertical: true)
            Text(m.t.deviceLabel).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 18)
            HStack(spacing: 8) {
                TextField("", text: $m.device)
                    .textFieldStyle(.roundedBorder)
                    .disabled(m.tvRunning || m.tvStarting)
                Menu {
                    ForEach(m.receivers, id: \.self) { r in
                        Button(r.label) { m.device = r.label }
                    }
                } label: {
                    Image(systemName: "list.bullet")
                }
                .menuStyle(.borderlessButton)
                .frame(width: 34)
                .disabled(m.receivers.isEmpty || m.tvRunning)
                Button { m.find() } label: {
                    HStack(spacing: 6) {
                        if m.searching { ProgressView().controlSize(.small) } else { Image(systemName: "magnifyingglass") }
                        Text(m.t.find)
                    }
                    .frame(minWidth: 90)
                }
                .disabled(m.searching)
            }
            .padding(.top, 6)
            Spacer(minLength: 12)
            VStack(alignment: .leading, spacing: 10) {
                Toggle(isOn: $m.sound) { Label(m.t.sound, systemImage: "speaker.wave.2") }
                    .disabled(m.tvRunning || m.tvStarting)
                Toggle(isOn: $m.fps120) { Label(m.t.fps120, systemImage: "speedometer") }
                    .disabled(m.tvRunning || m.tvStarting)
                Toggle(isOn: $m.control) { Label(m.t.control, systemImage: "hand.point.up.left") }
            }
            .toggleStyle(.checkbox)
            .font(.system(size: 13))
            .padding(.bottom, 16)
            BigButton(title: m.tvRunning || m.tvStarting ? m.t.stop : m.t.showOnDevice,
                      icon: m.tvRunning || m.tvStarting ? "stop.fill" : "play", prominent: !m.tvRunning && !m.tvStarting) { m.toggleTv() }
        }
    }
}

struct BrowserPage: View {
    @EnvironmentObject var m: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(m.t.browser).font(.system(size: 17, weight: .semibold))
            Text(m.t.webHint).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 8)
            Spacer(minLength: 12)
            if m.running, let info = m.info {
                Address(info: info)
            }
            Spacer(minLength: 12)
            VStack(alignment: .leading, spacing: 10) {
                Toggle(isOn: $m.sound) { Label(m.t.sound, systemImage: "speaker.wave.2") }
                    .disabled(m.running || m.starting)
                Toggle(isOn: $m.control) { Label(m.t.control, systemImage: "hand.point.up.left") }
            }
            .toggleStyle(.checkbox)
            .font(.system(size: 13))
            .padding(.bottom, 16)
            BigButton(title: m.running || m.starting ? (m.starting ? m.t.starting : m.t.stop) : m.t.show,
                      icon: m.running ? "stop.fill" : "play", prominent: !m.running && !m.starting) { m.toggle() }
        }
    }
}

struct Address: View {
    @EnvironmentObject var m: AppModel
    let info: CastInfo

    var body: some View {
        HStack(alignment: .top, spacing: 28) {
            VStack(alignment: .leading, spacing: 4) {
                Text(m.t.openInBrowser).font(.system(size: 11)).foregroundColor(Look.dim)
                ForEach(info.addresses.prefix(2), id: \.self) { a in
                    Text("http://\(a):\(String(Web.port))")
                        .font(.system(size: 20, weight: .semibold, design: .monospaced))
                        .textSelection(.enabled)
                }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(m.t.code).font(.system(size: 11)).foregroundColor(Look.dim)
                Text(spaced(m.code))
                    .font(.system(size: 26, weight: .bold, design: .monospaced))
                    .foregroundStyle(Look.gradient)
                    .textSelection(.enabled)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.25)))
    }

    func spaced(_ c: String) -> String { c.count == 6 ? String(c.prefix(3)) + " " + String(c.suffix(3)) : c }
}

struct ScreensPage: View {
    @EnvironmentObject var m: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(m.t.screens).font(.system(size: 17, weight: .semibold))
            Text(m.t.screensHint).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 8)
                .fixedSize(horizontal: false, vertical: true)
            Label(m.t.screenToShow, systemImage: "display").font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 18)
            Picker("", selection: $m.screenChoice) {
                Text(m.t.virtualScreen).tag(0)
                ForEach(Array(m.screens.enumerated()), id: \.offset) { i, id in
                    Text(m.screenName(id, index: i + 1)).tag(i + 1)
                }
            }
            .labelsHidden()
            .padding(.top, 6)
            if m.screenChoice == 0 {
                Text(m.t.device).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 14)
                HStack {
                    Picker("", selection: $m.sizeChoice) {
                        ForEach(Array(deviceSizes.enumerated()), id: \.offset) { i, d in
                            Text(verbatim: "\(d.name) · \(d.width)×\(d.height)").tag(i)
                        }
                        Text(m.t.custom).tag(deviceSizes.count)
                    }
                    .labelsHidden()
                    if m.sizeChoice == deviceSizes.count {
                        TextField(m.t.width, value: $m.customWidth, format: .number.grouping(.never)).frame(width: 70)
                        Text("×")
                        TextField(m.t.height, value: $m.customHeight, format: .number.grouping(.never)).frame(width: 70)
                    }
                }
                .padding(.top, 6)
                Toggle(m.t.retina, isOn: $m.hiDPI).toggleStyle(.checkbox).font(.system(size: 12)).padding(.top, 10)
                Text(m.t.virtualHow).font(.system(size: 12)).foregroundColor(Look.dim).padding(.top, 12)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            HStack {
                Text(m.running ? m.t.appliesNext : "").font(.system(size: 11)).foregroundColor(Look.dim)
                Spacer()
                BigButton(title: m.t.displaySettings, icon: "display", prominent: false, height: 34) { m.openDisplays() }
                    .frame(width: 230)
            }
        }
        .onAppear { m.refreshScreens() }
    }
}

struct StatusCard: View {
    @EnvironmentObject var m: AppModel

    var color: Color {
        switch m.statusKind {
        case .idle: return Color.gray
        case .waiting: return Color.orange
        case .connected: return Color.green
        case .error: return Color.red
        }
    }

    var body: some View {
        Card {
            HStack(alignment: .top, spacing: 10) {
                Circle().fill(color).frame(width: 9, height: 9).padding(.top, 4)
                VStack(alignment: .leading, spacing: 4) {
                    Text(m.status).font(.system(size: 13, weight: .semibold)).fixedSize(horizontal: false, vertical: true)
                    if !m.detail.isEmpty {
                        Text(m.detail).font(.system(size: 11)).foregroundColor(Look.dim).lineLimit(2)
                    }
                    if m.needAccessibility {
                        HStack {
                            Text(m.t.needAccessibility).font(.system(size: 11)).foregroundColor(.orange)
                            Button(m.t.openSettings) { m.openPrivacy("Privacy_Accessibility") }.controlSize(.small)
                        }
                    }
                    if m.needScreenPermission {
                        Button(m.t.openSettings) { m.openPrivacy("Privacy_ScreenCapture") }.controlSize(.small)
                    }
                }
                Spacer()
            }
        }
        .frame(height: 96)
    }
}

struct BigButton: View {
    let title: String
    let icon: String
    var prominent = true
    var height: CGFloat = 42
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                Text(title).font(.system(size: 13, weight: .semibold))
            }
            .frame(maxWidth: .infinity, minHeight: height)
            .foregroundColor(.white)
            .background(
                RoundedRectangle(cornerRadius: 7)
                    .fill(prominent ? AnyShapeStyle(Look.gradient) : AnyShapeStyle(Color.white.opacity(0.1)))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

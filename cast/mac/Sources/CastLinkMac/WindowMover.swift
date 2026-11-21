// Windows left on a virtual screen come home to the main screen when its cast ends (as
// cast/pc/WindowMover.cs does on Windows): before the virtual screen goes away, and then once a
// second while nothing is cast, for any window that lands on a screen nobody sees (an app
// leaving full screen, a window macOS put back at its old spot). Uses the Accessibility API
// (the permission control already needs); real-screen casts are left alone.

import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

enum WindowMover {
    private static let lock = NSLock()
    private static var casting = 0
    /// virtual screens this app made (display ID -> its bounds when last seen)
    private static var virtualScreens: [CGDirectDisplayID: CGRect] = [:]
    private static var guardTimer: DispatchSourceTimer?
    private static let queue = DispatchQueue(label: "castlink windows")

    /// a cast of a virtual screen started
    static func castStarted(_ id: CGDirectDisplayID) {
        lock.lock()
        casting += 1
        virtualScreens[id] = CGDisplayBounds(id)
        lock.unlock()
        startGuard()
    }

    /// a cast of a virtual screen ends, before the screen goes away: its windows go home now
    static func castEnding(_ id: CGDirectDisplayID) {
        lock.lock()
        casting = max(0, casting - 1)
        let r = virtualScreens[id] ?? CGDisplayBounds(id)
        virtualScreens[id] = r
        lock.unlock()
        queue.sync { moveHome(where: { r.contains(center($0)) }, why: "cast ended") }
    }

    /// a virtual screen was made again at another size for the same cast: nothing moves
    static func replaced(_ old: CGDirectDisplayID, by new: CGDirectDisplayID) {
        lock.lock()
        virtualScreens[old] = nil
        virtualScreens[new] = CGDisplayBounds(new)
        lock.unlock()
    }

    private static func startGuard() {
        lock.lock(); defer { lock.unlock() }
        if guardTimer != nil { return }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 1, repeating: 1)
        t.setEventHandler { guardTick() }
        t.resume()
        guardTimer = t
    }

    /// nothing cast: a window on one of our old virtual screens, or on no screen at all, goes home
    private static func guardTick() {
        lock.lock(); let idle = casting == 0, old = Array(virtualScreens.values); lock.unlock()
        if !idle || !AXIsProcessTrusted() { return }
        let screens = activeDisplays().map { CGDisplayBounds($0) }
        let main = CGDisplayBounds(CGMainDisplayID())
        moveHome(where: { w in
            let c = center(w)
            if main.contains(c) { return false }
            return old.contains(where: { $0.contains(c) }) || !screens.contains(where: { $0.contains(c) })
        }, why: "guard")
    }

    private static func center(_ r: CGRect) -> CGPoint { return CGPoint(x: r.midX, y: r.midY) }

    /// the main screen's area for windows (without the menu bar and the Dock), top-left origin
    private static func mainWorkArea() -> CGRect {
        let main = CGDisplayBounds(CGMainDisplayID())
        guard let s = NSScreen.screens.first else { return main }
        let f = s.frame, v = s.visibleFrame
        // AppKit counts from the bottom left of the first screen, CoreGraphics from its top left
        return CGRect(x: v.minX, y: f.maxY - v.maxY, width: v.width, height: v.height)
    }

    private static func attr(_ el: AXUIElement, _ name: String) -> CFTypeRef? {
        var v: CFTypeRef?
        return AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success ? v : nil
    }

    private static func frame(_ w: AXUIElement) -> CGRect? {
        guard let p = attr(w, kAXPositionAttribute), let s = attr(w, kAXSizeAttribute),
              CFGetTypeID(p) == AXValueGetTypeID(), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var pt = CGPoint.zero, sz = CGSize.zero
        AXValueGetValue(p as! AXValue, .cgPoint, &pt)
        AXValueGetValue(s as! AXValue, .cgSize, &sz)
        return CGRect(origin: pt, size: sz)
    }

    private static func set(_ w: AXUIElement, position p: CGPoint) {
        var v = p
        if let a = AXValueCreate(.cgPoint, &v) { AXUIElementSetAttributeValue(w, kAXPositionAttribute as CFString, a) }
    }

    private static func set(_ w: AXUIElement, size s: CGSize) {
        var v = s
        if let a = AXValueCreate(.cgSize, &v) { AXUIElementSetAttributeValue(w, kAXSizeAttribute as CFString, a) }
    }

    /// every normal window (standard or dialog, not minimized, not full screen) that `pick` wants
    /// goes to the main screen: the same place relative to its screen, smaller if it does not
    /// fit, a window that filled its screen (zoomed) fills the main one
    private static func moveHome(where pick: (CGRect) -> Bool, why: String) {
        let dest = mainWorkArea()
        let screens = activeDisplays().map { CGDisplayBounds($0) }
        var moved = 0
        var apps: [String] = []
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            let pid = app.processIdentifier
            if pid == getpid() { continue }
            let a = AXUIElementCreateApplication(pid)
            AXUIElementSetMessagingTimeout(a, 0.25) // a hung app must not hold us up
            guard let ws = attr(a, kAXWindowsAttribute) as? [AXUIElement] else { continue }
            for w in ws {
                let sub = attr(w, kAXSubroleAttribute) as? String ?? ""
                if sub != "AXStandardWindow" && sub != "AXDialog" { continue }
                if (attr(w, kAXMinimizedAttribute) as? Bool) == true { continue }
                if (attr(w, "AXFullScreen") as? Bool) == true { continue } // its own Space; the guard gets it once it leaves
                guard let f = frame(w), f.width > 1, f.height > 1, pick(f) else { continue }
                // the screen it is on (or was on): for its place within it
                let from = screens.first(where: { $0.contains(center(f)) }) ?? CGRect(origin: f.origin, size: dest.size)
                let zoomed = f.width >= from.width - 8 && f.height >= from.height - 60
                let size = zoomed ? dest.size : CGSize(width: min(f.width, dest.width), height: min(f.height, dest.height))
                var x = dest.minX + (f.minX - from.minX) / max(1, from.width) * dest.width
                var y = dest.minY + (f.minY - from.minY) / max(1, from.height) * dest.height
                x = min(max(x, dest.minX), dest.maxX - size.width)
                y = min(max(y, dest.minY), dest.maxY - size.height)
                if zoomed { x = dest.minX; y = dest.minY }
                set(w, size: size) // smaller first, so the move is not refused for not fitting
                set(w, position: CGPoint(x: x, y: y))
                set(w, size: size)
                moved += 1
                let name = app.localizedName ?? "?"
                if !apps.contains(name) { apps.append(name) }
            }
        }
        if moved > 0 { TvCast.inputLog("windows home (\(why)): \(moved) moved, apps: \(apps.joined(separator: ", "))") }
    }
}

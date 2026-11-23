// Replays the page's touch, mouse and keyboard on this Mac with CGEvent, on the screen being
// shown (also a virtual one). The pointer stays where the last touch or TV mouse left it; only
// the TV's "own pointer" mode (CLICKAT / WHEELAT) borrows it and gives it back.
// Needs System Settings -> Privacy & Security -> Accessibility for the app that runs this
// (Terminal, when started from there).

import AppKit
import ApplicationServices
import CastLinkCore
import CoreGraphics
import Foundation

final class MacInput {
    var displayID: CGDirectDisplayID
    private var held = [false, false, false]
    private var lastX = 32768, lastY = 32768
    // CLICKAT / WHEELAT: where the Mac's user had the pointer
    private var saved: CGPoint?
    // the TV mouse (MOVEBY / BUTTONHERE): when it was last used, and presses not passed on
    private var lastRelative = 0.0
    private var swallowed = [false, false, false]
    // the TV mouse's own idea of the pointer: the system's lags behind events just posted
    private var relPos: CGPoint?
    // the last pointer input was the TV mouse (relative), not a touch / absolute position
    private var relative = false

    /// KEYRAW keys held down now (macOS key codes): let go when the cast ends
    private var heldKeys = Set<UInt16>()
    /// KEYRAW comes only from a PC receiver (it said its refresh rate in INFO)
    var rawKeysAllowed = false

    /// a line for input.log (what was done with an event, or why not)
    var log: ((String) -> Void)?
    // double / triple clicks: macOS counts them only if the events say so
    private var clickCount = 0
    private var lastDownAt = 0.0
    private var lastDownPoint = CGPoint.zero
    private let source = CGEventSource(stateID: .hidSystemState)

    init(displayID: CGDirectDisplayID) {
        self.displayID = displayID
        // posted events must not be held back after the pointer moves (the default is 0.25 s)
        source?.localEventsSuppressionInterval = 0
    }

    static func trusted(prompt: Bool) -> Bool {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([key: prompt] as CFDictionary)
    }

    private var anyHeld: Bool { return held[0] || held[1] || held[2] }

    private func point(_ x: Int, _ y: Int) -> CGPoint {
        let b = CGDisplayBounds(displayID)
        let p = InputDecoder.toScreen(x, y, left: Double(b.minX), top: Double(b.minY), width: Double(b.width), height: Double(b.height))
        return CGPoint(x: p.0, y: p.1)
    }

    private func pointerNow() -> CGPoint { return CGEvent(source: nil)?.location ?? .zero }

    private func post(_ e: CGEvent?) { e?.post(tap: .cghidEventTap) }

    private func mouse(_ type: CGEventType, _ at: CGPoint, _ button: Int, clicks: Int = 0, delta: CGPoint? = nil) {
        let b: CGMouseButton = button == 0 ? .left : button == 1 ? .right : .center
        let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: at, mouseButton: b)
        if e == nil { log?("CGEvent \(type.rawValue) could not be made") }
        if let d = delta {
            e?.setIntegerValueField(.mouseEventDeltaX, value: Int64(d.x))
            e?.setIntegerValueField(.mouseEventDeltaY, value: Int64(d.y))
        }
        if clicks > 0 { e?.setIntegerValueField(.mouseEventClickState, value: Int64(clicks)) }
        if button == 2 { e?.setIntegerValueField(.mouseEventButtonNumber, value: 2) }
        if !heldKeys.isEmpty { e?.flags = modifierFlags() } // Command-click and the like
        post(e)
    }

    private func downType(_ b: Int) -> CGEventType { return b == 0 ? .leftMouseDown : b == 1 ? .rightMouseDown : .otherMouseDown }
    private func upType(_ b: Int) -> CGEventType { return b == 0 ? .leftMouseUp : b == 1 ? .rightMouseUp : .otherMouseUp }
    private func dragType(_ b: Int) -> CGEventType { return b == 0 ? .leftMouseDragged : b == 1 ? .rightMouseDragged : .otherMouseDragged }

    private func press(_ button: Int, down: Bool, at p: CGPoint, borrow: Bool = false) {
        if down {
            if borrow && !anyHeld && saved == nil { saved = pointerNow() }
            let now = Date().timeIntervalSinceReferenceDate
            let near = abs(p.x - lastDownPoint.x) < 8 && abs(p.y - lastDownPoint.y) < 8
            clickCount = now - lastDownAt < NSEvent.doubleClickInterval && near ? min(clickCount + 1, 3) : 1
            lastDownAt = now
            lastDownPoint = p
            held[button] = true
            mouse(.mouseMoved, p, button)
            mouse(downType(button), p, button, clicks: clickCount)
        } else {
            held[button] = false
            mouse(upType(button), p, button, clicks: max(1, clickCount))
            if !anyHeld { putBack() }
        }
    }

    /// CLICKAT / WHEELAT: the Mac's user gets the pointer back
    private func putBack() {
        guard let s = saved else { return }
        saved = nil
        // a posted move, not CGWarpMouseCursorPosition: a warp makes macOS ignore the events that
        // follow it for a moment (the next scroll or click would be lost)
        mouse(.mouseMoved, s, 0)
    }

    private func onShownScreen(_ p: CGPoint) -> Bool { return CGDisplayBounds(displayID).contains(p) }

    private func shownCenter() -> CGPoint {
        let b = CGDisplayBounds(displayID)
        return CGPoint(x: b.midX, y: b.midY)
    }

    /// The TV mouse moves the pointer like a real mouse, but only on the shown screen. When it is
    /// picked up again (3 s unused) and the Mac's user left the pointer on another screen (a
    /// virtual screen: the pointer is on the Mac's own), it first comes to the shown screen's middle.
    private func relativeStart() -> (CGPoint, Bool) {
        let now = Date().timeIntervalSinceReferenceDate
        let stale = now - lastRelative > 3
        lastRelative = now
        if stale || relPos == nil { relPos = pointerNow() }
        let p = relPos!
        if stale && !anyHeld && !onShownScreen(p) {
            let c = shownCenter()
            mouse(.mouseMoved, c, 0)
            relPos = c
            log?("pointer was at \(Int(p.x)),\(Int(p.y)), off the shown screen: brought to \(Int(c.x)),\(Int(c.y))")
            return (c, true)
        }
        return (p, false)
    }

    /// a drag cut off by the Wi-Fi must not leave a button held
    func releaseButtons() {
        for b in 0..<3 where held[b] {
            held[b] = false
            mouse(upType(b), pointerNow(), b)
        }
        swallowed = [false, false, false]
        putBack()
        // keys from a PC receiver: none stays down (a held Command would turn every click into Command-click)
        for k in heldKeys { rawKey(k, down: false) }
        heldKeys.removeAll()
    }

    // ---- KEYRAW: the whole keyboard of a PC receiver ------------------------------------------

    private func modifierFlags() -> CGEventFlags {
        var f: CGEventFlags = []
        for k in heldKeys {
            switch KeyMap.modifier(k) {
            case .shift: f.insert(.maskShift)
            case .control: f.insert(.maskControl)
            case .option: f.insert(.maskAlternate)
            case .command: f.insert(.maskCommand)
            case nil: break
            }
        }
        return f
    }

    private func rawKey(_ k: UInt16, down: Bool) {
        let e = CGEvent(keyboardEventSource: source, virtualKey: k, keyDown: down)
        var f = modifierFlags()
        if KeyMap.isFunctionKey(k) { f.insert(.maskSecondaryFn) }
        if KeyMap.isArrow(k) { f.insert(.maskNumericPad) }
        if KeyMap.modifier(k) != nil {
            // a modifier on its own is a "flags changed" event
            e?.type = .flagsChanged
        }
        e?.flags = f
        post(e)
    }

    private func applyRawKey(_ e: InputEvent) {
        guard rawKeysAllowed else { log?("raw key refused: not a PC receiver"); return }
        guard let k = KeyMap.macKey(vk: e.key, extended: e.extended) else { return }
        if e.down {
            // a key (not Shift / Ctrl / ...) goes only to a window on the shown screen
            if KeyMap.modifier(k) == nil && !typingOnShownScreen() { log?("raw key refused: the front window is not on the shown screen"); return }
            heldKeys.insert(k)
        } else {
            // a key let go that was never pressed here (refused, or pressed before the cast) is left alone
            guard heldKeys.remove(k) != nil else { return }
        }
        rawKey(k, down: e.down)
    }

    private func scroll(_ dy: Int, _ dx: Int, at p: CGPoint, borrow: Bool = false, move: Bool = true) {
        let keep = !borrow || anyHeld
        if !keep && saved == nil { saved = pointerNow() }
        if move { mouse(.mouseMoved, p, 0) } // the window under the finger gets the scroll
        // 120 = one wheel notch; about 40 pixels on the Mac
        let e = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2,
                        wheel1: Int32(dy / 3), wheel2: Int32(dx / 3), wheel3: 0)
        e?.location = p
        post(e)
        if !keep { putBack() }
    }

    func apply(_ e: InputEvent) {
        switch e.kind {
        case .move:
            lastX = e.x; lastY = e.y; relative = false
            // a bare move only remembers the spot (for the wheel); with a finger down it drags
            if let b = held.firstIndex(of: true) { mouse(dragType(b), point(e.x, e.y), b) }
        case .button:
            lastX = e.x; lastY = e.y; relative = false
            press(e.button, down: e.down, at: point(e.x, e.y))
        case .clickAt:
            lastX = e.x; lastY = e.y
            press(e.button, down: e.down, at: point(e.x, e.y), borrow: true)
        case .buttonHere:
            relative = true
            if e.down {
                // the first press after a while with the pointer on another screen only brings it here
                if relativeStart().1 { swallowed[e.button] = true; log?("buttonHere \(e.button) down: only brought the pointer"); return }
            } else if swallowed[e.button] {
                swallowed[e.button] = false
                log?("buttonHere \(e.button) up: swallowed")
                return
            }
            held[e.button] = e.down
            let at = relPos ?? pointerNow()
            mouse(e.down ? downType(e.button) : upType(e.button), at, e.button, clicks: 1)
            log?("buttonHere \(e.button) \(e.down ? "down" : "up") at \(Int(at.x)),\(Int(at.y))")
        case .moveBy:
            relative = true
            let (p, _) = relativeStart()
            // kept on the shown screen: the TV's user sees no other, a pointer gone there is lost
            let to = clampToShown(CGPoint(x: p.x + CGFloat(e.x), y: p.y + CGFloat(e.y)))
            relPos = to
            let d = CGPoint(x: e.x, y: e.y)
            if let b = held.firstIndex(of: true) { mouse(dragType(b), to, b, delta: d) } else { mouse(.mouseMoved, to, 0, delta: d) }
            log?("moveBy \(e.x),\(e.y) -> \(Int(to.x)),\(Int(to.y))")
        case .wheel:
            if relative {
                // the TV mouse's wheel: where its pointer is, without moving it
                let at = relPos ?? pointerNow()
                scroll(e.wheelY, e.wheelX, at: at, move: false)
                log?("wheel \(e.wheelY),\(e.wheelX) at the pointer \(Int(at.x)),\(Int(at.y))")
                return
            }
            scroll(e.wheelY, e.wheelX, at: point(lastX, lastY))
        case .wheelAt:
            scroll(e.wheelY, e.wheelX, at: point(e.x, e.y), borrow: true)
        case .text:
            if typingOnShownScreen() { type(e.text); log?("text typed (\(e.text.count) chars)") } else { log?("text refused: the front window is not on the shown screen") }
        case .key:
            if typingOnShownScreen(), let k = MacInput.keyCodes[e.key] { key(k); log?("key \(e.key) pressed") } else { log?("key \(e.key) refused: the front window is not on the shown screen") }
        case .keyRaw:
            applyRawKey(e) // not logged: the log must not record what is typed
        case .moveTo:
            // a PC receiver's mouse moves the real pointer, on the shown screen
            relative = true
            lastRelative = Date().timeIntervalSinceReferenceDate
            let to = point(e.x, e.y)
            relPos = to
            if let b = held.firstIndex(of: true) { mouse(dragType(b), to, b) } else { mouse(.mouseMoved, to, 0) }
        case .wheelHere:
            let at = relPos ?? pointerNow()
            scroll(e.wheelY, e.wheelX, at: at, move: false)
        }
        if e.kind != .moveBy && e.kind != .buttonHere && e.kind != .text && e.kind != .key && e.kind != .keyRaw && e.kind != .moveTo { log?("\(e.kind) \(e.button) \(e.down) \(e.x),\(e.y) wheel \(e.wheelY),\(e.wheelX) applied") }
    }

    /// a point kept on the shown screen
    private func clampToShown(_ p: CGPoint) -> CGPoint {
        let r = CGDisplayBounds(displayID)
        return CGPoint(x: min(max(p.x, r.minX), r.maxX - 1), y: min(max(p.y, r.minY), r.maxY - 1))
    }

    /// Typing goes only into a window on the shown screen: the frontmost app's front window must
    /// have its middle there (as on Windows), so a phone or TV cannot type into what it does not see.
    private func typingOnShownScreen() -> Bool {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]
        else { return false }
        let shown = CGDisplayBounds(displayID)
        for w in list {
            guard (w[kCGWindowOwnerPID as String] as? Int32) == pid, (w[kCGWindowLayer as String] as? Int) == 0,
                  let d = w[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: d), r.width > 1, r.height > 1 else { continue }
            return shown.contains(CGPoint(x: r.midX, y: r.midY)) // the list goes front to back
        }
        return false
    }

    /// Windows virtual-key codes from the page to macOS key codes
    static let keyCodes: [UInt8: CGKeyCode] = [
        0x08: 0x33, // Backspace -> Delete
        0x09: 0x30, // Tab
        0x0D: 0x24, // Enter -> Return
        0x1B: 0x35, // Esc
        0x20: 0x31, // Space
        0x21: 0x74, // Page Up
        0x22: 0x79, // Page Down
        0x23: 0x77, // End
        0x24: 0x73, // Home
        0x25: 0x7B, // Left
        0x26: 0x7E, // Up
        0x27: 0x7C, // Right
        0x28: 0x7D, // Down
        0x2E: 0x75, // Delete -> Forward Delete
    ]

    private func key(_ code: CGKeyCode) {
        post(CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: true))
        post(CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false))
    }

    /// any text, also Lithuanian letters, whatever keyboard layout the Mac has
    private func type(_ text: String) {
        let units = Array(text.utf16)
        var i = 0
        while i < units.count {
            let n = min(20, units.count - i) // a key event carries at most 20 UTF-16 units
            var chunk = Array(units[i..<(i + n)])
            for down in [true, false] {
                let e = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: down)
                e?.keyboardSetUnicodeString(stringLength: n, unicodeString: &chunk)
                post(e)
            }
            i += n
        }
    }
}

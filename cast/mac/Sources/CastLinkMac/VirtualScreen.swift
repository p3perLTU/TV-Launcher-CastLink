// A virtual second screen with CoreGraphics' private CGVirtualDisplay (as DeskPad does): macOS
// sees one more monitor, arranged in System Settings -> Displays like a real one, and CastLink
// shows it on the iPad. It exists while this app runs.

import CGVirtualDisplayPrivate
import CoreGraphics
import Foundation

final class VirtualScreen {
    let display: CGVirtualDisplay
    var displayID: CGDirectDisplayID { return display.displayID }

    /// The sizes offered in System Settings -> Displays, in points, largest last: from "looks like
    /// the iPad" (half the pixels each way, sharp at 2x) up to the device's full pixel count (more
    /// room, smaller text). The capture always sends the device's pixel size; macOS scales.
    static func modes(width: Int, height: Int) -> [(Int, Int)] {
        var out: [(Int, Int)] = []
        for f in [0.5, 0.6, 2.0 / 3.0, 0.75, 1.0] {
            let w = Int((Double(width) * f).rounded()) & ~1, h = Int((Double(height) * f).rounded()) & ~1
            if !out.contains(where: { $0 == (w, h) }) { out.append((w, h)) }
        }
        return out
    }

    /// width x height in pixels (the device's screen). With hiDPI the default size has half as many
    /// points each way, drawn sharp at 2x, like the iPad itself (2160x1620 -> looks like 1080x810);
    /// larger sizes can be picked in System Settings -> Displays.
    init(width: Int, height: Int, hiDPI: Bool, ppi: Double = 264, name: String = "CastLink") throws {
        let d = CGVirtualDisplayDescriptor()
        d.setDispatchQueue(DispatchQueue.main)
        d.name = name
        // a HiDPI size is drawn at twice its points
        d.maxPixelsWide = UInt32(hiDPI ? width * 2 : width)
        d.maxPixelsHigh = UInt32(hiDPI ? height * 2 : height)
        d.sizeInMillimeters = CGSize(width: Double(width) / ppi * 25.4, height: Double(height) / ppi * 25.4)
        d.productID = 0xCA57
        d.vendorID = 0x1A2B
        d.serialNum = 1
        d.terminationHandler = { _, _ in }
        guard let v = CGVirtualDisplay(descriptor: d) else {
            throw NSError(domain: "CastLink", code: 3, userInfo: [NSLocalizedDescriptionKey: "macOS refused to make a virtual screen"])
        }
        let s = CGVirtualDisplaySettings()
        s.hiDPI = hiDPI ? 1 : 0
        // the first mode is the one macOS starts with
        let sizes = hiDPI ? VirtualScreen.modes(width: width, height: height) : [(width, height)]
        s.modes = sizes.map { CGVirtualDisplayMode(width: UInt($0.0), height: UInt($0.1), refreshRate: 60) }
        if !v.apply(s) {
            throw NSError(domain: "CastLink", code: 4, userInfo: [NSLocalizedDescriptionKey: "the virtual screen did not take the size \(width)x\(height)"])
        }
        display = v
    }
}

/// A screen's size in pixels (what the capture and the encoder use).
func pixelSize(_ id: CGDirectDisplayID) -> (Int, Int) {
    if let m = CGDisplayCopyDisplayMode(id) { return (m.pixelWidth, m.pixelHeight) }
    return (CGDisplayPixelsWide(id), CGDisplayPixelsHigh(id))
}

func activeDisplays() -> [CGDirectDisplayID] {
    var n: UInt32 = 0
    CGGetActiveDisplayList(0, nil, &n)
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(n))
    CGGetActiveDisplayList(n, &ids, &n)
    return Array(ids.prefix(Int(n)))
}

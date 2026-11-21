// swift-tools-version:5.7
// CastLink for macOS: shows this Mac's screen (or a virtual second screen) on an iPad, a phone
// or another computer in its browser, the same page and protocol as CastLink.exe on Windows.
// Build: ./build.sh   (needs the Xcode Command Line Tools, macOS 13 or newer)

import PackageDescription

let package = Package(
    name: "CastLinkMac",
    platforms: [.macOS(.v13)],
    targets: [
        // CoreGraphics' private virtual display classes (the same ones DeskPad and BetterDisplay use)
        .target(name: "CGVirtualDisplayPrivate", path: "Sources/CGVirtualDisplayPrivate"),
        // pure parts with no macOS-only APIs: fMP4, WebSocket framing, input decoding, the code throttle
        .target(name: "CastLinkCore", path: "Sources/CastLinkCore"),
        .executableTarget(
            name: "CastLinkMac",
            dependencies: ["CastLinkCore", "CGVirtualDisplayPrivate"],
            path: "Sources/CastLinkMac",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("VideoToolbox"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("ApplicationServices"),
            ]),
        .executableTarget(
            name: "CastLinkCoreTests",
            dependencies: ["CastLinkCore"],
            path: "Tests/CastLinkCoreTests"),
    ]
)

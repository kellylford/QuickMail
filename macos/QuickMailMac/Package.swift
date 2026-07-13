// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "QuickMailMac",
    platforms: [.macOS(.v14)],
    targets: [
        // Core mail engine: IMAP, SMTP, MIME, account store. No UI imports.
        // Kept as a separate target so it can be exercised headlessly (tests,
        // qmcli) and later replaced by / merged with a shared cross-platform core.
        .target(
            name: "QuickMailCore",
            path: "Sources/QuickMailCore"
        ),
        // The Mac app: SwiftUI/AppKit UI over QuickMailCore.
        .executableTarget(
            name: "QuickMailMac",
            dependencies: ["QuickMailCore"],
            path: "Sources/QuickMailMac"
        ),
        // Headless CLI used to smoke-test the engine end to end against a
        // dev IMAP/SMTP server without launching the GUI.
        .executableTarget(
            name: "qmcli",
            dependencies: ["QuickMailCore"],
            path: "Sources/qmcli"
        ),
        .testTarget(
            name: "QuickMailMacTests",
            dependencies: ["QuickMailCore"],
            path: "Tests/QuickMailMacTests"
        ),
    ]
)

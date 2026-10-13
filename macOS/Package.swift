// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "AfanTraders",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "AfanTraders",
            path: "Sources/AfanTraders",
            linkerSettings: [.linkedLibrary("sqlite3")]
        )
    ]
)

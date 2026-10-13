import SwiftUI
import AppKit

/// 3D icon from Resources/Icons (Microsoft Fluent 3D, MIT). Swap the PNG files
/// for any other set (e.g. IconScout) without code changes. Falls back to an SF Symbol.
struct Icon3D: View {
    let name: String
    var fallback: String = "questionmark.square"
    var size: CGFloat = 22

    var body: some View {
        if let img = Icon3D.image(name) {
            Image(nsImage: img).resizable().interpolation(.high).scaledToFit().frame(width: size, height: size)
        } else {
            Image(systemName: fallback).font(.system(size: size * 0.75)).frame(width: size, height: size)
        }
    }

    private static var cache: [String: NSImage] = [:]

    static func image(_ name: String) -> NSImage? {
        if let hit = cache[name] { return hit }
        let candidates = [
            Bundle.main.url(forResource: name, withExtension: "png", subdirectory: "Icons"),
            // During development (swift run), read straight from the source folder.
            URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent()
                .appendingPathComponent("Resources/Icons/\(name).png"),
        ]
        for url in candidates.compactMap({ $0 }) {
            if let img = NSImage(contentsOf: url) {
                cache[name] = img
                return img
            }
        }
        return nil
    }
}

// Draws the app icon (1024×1024 PNG). Usage: swift make-icon.swift out.png
import AppKit

let size: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

let inset: CGFloat = 100
let rect = NSRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
let shape = NSBezierPath(roundedRect: rect, xRadius: 185, yRadius: 185)
let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
shadow.shadowBlurRadius = 30
shadow.shadowOffset = NSSize(width: 0, height: -12)
NSGraphicsContext.saveGraphicsState()
shadow.set()
NSColor(red: 0.12, green: 0.23, blue: 0.40, alpha: 1).setFill()
shape.fill()
NSGraphicsContext.restoreGraphicsState()
NSGradient(starting: NSColor(red: 0.20, green: 0.38, blue: 0.62, alpha: 1),
           ending: NSColor(red: 0.08, green: 0.16, blue: 0.30, alpha: 1))!.draw(in: shape, angle: -90)

// White document card
let doc = NSRect(x: 300, y: 250, width: 424, height: 540)
NSColor.white.setFill()
NSBezierPath(roundedRect: doc, xRadius: 42, yRadius: 42).fill()
let navy = NSColor(red: 0.12, green: 0.23, blue: 0.40, alpha: 1)
navy.withAlphaComponent(0.85).setFill()
NSBezierPath(roundedRect: NSRect(x: 350, y: 680, width: 220, height: 40), xRadius: 20, yRadius: 20).fill()
navy.withAlphaComponent(0.25).setFill()
for (i, w) in [324.0, 280, 324, 240].enumerated() {
    NSBezierPath(roundedRect: NSRect(x: 350, y: 590 - Double(i) * 70, width: w, height: 28), xRadius: 14, yRadius: 14).fill()
}
// Green check badge
let badge = NSRect(x: 600, y: 200, width: 230, height: 230)
NSColor(red: 0.13, green: 0.62, blue: 0.38, alpha: 1).setFill()
NSBezierPath(ovalIn: badge).fill()
NSColor.white.setStroke()
let check = NSBezierPath()
check.lineWidth = 34
check.lineCapStyle = .round
check.lineJoinStyle = .round
check.move(to: NSPoint(x: 662, y: 318))
check.line(to: NSPoint(x: 705, y: 272))
check.line(to: NSPoint(x: 775, y: 362))
check.stroke()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))

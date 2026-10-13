import SwiftUI
import AppKit
import UniformTypeIdentifiers

enum Theme {
    static let navy = Color(red: 0.12, green: 0.23, blue: 0.40)
    static let green = Color(red: 0.13, green: 0.60, blue: 0.36)
    static let orange = Color(red: 0.92, green: 0.55, blue: 0.10)
    static let blue = Color(red: 0.20, green: 0.45, blue: 0.85)
    static let red = Color(red: 0.85, green: 0.25, blue: 0.25)
}

/// Caption label above a control.
struct Field<Content: View>: View {
    let label: String
    let content: Content

    init(_ label: String, @ViewBuilder content: () -> Content) {
        self.label = label
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            content
        }
    }
}

/// Number text field that updates the value as you type.
struct NumField: View {
    let placeholder: String
    @Binding var value: Double
    var width: CGFloat? = nil

    @State private var text = ""
    @FocusState private var focused: Bool

    init(_ placeholder: String = "", value: Binding<Double>, width: CGFloat? = nil) {
        self.placeholder = placeholder
        self._value = value
        self.width = width
    }

    var body: some View {
        TextField(placeholder, text: $text)
            .textFieldStyle(.roundedBorder)
            .multilineTextAlignment(.trailing)
            .monospacedDigit()
            .focused($focused)
            .frame(width: width)
            .onAppear { text = Fmt.editable(value) }
            .onChange(of: text) { _, new in
                if let v = Fmt.parse(new) {
                    if v != value { value = v }
                } else if new.trimmed.isEmpty {
                    value = 0
                }
            }
            .onChange(of: value) { _, new in
                if !focused || Fmt.parse(text) != new { text = Fmt.editable(new) }
            }
            .onChange(of: focused) { _, isFocused in
                if !isFocused { text = Fmt.editable(value) }
            }
    }
}

struct StatusPill: View {
    let text: String
    let color: Color
    var icon: String? = nil

    var body: some View {
        HStack(spacing: 4) {
            if let icon { Image(systemName: icon).font(.caption2.weight(.bold)) }
            Text(text).font(.caption.weight(.semibold)).lineLimit(1)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .foregroundStyle(color)
        .background(Capsule().fill(color.opacity(0.14)))
    }
}

struct ItemThumb: View {
    let data: Data?
    var size: CGFloat = 28

    var body: some View {
        Group {
            if let data, let img = NSImage(data: data) {
                Image(nsImage: img).resizable().scaledToFill()
            } else {
                Image(systemName: "shippingbox")
                    .font(.system(size: size * 0.45))
                    .foregroundStyle(.tertiary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.primary.opacity(0.05))
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.2))
    }
}

struct Card: ViewModifier {
    var padding: CGFloat = 16
    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.primary.opacity(0.08)))
    }
}

extension View {
    func card(padding: CGFloat = 16) -> some View { modifier(Card(padding: padding)) }
}

/// Title bar at the top of every editor sheet.
struct SheetHeader: View {
    let title: String
    let subtitle: String
    let icon: String
    var tint: Color = Theme.navy
    var icon3D: String? = nil

    var body: some View {
        HStack(spacing: 12) {
            Group {
                if let icon3D, Icon3D.image(icon3D) != nil {
                    Icon3D(name: icon3D, size: 40)
                } else {
                    Image(systemName: icon)
                        .font(.system(size: 20, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 40, height: 40)
                        .background(RoundedRectangle(cornerRadius: 10).fill(tint.gradient))
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.title2.weight(.semibold))
                if !subtitle.isEmpty { Text(subtitle).font(.callout).foregroundStyle(.secondary) }
            }
            Spacer()
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
    }
}

/// Quotation → Delivery Challan → Invoice progress strip.
struct FlowSteps: View {
    let quotationSaved: Bool
    let challan: Challan?
    let invoice: Invoice?

    var body: some View {
        HStack(spacing: 10) {
            step("Quotation", icon: "doc.text", done: quotationSaved, detail: quotationSaved ? "Saved" : "Not saved")
            arrow
            step("Delivery Challan", icon: "truck.box",
                 done: challan?.status == .approved,
                 pending: challan?.status == .pending,
                 detail: challan.map { "\($0.number) · \($0.status == .approved ? "Approved" : "Pending approval")" } ?? "Not created")
            arrow
            step("Invoice", icon: "banknote", done: invoice != nil, detail: invoice?.number ?? "Not created")
        }
    }

    private var arrow: some View {
        Image(systemName: "chevron.right").font(.caption.weight(.bold)).foregroundStyle(.tertiary)
    }

    private func step(_ title: String, icon: String, done: Bool, pending: Bool = false, detail: String) -> some View {
        let color: Color = done ? Theme.green : (pending ? Theme.orange : .secondary)
        return HStack(spacing: 8) {
            Image(systemName: done ? "checkmark.circle.fill" : (pending ? "clock.fill" : icon))
                .foregroundStyle(color)
                .font(.system(size: 16, weight: .semibold))
            VStack(alignment: .leading, spacing: 0) {
                Text(title).font(.caption.weight(.semibold))
                Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8).fill(color.opacity(0.08)))
    }
}

/// Shown in place of a cost price while costs are hidden.
struct HiddenCost: View {
    var body: some View {
        Text("••••••").foregroundStyle(.tertiary).help("Cost hidden – click the eye button in the toolbar to show")
    }
}

struct CostToggleButton: View {
    @EnvironmentObject var store: AppStore
    var body: some View {
        Button {
            store.showCost.toggle()
        } label: {
            Label(store.showCost ? "Hide Cost" : "Show Cost", systemImage: store.showCost ? "eye" : "eye.slash")
        }
        .labelStyle(.titleAndIcon)
        .help("Show or hide cost prices (⇧⌘K)")
    }
}

enum ImageUtil {
    /// Product photos: JPEG, at most 600 px – about 10× smaller than PNG, which keeps the
    /// database and cloud sync small. `keepTransparency` saves PNG instead (for logos).
    static func compressed(_ image: NSImage, maxSide: CGFloat = 600, keepTransparency: Bool = false) -> Data? {
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        let scale = min(1, maxSide / CGFloat(max(cg.width, cg.height)))
        let w = max(1, Int(CGFloat(cg.width) * scale))
        let h = max(1, Int(CGFloat(cg.height) * scale))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        if !keepTransparency {
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))   // JPEG has no transparency: use white
            ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        }
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let out = ctx.makeImage() else { return nil }
        let rep = NSBitmapImageRep(cgImage: out)
        return keepTransparency ? rep.representation(using: .png, properties: [:])
                                : rep.representation(using: .jpeg, properties: [.compressionFactor: 0.72])
    }

    static func pickImage(maxSide: CGFloat = 600, keepTransparency: Bool = false) -> Data? {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url, let img = NSImage(contentsOf: url) else { return nil }
        return compressed(img, maxSide: maxSide, keepTransparency: keepTransparency)
    }
}

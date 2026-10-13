import SwiftUI
import AppKit
import CoreGraphics

/// Turns a PrintableDoc into an A4 PDF. Pages are laid out with SwiftUI
/// (vector text) and drawn on top of the user's letterhead, if one is set.
@MainActor
enum PDFRenderer {
    static let pageSize = CGSize(width: 595.28, height: 841.89)

    // Fixed block heights used for pagination (the page view uses the same values).
    static let builtInHeaderHeight: CGFloat = 112
    static let builtInFooterHeight: CGFloat = 40
    static let titleHeight: CGFloat = 46
    static let infoHeight: CGFloat = 112
    static let tableHeaderHeight: CGFloat = 24
    static let pageNumberHeight: CGFloat = 18

    static func rowHeight(_ line: DocLine) -> CGFloat { line.details.trimmed.isEmpty ? 22 : 34 }

    /// Exact height of the totals / notes / terms / signatures block, measured by laying it out.
    static func closingHeight(_ doc: PrintableDoc, settings: CompanySettings) -> CGFloat {
        let page = PDFPageView(doc: doc, page: Page(rows: [], number: 1, count: 1), settings: settings, hasLetterhead: true)
        let block = page.closing
            .padding(.top, 10)
            .frame(width: pageSize.width - 80)
            .environment(\.colorScheme, .light)
        let host = NSHostingView(rootView: block)
        return ceil(host.fittingSize.height) + 6   // small safety margin
    }

    struct Page {
        var rows: [(index: Int, line: DocLine)]
        var number: Int
        var count: Int
        var isFirst: Bool { number == 1 }
        var isLast: Bool { number == count }
    }

    static func paginate(_ doc: PrintableDoc, settings: CompanySettings) -> [Page] {
        let hasLetterhead = settings.letterhead != nil
        let top = hasLetterhead ? CGFloat(settings.letterheadTop) : builtInHeaderHeight
        let bottom = hasLetterhead ? CGFloat(settings.letterheadBottom) : builtInFooterHeight
        let usable = pageSize.height - top - bottom - pageNumberHeight - titleHeight
        let firstBudget = usable - infoHeight - tableHeaderHeight
        let otherBudget = usable - tableHeaderHeight

        var pages: [[(index: Int, line: DocLine)]] = [[]]
        var used: CGFloat = 0
        var budget = firstBudget
        for (i, line) in doc.lines.enumerated() {
            let h = rowHeight(line)
            if used + h > budget && !pages[pages.count - 1].isEmpty {
                pages.append([])
                used = 0
                budget = otherBudget
            }
            pages[pages.count - 1].append((i, line))
            used += h
        }
        // Totals, notes and signatures must fit under the last rows.
        if used + closingHeight(doc, settings: settings) > budget {
            pages.append([])
        }
        return pages.enumerated().map { Page(rows: $1, number: $0 + 1, count: pages.count) }
    }

    static func render(_ doc: PrintableDoc, settings: CompanySettings) -> Data {
        let data = NSMutableData()
        var box = CGRect(origin: .zero, size: pageSize)
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let ctx = CGContext(consumer: consumer, mediaBox: &box, nil) else { return Data() }

        let letterhead = Letterhead(data: settings.letterhead)
        for page in paginate(doc, settings: settings) {
            ctx.beginPDFPage(nil)
            letterhead?.draw(in: ctx, box: box)
            let view = PDFPageView(doc: doc, page: page, settings: settings, hasLetterhead: letterhead != nil)
            let renderer = ImageRenderer(content: view)
            renderer.proposedSize = ProposedViewSize(pageSize)
            renderer.render { _, draw in draw(ctx) }
            ctx.endPDFPage()
        }
        ctx.closePDF()
        return data as Data
    }
}

/// The user's letterhead: first page of a PDF, or an image stretched to A4.
struct Letterhead {
    private var pdfPage: CGPDFPage?
    private var image: CGImage?

    init?(data: Data?) {
        guard let data, !data.isEmpty else { return nil }
        let isPDF = data.prefix(5) == Data("%PDF-".utf8)
        if isPDF, let provider = CGDataProvider(data: data as CFData),
           let doc = CGPDFDocument(provider), let page = doc.page(at: 1) {
            pdfPage = page
        } else if let img = NSImage(data: data)?.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            image = img
        } else {
            return nil
        }
    }

    func draw(in ctx: CGContext, box: CGRect) {
        ctx.saveGState()
        defer { ctx.restoreGState() }
        if let page = pdfPage {
            let r = page.getBoxRect(.mediaBox)
            let s = min(box.width / r.width, box.height / r.height)
            ctx.translateBy(x: (box.width - r.width * s) / 2, y: (box.height - r.height * s) / 2)
            ctx.scaleBy(x: s, y: s)
            ctx.translateBy(x: -r.origin.x, y: -r.origin.y)
            ctx.drawPDFPage(page)
        } else if let image {
            ctx.interpolationQuality = .high
            ctx.draw(image, in: box)
        }
    }
}

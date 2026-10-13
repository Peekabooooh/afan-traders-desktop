import SwiftUI

/// Prints any report as landscape A4 pages.
@MainActor
enum ReportPDF {
    static let page = CGSize(width: 841.89, height: 595.28)
    static let margin: CGFloat = 30
    static let rowHeight: CGFloat = 16

    static func render(_ table: ReportTable, rows: [ReportRow], period: String, settings: CompanySettings,
                       showCost: Bool) -> Data {
        // Hidden cost columns are left off the printout.
        let cols = table.columns.indices.filter { showCost || !table.columns[$0].sensitive }
        let natural = cols.reduce(0) { $0 + table.columns[$1].width }
        let scale = min(1, (page.width - margin * 2) / natural)
        let widths = cols.map { table.columns[$0].width * scale }
        let totals = table.totals(for: rows)

        let perPage = Int((page.height - 92 - 30 - 20 - rowHeight) / rowHeight)
        let chunks = stride(from: 0, to: max(rows.count, 1), by: perPage).map {
            Array(rows[$0..<min($0 + perPage, rows.count)])
        }

        let data = NSMutableData()
        var box = CGRect(origin: .zero, size: page)
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let ctx = CGContext(consumer: consumer, mediaBox: &box, nil) else { return Data() }
        for (n, chunk) in chunks.enumerated() {
            ctx.beginPDFPage(nil)
            let view = ReportPage(title: table.title, period: period, company: settings.companyName,
                                  columns: cols.map { table.columns[$0] }, widths: widths,
                                  rows: chunk.map { r in cols.map { r.cells[$0] } },
                                  totals: n == chunks.count - 1 ? totals.map { t in cols.map { t[$0] } } : nil,
                                  pageNo: n + 1, pageCount: chunks.count, fontScale: max(0.8, scale))
            let renderer = ImageRenderer(content: view)
            renderer.proposedSize = ProposedViewSize(page)
            renderer.render { _, draw in draw(ctx) }
            ctx.endPDFPage()
        }
        ctx.closePDF()
        return data as Data
    }
}

private struct ReportPage: View {
    let title: String
    let period: String
    let company: String
    let columns: [ReportColumn]
    let widths: [CGFloat]
    let rows: [[ReportCell]]
    let totals: [ReportCell]?
    let pageNo: Int
    let pageCount: Int
    let fontScale: CGFloat

    private let ink = Color(red: 0.12, green: 0.13, blue: 0.16)
    private let muted = Color(red: 0.42, green: 0.45, blue: 0.50)

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .lastTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(company).font(.system(size: 15, weight: .heavy)).foregroundStyle(Theme.navy)
                    Text(title.uppercased()).font(.system(size: 11, weight: .bold)).tracking(1.2)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 2) {
                    Text("Period: \(period)").font(.system(size: 9, weight: .semibold))
                    Text("Generated \(Fmt.date(Date()))").font(.system(size: 8)).foregroundStyle(muted)
                }
            }
            .padding(.top, 26)
            Rectangle().fill(Theme.navy).frame(height: 2).padding(.top, 8).padding(.bottom, 10)

            row(columns.map { ReportCell(text: $0.title.uppercased(), value: nil) }, header: true)
                .frame(height: 20)
                .background(Theme.navy)
            ForEach(Array(rows.enumerated()), id: \.offset) { i, cells in
                row(cells, header: false)
                    .frame(height: ReportPDF.rowHeight)
                    .background(i.isMultiple(of: 2) ? Color.clear : Theme.navy.opacity(0.05))
            }
            if let totals {
                row(totals, header: false, bold: true)
                    .frame(height: ReportPDF.rowHeight + 2)
                    .background(Theme.navy.opacity(0.12))
            }
            Spacer(minLength: 0)
            HStack {
                Text("\(company) · \(title)")
                Spacer()
                Text("Page \(pageNo) of \(pageCount)")
            }
            .font(.system(size: 7.5)).foregroundStyle(muted)
            .padding(.bottom, 16)
        }
        .padding(.horizontal, ReportPDF.margin)
        .frame(width: ReportPDF.page.width, height: ReportPDF.page.height, alignment: .top)
        .foregroundStyle(ink)
        .environment(\.colorScheme, .light)
    }

    private func row(_ cells: [ReportCell], header: Bool, bold: Bool = false) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(columns.enumerated()), id: \.offset) { i, col in
                Text(cells[i].text)
                    .font(.system(size: (header ? 6.5 : 7.5) * fontScale, weight: header || bold ? .bold : .regular))
                    .monospacedDigit()
                    .foregroundStyle(header ? Color.white : ink)
                    .lineLimit(1)
                    .padding(.horizontal, 4)
                    .frame(width: widths[i], alignment: col.numeric ? .trailing : .leading)
            }
        }
    }
}

import SwiftUI
import AppKit

/// One A4 page of a quotation, delivery challan or invoice.
struct PDFPageView: View {
    let doc: PrintableDoc
    let page: PDFRenderer.Page
    let settings: CompanySettings
    let hasLetterhead: Bool

    private let ink = Color(red: 0.12, green: 0.13, blue: 0.16)
    private let navy = Theme.navy
    private let muted = Color(red: 0.42, green: 0.45, blue: 0.50)
    private let rule = Color(red: 0.82, green: 0.85, blue: 0.89)
    private let zebra = Color(red: 0.12, green: 0.23, blue: 0.40).opacity(0.04)

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if hasLetterhead {
                Color.clear.frame(height: settings.letterheadTop)
            } else {
                builtInHeader.frame(height: PDFRenderer.builtInHeaderHeight)
            }
            titleBar.frame(height: PDFRenderer.titleHeight)
            if page.isFirst {
                infoBlock.frame(height: PDFRenderer.infoHeight)
            }
            if !page.rows.isEmpty {
                tableHeader.frame(height: PDFRenderer.tableHeaderHeight)
                ForEach(page.rows, id: \.line.id) { row in
                    tableRow(row.index, row.line).frame(height: PDFRenderer.rowHeight(row.line))
                }
            }
            if page.isLast {
                closing.padding(.top, 10)
            }
            Spacer(minLength: 0)
            pageNumber.frame(height: PDFRenderer.pageNumberHeight)
            if hasLetterhead {
                Color.clear.frame(height: settings.letterheadBottom)
            } else {
                builtInFooter.frame(height: PDFRenderer.builtInFooterHeight)
            }
        }
        .padding(.horizontal, 40)
        .frame(width: PDFRenderer.pageSize.width, height: PDFRenderer.pageSize.height, alignment: .top)
        .foregroundStyle(ink)
        .environment(\.colorScheme, .light)
    }

    // MARK: Header / footer (used when no letterhead is set)

    private var builtInHeader: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 0)
            HStack(alignment: .center, spacing: 14) {
                if let logo = settings.logo, let img = NSImage(data: logo) {
                    Image(nsImage: img).resizable().scaledToFit().frame(width: 62, height: 62)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(settings.companyName)
                        .font(.system(size: 22, weight: .heavy))
                        .tracking(1)
                        .foregroundStyle(navy)
                    if !settings.tagline.isEmpty {
                        Text(settings.tagline).font(.system(size: 9.5, weight: .medium)).foregroundStyle(muted)
                    }
                }
                Spacer(minLength: 10)
                VStack(alignment: .trailing, spacing: 2) {
                    ForEach(contactLines, id: \.self) { line in
                        Text(line).font(.system(size: 8.5)).foregroundStyle(muted).lineLimit(1)
                    }
                }
            }
            .padding(.bottom, 12)
            Rectangle().fill(navy).frame(height: 2.5)
            Rectangle().fill(navy.opacity(0.25)).frame(height: 1).padding(.top, 2)
        }
    }

    private var contactLines: [String] {
        var lines = settings.address.split(separator: "\n").map(String.init).filter { !$0.trimmed.isEmpty }
        let contact = [settings.phone.isEmpty ? nil : "Tel: \(settings.phone)",
                       settings.email.isEmpty ? nil : settings.email].compactMap { $0 }
        if !contact.isEmpty { lines.append(contact.joined(separator: "  ·  ")) }
        let tax = [settings.ntn.isEmpty ? nil : "NTN: \(settings.ntn)",
                   settings.strn.isEmpty ? nil : "STRN: \(settings.strn)"].compactMap { $0 }
        if !tax.isEmpty { lines.append(tax.joined(separator: "  ·  ")) }
        return Array(lines.prefix(5))
    }

    private var builtInFooter: some View {
        VStack(spacing: 6) {
            Rectangle().fill(rule).frame(height: 0.6)
            HStack {
                Text("Thank you for your business!").font(.system(size: 8, weight: .medium)).foregroundStyle(navy)
                Spacer()
                Text(settings.companyName).font(.system(size: 8)).foregroundStyle(muted)
            }
            Spacer(minLength: 0)
        }
    }

    private var pageNumber: some View {
        HStack {
            Text("\(doc.title) · \(doc.number)")
            Spacer()
            Text("Page \(page.number) of \(page.count)")
        }
        .font(.system(size: 7.5))
        .foregroundStyle(muted)
    }

    // MARK: Title and info

    private var titleBar: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(doc.title)
                .font(.system(size: 17, weight: .bold))
                .tracking(2)
                .foregroundStyle(navy)
            if !page.isFirst {
                Text("(continued)").font(.system(size: 9)).foregroundStyle(muted)
            }
            Spacer()
            Text("No.  ").font(.system(size: 10)).foregroundStyle(muted)
                + Text(doc.number).font(.system(size: 13, weight: .semibold))
        }
        .padding(.top, 16)
        .frame(maxHeight: .infinity, alignment: .top)
    }

    private var infoBlock: some View {
        HStack(alignment: .top, spacing: 12) {
            infoBox(doc.kind == .challan ? "DELIVER TO" : (doc.kind == .quotation ? "QUOTATION FOR" : "BILL TO")) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(doc.customerName).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                    if !doc.customerAddress.isEmpty {
                        Text(doc.customerAddress).font(.system(size: 9)).foregroundStyle(muted).lineLimit(3)
                    }
                    if !doc.customerPhone.isEmpty {
                        Text("Tel: \(doc.customerPhone)").font(.system(size: 9)).foregroundStyle(muted)
                    }
                }
            }
            infoBox("DOCUMENT DETAILS") {
                VStack(alignment: .leading, spacing: 3) {
                    keyValue("Date", Fmt.date(doc.date))
                    keyValue("PO No.", doc.poNumber.isEmpty ? "—" : doc.poNumber)
                    ForEach(Array(doc.info.prefix(4).enumerated()), id: \.offset) { _, kv in
                        keyValue(kv.0, kv.1)
                    }
                }
            }
            .frame(width: 215)
        }
        .padding(.bottom, 12)
    }

    private func infoBox<C: View>(_ title: String, @ViewBuilder content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 7.5, weight: .bold)).tracking(1.2).foregroundStyle(navy)
            content()
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 6).fill(zebra))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(rule, lineWidth: 0.7))
    }

    private func keyValue(_ k: String, _ v: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(k).font(.system(size: 8.5)).foregroundStyle(muted).frame(width: 82, alignment: .leading)
            Text(v).font(.system(size: 9, weight: .semibold)).lineLimit(1)
        }
    }

    // MARK: Table

    private var tableHeader: some View {
        HStack(spacing: 6) {
            Text("S#").frame(width: 22, alignment: .leading)
            Text("ITEM ID").frame(width: 70, alignment: .leading)
            Text("DESCRIPTION").frame(maxWidth: .infinity, alignment: .leading)
            Text("UNIT").frame(width: 34, alignment: .leading)
            Text("QTY").frame(width: 44, alignment: .trailing)
            if doc.showPrices {
                Text("RATE").frame(width: 70, alignment: .trailing)
                Text("AMOUNT").frame(width: 82, alignment: .trailing)
            }
        }
        .font(.system(size: 7.5, weight: .bold))
        .tracking(0.6)
        .foregroundStyle(.white)
        .padding(.horizontal, 8)
        .frame(maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 3).fill(navy))
    }

    private func tableRow(_ index: Int, _ line: DocLine) -> some View {
        HStack(spacing: 6) {
            Text("\(index + 1)").foregroundStyle(muted).frame(width: 22, alignment: .leading)
            Text(line.code).font(.system(size: 8.5, weight: .semibold)).lineLimit(1).frame(width: 70, alignment: .leading)
            VStack(alignment: .leading, spacing: 1) {
                Text(line.name).lineLimit(1)
                if !line.details.trimmed.isEmpty {
                    Text(line.details).font(.system(size: 7.5)).foregroundStyle(muted).lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(line.unit).foregroundStyle(muted).lineLimit(1).frame(width: 34, alignment: .leading)
            Text(Fmt.qty(line.qty)).frame(width: 44, alignment: .trailing)
            if doc.showPrices {
                Text(Fmt.money(line.rate)).frame(width: 70, alignment: .trailing)
                Text(Fmt.money(line.total)).font(.system(size: 9, weight: .semibold)).frame(width: 82, alignment: .trailing)
            }
        }
        .font(.system(size: 9))
        .monospacedDigit()
        .padding(.horizontal, 8)
        .frame(maxHeight: .infinity)
        .background(index.isMultiple(of: 2) ? Color.clear : zebra)
        .overlay(alignment: .bottom) { Rectangle().fill(rule).frame(height: 0.5) }
    }

    // MARK: Totals, notes, signatures

    /// Totals, notes, terms and signatures (also measured by the paginator).
    var closing: some View {
        VStack(alignment: .leading, spacing: 12) {
            if doc.showPrices {
                HStack(alignment: .top, spacing: 16) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("AMOUNT IN WORDS").font(.system(size: 7.5, weight: .bold)).tracking(1).foregroundStyle(navy)
                        Text(Fmt.words(doc.grandTotal, currencyWord: settings.currencyWord))
                            .font(.system(size: 9).italic())
                            .lineLimit(3)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    VStack(spacing: 0) {
                        totalRow("Subtotal", Fmt.money(doc.subtotal))
                        totalRow("Sales Tax @ \(Fmt.qty(doc.taxRate))%", Fmt.money(doc.taxAmount))
                        HStack {
                            Text("GRAND TOTAL").font(.system(size: 9, weight: .bold)).tracking(0.8)
                            Spacer()
                            Text("\(settings.currency) \(Fmt.money(doc.grandTotal))").font(.system(size: 11, weight: .bold))
                        }
                        .foregroundStyle(.white)
                        .padding(.horizontal, 10)
                        .frame(height: 26)
                        .background(RoundedRectangle(cornerRadius: 3).fill(navy))
                    }
                    .frame(width: 220)
                    .monospacedDigit()
                }
            } else {
                HStack {
                    Spacer()
                    Text("Total Quantity:  ").font(.system(size: 9)).foregroundStyle(muted)
                        + Text(Fmt.qty(doc.totalQty)).font(.system(size: 10, weight: .bold))
                }
            }

            if !doc.notes.trimmed.isEmpty {
                textBlock("NOTES", doc.notes, lines: 3)
            }
            if !doc.terms.trimmed.isEmpty {
                textBlock("TERMS & CONDITIONS", doc.terms, lines: 4)
            }
            signatures.padding(.top, 26)
        }
    }

    private func totalRow(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).font(.system(size: 9)).foregroundStyle(muted)
            Spacer()
            Text(value).font(.system(size: 9.5, weight: .medium))
        }
        .padding(.horizontal, 10)
        .frame(height: 22)
        .overlay(alignment: .bottom) { Rectangle().fill(rule).frame(height: 0.5) }
    }

    private func textBlock(_ title: String, _ text: String, lines: Int) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.system(size: 7.5, weight: .bold)).tracking(1).foregroundStyle(navy)
            Text(text).font(.system(size: 8.5)).foregroundStyle(ink.opacity(0.85)).lineLimit(lines)
        }
    }

    private var signatureLabels: [String] {
        switch doc.kind {
        case .quotation: return ["For \(settings.companyName)"]
        case .challan: return ["Prepared By", "Gate / Security", "Received By (Customer)"]
        case .invoice: return ["Received By", "For \(settings.companyName)"]
        }
    }

    private var signatures: some View {
        HStack(alignment: .top, spacing: 30) {
            if signatureLabels.count == 1 { Spacer() }
            ForEach(signatureLabels, id: \.self) { label in
                VStack(spacing: 3) {
                    Rectangle().fill(ink.opacity(0.6)).frame(height: 0.7)
                    Text(label).font(.system(size: 8.5, weight: .semibold))
                    Text("Signature & Stamp").font(.system(size: 7)).foregroundStyle(muted)
                }
                .frame(width: signatureLabels.count == 1 ? 190 : nil)
                .frame(maxWidth: signatureLabels.count == 1 ? 190 : .infinity)
            }
        }
    }
}

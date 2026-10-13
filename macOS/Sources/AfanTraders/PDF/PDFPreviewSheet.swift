import SwiftUI
import PDFKit
import UniformTypeIdentifiers

struct PDFPreviewSheet: View {
    let doc: PreviewDoc
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Icon3D(name: "printer", fallback: "doc.richtext", size: 26)
                Text(doc.title).font(.headline)
                Spacer()
                Button { save() } label: { Label("Save PDF…", systemImage: "square.and.arrow.down") }
                Button { printDoc() } label: { Label("Print…", systemImage: "printer") }
                    .keyboardShortcut("p", modifiers: .command)
                    .buttonStyle(.borderedProminent)
                Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(12)
            Divider()
            PDFKitView(data: doc.data)
        }
        .frame(minWidth: 760, idealWidth: 840, minHeight: 680, idealHeight: 900)
    }

    private func save() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.pdf]
        panel.nameFieldStringValue = doc.fileName
        if panel.runModal() == .OK, let url = panel.url {
            try? doc.data.write(to: url)
        }
    }

    private func printDoc() {
        guard let pdf = PDFDocument(data: doc.data) else { return }
        let info = (NSPrintInfo.shared.copy() as? NSPrintInfo) ?? NSPrintInfo()
        info.paperSize = NSSize(width: PDFRenderer.pageSize.width, height: PDFRenderer.pageSize.height)
        info.topMargin = 0
        info.bottomMargin = 0
        info.leftMargin = 0
        info.rightMargin = 0
        info.orientation = .portrait
        guard let op = pdf.printOperation(for: info, scalingMode: .pageScaleDownToFit, autoRotate: false) else { return }
        op.jobTitle = doc.fileName
        op.showsPrintPanel = true
        op.showsProgressPanel = true
        op.run()
    }
}

struct PDFKitView: NSViewRepresentable {
    let data: Data

    func makeNSView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.displayMode = .singlePageContinuous
        view.displaysPageBreaks = true
        view.backgroundColor = .underPageBackgroundColor
        view.document = PDFDocument(data: data)
        return view
    }

    func updateNSView(_ view: PDFView, context: Context) {
        if view.document?.dataRepresentation() != data {
            view.document = PDFDocument(data: data)
        }
    }
}

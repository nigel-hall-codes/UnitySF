#if canImport(SwiftUI) && canImport(PencilKit)
import SwiftUI
import PencilKit
import PhotosUI

/// The Building Canvas (#282, MVP surface per #365): exactly five tools — Brush, Image,
/// AI Sign, Text, Erase — over one facade's unit square, plus Save. No procedural tools,
/// no region tools, no template concepts. Never touches AI or Unity directly — the view
/// model's `ServerClient` is the sole egress.
public struct FacadeCanvasView: View {
    @StateObject private var vm: FacadeCanvasViewModel
    @State private var drawing = PKDrawing()
    @State private var canvasSize: CGSize = .zero
    @State private var tool: CanvasTool = .brush
    @State private var brushColor: Color = .black
    @State private var showSignSheet = false
    @State private var showImagePicker = false
    @State private var showTextSheet = false

    public init(viewModel: FacadeCanvasViewModel) {
        _vm = StateObject(wrappedValue: viewModel)
    }

    public var body: some View {
        VStack(spacing: 0) {
            toolRail
            canvas
            statusBar
        }
        .sheet(isPresented: $showSignSheet) {
            SignRequestSheet { request in
                Task {
                    if let sign = await vm.requestSign(request) { vm.placeSign(sign) }
                    showSignSheet = false
                }
            }
        }
        .sheet(isPresented: $showImagePicker) {
            ImagePickerView { png in
                Task { await placeUploadedImage(png, name: "upload_\(shortId())") }
            }
        }
        .sheet(isPresented: $showTextSheet) {
            TextToolSheet { text, color in
                showTextSheet = false
                guard let png = Self.renderTextPNG(text, color: UIColor(color)) else { return }
                Task { await placeUploadedImage(png, name: text) }
            }
        }
        .task {
            await vm.load()
            await vm.loadBackdrop()
        }
    }

    // MARK: - Tool rail (the five MVP tools + Save)

    private enum CanvasTool { case brush, erase }

    private var pkTool: PKTool {
        switch tool {
        case .brush: return PKInkingTool(.pen, color: UIColor(brushColor), width: 8)
        case .erase: return PKEraserTool(.bitmap)
        }
    }

    private var toolRail: some View {
        HStack(spacing: 8) {
            toolButton("Brush", systemImage: "paintbrush.pointed", isActive: tool == .brush) {
                tool = .brush
            }
            Button { showImagePicker = true } label: {
                Label("Image", systemImage: "photo")
            }
            Button { showSignSheet = true } label: {
                Label("AI Sign", systemImage: "sparkles")
            }
            Button { showTextSheet = true } label: {
                Label("Text", systemImage: "textformat")
            }
            toolButton("Erase", systemImage: "eraser", isActive: tool == .erase) {
                tool = .erase
            }

            if tool == .brush {
                ColorPicker("Brush color", selection: $brushColor, supportsOpacity: false)
                    .labelsHidden()
            }

            Spacer()
            Button("Save") { save() }
                .buttonStyle(.borderedProminent)
                .disabled(vm.status == .saving)
        }
        .buttonStyle(.bordered)
        .padding()
    }

    private func toolButton(_ title: String, systemImage: String, isActive: Bool,
                            action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
        }
        .buttonStyle(.bordered)
        .tint(isActive ? .blue : nil)
    }

    // MARK: - Canvas

    private var canvas: some View {
        GeometryReader { geo in
            ZStack {
                // Static wall colour (Unity's vertex-coloured wall shows through the alpha channel
                // in the real export; this is the authoring stand-in).
                Rectangle().fill(Color(white: 0.9))
                // Facade reference render for tracing over, when one exists on the server.
                if let data = vm.backdropData, let uiImage = UIImage(data: data) {
                    Image(uiImage: uiImage)
                        .resizable()
                        .scaledToFill()
                        .opacity(0.35)
                        .clipped()
                }
                PencilCanvas(drawing: $drawing, tool: pkTool)
                // Image layers rendered in z-order (array order = ascending z). With the
                // Erase tool active, tapping a placed image removes it.
                ForEach($vm.imageLayers) { $img in
                    PlacedImageView(image: $img, canvasSize: geo.size,
                                    eraseActive: tool == .erase) {
                        vm.imageLayers.removeAll { $0.id == img.id }
                    }
                }
            }
            .onAppear { canvasSize = geo.size }
            .onChange(of: geo.size) { canvasSize = $0 }
        }
        .aspectRatio(1, contentMode: .fit)   // the facade unit square
        .border(Color.secondary)
        .padding()
    }

    private var statusBar: some View {
        HStack {
            switch vm.status {
            case .idle:            Text("Ready").foregroundColor(.secondary)
            case .loading:         ProgressView(); Text("Loading…")
            case .saving:          ProgressView(); Text("Saving…")
            case .saved:           Label("Saved", systemImage: "checkmark.circle").foregroundColor(.green)
            case .queued:          Label("Queued (offline)", systemImage: "arrow.triangle.2.circlepath").foregroundColor(.orange)
            case .failed(let msg): Label(msg, systemImage: "exclamationmark.triangle").foregroundColor(.red)
            }
            Spacer()
            Text("\(vm.imageLayers.count) placed").foregroundColor(.secondary)
        }
        .padding(.horizontal)
        .padding(.bottom, 8)
    }

    // MARK: - Actions

    private func save() {
        let converted = StrokeConversion.strokes(from: drawing, canvasSize: canvasSize)
        // Don't wipe strokes loaded from the server that aren't yet re-hydrated into the on-screen
        // PKDrawing (paint reload is a follow-up): only overwrite when the user painted this session
        // (drawing non-empty), or when there were no strokes to preserve.
        if !converted.isEmpty || vm.paintStrokes.isEmpty {
            vm.paintStrokes = converted
        }
        Task { await vm.save() }
    }

    /// Image and Text tools share one path: PNG → POST /signs/upload → placed image layer.
    private func placeUploadedImage(_ png: Data, name: String) async {
        if let sign = await vm.uploadImageAsset(name: name, png: png) {
            vm.placeSign(sign)
        }
    }

    private func shortId() -> String {
        String(UUID().uuidString.prefix(8)).lowercased()
    }

    /// Render text into a transparent PNG so it rides the existing sign-asset pipeline
    /// (place, save, export) with no new server-side layer kind.
    static func renderTextPNG(_ text: String, color: UIColor) -> Data? {
        guard !text.isEmpty else { return nil }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: UIFont.systemFont(ofSize: 96, weight: .bold),
            .foregroundColor: color,
        ]
        let textSize = (text as NSString).size(withAttributes: attrs)
        let size = CGSize(width: ceil(textSize.width) + 24, height: ceil(textSize.height) + 16)
        let image = UIGraphicsImageRenderer(size: size).image { _ in
            (text as NSString).draw(at: CGPoint(x: 12, y: 8), withAttributes: attrs)
        }
        return image.pngData()
    }
}

// MARK: - PencilCanvas

/// UIKit bridge for PencilKit's canvas. The tool is driven by the MVP tool rail (Brush /
/// Erase) rather than the floating system PKToolPicker.
private struct PencilCanvas: UIViewRepresentable {
    @Binding var drawing: PKDrawing
    let tool: PKTool

    func makeUIView(context: Context) -> PKCanvasView {
        let view = PKCanvasView()
        view.drawingPolicy = .anyInput
        view.backgroundColor = .clear
        view.isOpaque = false
        view.delegate = context.coordinator
        view.drawing = drawing
        view.tool = tool
        return view
    }

    func updateUIView(_ view: PKCanvasView, context: Context) {
        if view.drawing != drawing { view.drawing = drawing }
        view.tool = tool     // PKTool isn't Equatable; setting every update is cheap
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, PKCanvasViewDelegate {
        let parent: PencilCanvas
        init(_ parent: PencilCanvas) { self.parent = parent }
        func canvasViewDrawingDidChange(_ canvasView: PKCanvasView) {
            parent.drawing = canvasView.drawing
        }
    }
}

// MARK: - PlacedImageView

/// A placed image/sign rendered as a movable normalized rect. (Fetching the actual PNG from the
/// server for preview needs the G2 asset GET endpoint, #300 — so the MVP shows a label.)
private struct PlacedImageView: View {
    @Binding var image: FacadeCanvasViewModel.PlacedImage
    let canvasSize: CGSize
    let eraseActive: Bool
    let onDelete: () -> Void

    var body: some View {
        let w = CGFloat(image.rect[2] - image.rect[0]) * canvasSize.width
        let h = CGFloat(image.rect[3] - image.rect[1]) * canvasSize.height
        // Rect is bottom-origin facade UV; convert the centre to top-down screen space.
        let cx = CGFloat((image.rect[0] + image.rect[2]) / 2) * canvasSize.width
        let cy = (1 - CGFloat((image.rect[1] + image.rect[3]) / 2)) * canvasSize.height

        return RoundedRectangle(cornerRadius: 4)
            .strokeBorder(eraseActive ? Color.red : Color.blue, lineWidth: 2)
            .background(Color.blue.opacity(0.08))
            .overlay(Text(image.signAsset.isEmpty ? "image" : image.signAsset)
                        .font(.caption2).lineLimit(1).padding(2))
            .frame(width: max(w, 24), height: max(h, 24))
            .position(x: cx, y: cy)
            .onTapGesture { if eraseActive { onDelete() } }
            .gesture(DragGesture().onChanged { g in move(to: g.location) })
    }

    private func move(to point: CGPoint) {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return }
        let halfW = (image.rect[2] - image.rect[0]) / 2
        let halfH = (image.rect[3] - image.rect[1]) / 2
        let nx = Double(point.x / canvasSize.width)
        let ny = 1 - Double(point.y / canvasSize.height)         // screen → facade bottom-up
        // Clamp the CENTRE so the rect keeps its size and stays on the facade [0,1].
        let cx = min(max(nx, halfW), 1 - halfW)
        let cy = min(max(ny, halfH), 1 - halfH)
        image.rect = [cx - halfW, cy - halfH, cx + halfW, cy + halfH]
    }
}

// MARK: - SignRequestSheet

/// AI-sign request form — exposes all SignRequest fields so the server can pick a provider
/// and apply the neighborhood style context.
private struct SignRequestSheet: View {
    var onSubmit: (SignRequest) -> Void
    @State private var text = ""
    @State private var businessType = ""
    @State private var neighborhood = ""
    @State private var aspect = "3:1"
    @State private var stylePreset = ""
    @State private var provider = ""
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Sign content") {
                    TextField("Sign text", text: $text)
                    TextField("Business type", text: $businessType)
                    Picker("Aspect ratio", selection: $aspect) {
                        ForEach(["1:1", "3:1", "2:1", "4:1"], id: \.self) { Text($0) }
                    }
                }
                Section("Context (optional)") {
                    TextField("Neighborhood", text: $neighborhood)
                    TextField("Style preset", text: $stylePreset)
                    TextField("Provider", text: $provider)
                }
            }
            .navigationTitle("Generate AI Sign")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Generate") {
                        onSubmit(SignRequest(
                            businessType: businessType,
                            neighborhood: neighborhood,
                            text: text,
                            aspectRatio: aspect,
                            stylePreset: stylePreset,
                            provider: provider
                        ))
                    }
                    .disabled(text.isEmpty)
                }
            }
        }
    }
}

// MARK: - TextToolSheet

/// The Text tool's form: a string + colour that the canvas renders to a transparent PNG.
private struct TextToolSheet: View {
    var onSubmit: (String, Color) -> Void
    @State private var text = ""
    @State private var color: Color = .black
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                TextField("Text", text: $text)
                ColorPicker("Color", selection: $color, supportsOpacity: false)
            }
            .navigationTitle("Add Text")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add") { onSubmit(text, color) }
                        .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }
}

// MARK: - ImagePickerView

/// Photo-library picker for the Image tool; returns PNG data ready for POST /signs/upload.
private struct ImagePickerView: UIViewControllerRepresentable {
    var onPick: (Data) -> Void
    @Environment(\.dismiss) private var dismiss

    func makeUIViewController(context: Context) -> PHPickerViewController {
        var config = PHPickerConfiguration()
        config.filter = .images
        config.selectionLimit = 1
        let picker = PHPickerViewController(configuration: config)
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ vc: PHPickerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, PHPickerViewControllerDelegate {
        let parent: ImagePickerView
        init(_ parent: ImagePickerView) { self.parent = parent }

        func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
            parent.dismiss()
            guard let provider = results.first?.itemProvider,
                  provider.canLoadObject(ofClass: UIImage.self) else { return }
            provider.loadObject(ofClass: UIImage.self) { obj, _ in
                // PNG, not JPEG: /signs/upload accepts only PNG so transparency survives.
                guard let image = obj as? UIImage,
                      let data = image.pngData() else { return }
                DispatchQueue.main.async { self.parent.onPick(data) }
            }
        }
    }
}
#endif

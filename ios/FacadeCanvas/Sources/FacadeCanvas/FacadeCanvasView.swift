#if canImport(SwiftUI) && canImport(PencilKit)
import SwiftUI
import PencilKit
import PhotosUI

/// The facade canvas authoring surface (#282, mode inside the #276 client): a Pencil paint layer
/// plus draggable placed images / AI signs over the unit square of one building facade, saved to
/// the server. Never touches AI or Unity directly — the view model's `ServerClient` is the sole egress.
public struct FacadeCanvasView: View {
    @StateObject private var vm: FacadeCanvasViewModel
    @State private var drawing = PKDrawing()
    @State private var canvasSize: CGSize = .zero
    @State private var showSignSheet = false
    @State private var showPaletteSheet = false
    @State private var showLayerPanel = false
    @State private var showBackdropPicker = false

    public init(viewModel: FacadeCanvasViewModel) {
        _vm = StateObject(wrappedValue: viewModel)
    }

    public var body: some View {
        VStack(spacing: 0) {
            header
            HStack(spacing: 0) {
                canvas
                if showLayerPanel {
                    layerPanel
                        .frame(width: 200)
                        .transition(.move(edge: .trailing))
                }
            }
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
        .sheet(isPresented: $showPaletteSheet) {
            PaletteAuthorSheet { palette in
                Task { _ = await vm.savePalette(palette) }
                showPaletteSheet = false
            }
        }
        .sheet(isPresented: $showBackdropPicker) {
            BackdropPickerView { data in
                Task { await vm.uploadBackdrop(data) }
            }
        }
        .task {
            await vm.load()
            await vm.loadBackdrop()
        }
        .animation(.easeInOut(duration: 0.2), value: showLayerPanel)
    }

    private var header: some View {
        HStack {
            Text("Building \(vm.osmId)").font(.headline)
            Picker("Facade", selection: $vm.facade) {
                ForEach(["Front", "Back", "Left", "Right", "Street"], id: \.self) { Text($0) }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 360)
            Spacer()
            Button { showBackdropPicker = true } label: {
                Label("Backdrop", systemImage: "photo.badge.plus")
            }
            Button("AI Sign") { showSignSheet = true }
            Button("Palette") { showPaletteSheet = true }
            Toggle(isOn: $showLayerPanel) {
                Label("Layers", systemImage: "square.3.layers.3d")
            }
            .toggleStyle(.button)
            Button("Save") { save() }
                .buttonStyle(.borderedProminent)
                .disabled(vm.status == .saving)
        }
        .padding()
    }

    private var canvas: some View {
        GeometryReader { geo in
            ZStack {
                // Static wall colour (Unity's vertex-coloured wall shows through the alpha channel
                // in the real export; this is the authoring stand-in).
                Rectangle().fill(Color(white: 0.9))
                // Facade reference render for tracing over (gap G2 preview, fetched from server).
                // The VM stores raw Data; convert here where UIKit is guaranteed available.
                if let data = vm.backdropData, let uiImage = UIImage(data: data) {
                    Image(uiImage: uiImage)
                        .resizable()
                        .scaledToFill()
                        .opacity(0.35)
                        .clipped()
                }
                PencilCanvas(drawing: $drawing)
                // Image layers rendered in z-order (array order = ascending z).
                ForEach($vm.imageLayers) { $img in
                    PlacedImageView(image: $img, canvasSize: geo.size)
                }
            }
            .onAppear { canvasSize = geo.size }
            .onChange(of: geo.size) { canvasSize = $0 }
        }
        .aspectRatio(1, contentMode: .fit)   // the facade unit square
        .border(Color.secondary)
        .padding()
    }

    /// Side panel for reordering image layers by drag.
    private var layerPanel: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Layers")
                .font(.caption.bold())
                .foregroundColor(.secondary)
                .padding(.horizontal, 8)
                .padding(.top, 8)
            List {
                // Paint layer is always at the bottom; shown as a non-movable separator.
                if !vm.paintStrokes.isEmpty {
                    Label("Paint", systemImage: "paintbrush")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .listRowBackground(Color.clear)
                }
                ForEach(vm.imageLayers) { img in
                    Label(img.signAsset.isEmpty ? "Image" : img.signAsset,
                          systemImage: "photo")
                        .font(.caption)
                        .lineLimit(1)
                }
                .onMove { vm.moveImageLayer(from: $0, to: $1) }
                .onDelete { vm.imageLayers.remove(atOffsets: $0) }
            }
            .listStyle(.plain)
            .environment(\.editMode, .constant(.active))
        }
        .background(Color(uiColor: .secondarySystemBackground))
        .border(Color(uiColor: .separator), width: 0.5)
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
}

// MARK: - PencilCanvas

/// UIKit bridge for PencilKit's canvas + system tool picker.
private struct PencilCanvas: UIViewRepresentable {
    @Binding var drawing: PKDrawing

    func makeUIView(context: Context) -> PKCanvasView {
        let view = PKCanvasView()
        view.drawingPolicy = .anyInput
        view.backgroundColor = .clear
        view.isOpaque = false
        view.delegate = context.coordinator
        view.drawing = drawing
        return view
    }

    func updateUIView(_ view: PKCanvasView, context: Context) {
        if view.drawing != drawing { view.drawing = drawing }
        // Attach the system tool picker once the view is actually in the window hierarchy —
        // becomeFirstResponder / picker attach fail in makeUIView (view.window is still nil).
        if !context.coordinator.pickerAttached, view.window != nil {
            context.coordinator.pickerAttached = true
            let picker = context.coordinator.toolPicker
            picker.setVisible(true, forFirstResponder: view)
            picker.addObserver(view)
            view.becomeFirstResponder()
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, PKCanvasViewDelegate {
        let parent: PencilCanvas
        let toolPicker = PKToolPicker()          // iOS 14+; no window needed
        var pickerAttached = false
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

    var body: some View {
        let w = CGFloat(image.rect[2] - image.rect[0]) * canvasSize.width
        let h = CGFloat(image.rect[3] - image.rect[1]) * canvasSize.height
        // Rect is bottom-origin facade UV; convert the centre to top-down screen space.
        let cx = CGFloat((image.rect[0] + image.rect[2]) / 2) * canvasSize.width
        let cy = (1 - CGFloat((image.rect[1] + image.rect[3]) / 2)) * canvasSize.height

        return RoundedRectangle(cornerRadius: 4)
            .strokeBorder(Color.blue, lineWidth: 2)
            .background(Color.blue.opacity(0.08))
            .overlay(Text(image.signAsset.isEmpty ? "image" : image.signAsset)
                        .font(.caption2).lineLimit(1).padding(2))
            .frame(width: max(w, 24), height: max(h, 24))
            .position(x: cx, y: cy)
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

// MARK: - PaletteAuthorSheet

/// Palette authoring form — builds a named PaletteEntry list and POSTs to /palettes.
private struct PaletteAuthorSheet: View {
    var onSubmit: (Palette) -> Void
    @State private var name = ""
    @State private var entries: [PaletteEntry] = [
        PaletteEntry(role: "wall",   color: "#C8B89A"),
        PaletteEntry(role: "trim",   color: "#FFFFFF"),
        PaletteEntry(role: "window", color: "#6AADE4"),
    ]
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Palette name") {
                    TextField("Name", text: $name)
                }
                Section("Material roles") {
                    // Use index-based ForEach to avoid requiring PaletteEntry: Identifiable.
                    ForEach(entries.indices, id: \.self) { i in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                TextField("Role", text: $entries[i].role)
                                    .frame(maxWidth: 90)
                                TextField("Color (#RRGGBB)", text: $entries[i].color)
                                    .font(.system(.body, design: .monospaced))
                            }
                            HStack {
                                Text("M").font(.caption).foregroundColor(.secondary)
                                Stepper(value: $entries[i].metallic, in: 0...1, step: 0.1) {
                                    Text(String(format: "%.1f", entries[i].metallic))
                                        .font(.caption)
                                }
                                Spacer()
                                Text("R").font(.caption).foregroundColor(.secondary)
                                Stepper(value: $entries[i].roughness, in: 0...1, step: 0.1) {
                                    Text(String(format: "%.1f", entries[i].roughness))
                                        .font(.caption)
                                }
                            }
                        }
                        .swipeActions { Button("Delete", role: .destructive) { entries.remove(at: i) } }
                    }
                    Button("Add role") {
                        entries.append(PaletteEntry())
                    }
                }
            }
            .navigationTitle("New Palette")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") {
                        onSubmit(Palette(name: name, entries: entries))
                    }
                    .disabled(name.isEmpty || entries.isEmpty)
                }
            }
        }
    }
}

// MARK: - BackdropPickerView

private struct BackdropPickerView: UIViewControllerRepresentable {
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
        let parent: BackdropPickerView
        init(_ parent: BackdropPickerView) { self.parent = parent }

        func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
            parent.dismiss()
            guard let provider = results.first?.itemProvider,
                  provider.canLoadObject(ofClass: UIImage.self) else { return }
            provider.loadObject(ofClass: UIImage.self) { obj, _ in
                guard let image = obj as? UIImage,
                      let data = image.jpegData(compressionQuality: 0.9) else { return }
                DispatchQueue.main.async { self.parent.onPick(data) }
            }
        }
    }
}
#endif

#if canImport(SwiftUI) && canImport(PencilKit)
import SwiftUI
import PencilKit

/// The facade canvas authoring surface (#282, mode inside the #276 client): a Pencil paint layer
/// plus draggable placed images / AI signs over the unit square of one building facade, saved to
/// the server. Never touches AI or Unity directly — the view model's `ServerClient` is the sole egress.
public struct FacadeCanvasView: View {
    @StateObject private var vm: FacadeCanvasViewModel
    @State private var drawing = PKDrawing()
    @State private var canvasSize: CGSize = .zero
    @State private var showSignSheet = false

    public init(viewModel: FacadeCanvasViewModel) {
        _vm = StateObject(wrappedValue: viewModel)
    }

    public var body: some View {
        VStack(spacing: 0) {
            header
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
        .task { await vm.load() }
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
            Button("AI Sign") { showSignSheet = true }
            Button("Save") { save() }
                .buttonStyle(.borderedProminent)
                .disabled(vm.status == .saving)
        }
        .padding()
    }

    private var canvas: some View {
        GeometryReader { geo in
            ZStack {
                // The wall backdrop (in Unity the alpha shows the real vertex-coloured wall).
                Rectangle().fill(Color(white: 0.9))
                PencilCanvas(drawing: $drawing)
                // Placed images / signs as draggable normalized rects.
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

    private var statusBar: some View {
        HStack {
            switch vm.status {
            case .idle:            Text("Ready").foregroundColor(.secondary)
            case .loading:         ProgressView(); Text("Loading…")
            case .saving:          ProgressView(); Text("Saving…")
            case .saved:           Label("Saved", systemImage: "checkmark.circle").foregroundColor(.green)
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

/// A placed image/sign rendered as a movable normalized rect. (Fetching the actual PNG from the
/// server for preview needs an asset GET endpoint — design #276 gap G2 — so the MVP shows a label.)
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

/// A minimal AI-sign request form; the server generates the PNG (the iPad never calls a provider).
private struct SignRequestSheet: View {
    var onSubmit: (SignRequest) -> Void
    @State private var text = ""
    @State private var businessType = ""
    @State private var aspect = "3:1"
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                TextField("Sign text", text: $text)
                TextField("Business type", text: $businessType)
                Picker("Aspect", selection: $aspect) {
                    ForEach(["1:1", "3:1", "2:1", "4:1"], id: \.self) { Text($0) }
                }
            }
            .navigationTitle("Generate AI Sign")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Generate") {
                        onSubmit(SignRequest(businessType: businessType, text: text, aspectRatio: aspect))
                    }
                    .disabled(text.isEmpty)
                }
            }
        }
    }
}
#endif

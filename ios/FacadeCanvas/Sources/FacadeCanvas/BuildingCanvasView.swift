#if canImport(SwiftUI) && canImport(PencilKit)
import SwiftUI
import PencilKit

/// The Building Customization Canvas (#367): the full-screen editor the Buildings tab's
/// Customize button opens. Five regions per the UX spec (canvas-design-doc.txt): top toolbar,
/// left layers panel, center facade canvas (architectural elevation, zoom/pan), right
/// properties panel, bottom asset library — plus a view-only 3D preview card.
///
/// Decorates ONE building's facades with signs/text/images/paint; never touches geometry or
/// the procedural template system. All state + persistence live in FacadeCanvasViewModel;
/// the server is reached only through its ServerClient.
@available(iOS 17, *)
public struct BuildingCanvasView: View {
    private let building: BuildingFacts
    private let client: ServerClient
    private let onClose: () -> Void

    @StateObject private var vm: FacadeCanvasViewModel

    // Paint drawings are view-owned (the VM stays PencilKit-free), one per facade.
    @State private var drawing = PKDrawing()
    @State private var facadeDrawings: [String: PKDrawing] = [:]
    // Facades the user actually inked this session (#365's didDrawThisSession, per facade):
    // only then may syncStrokes overwrite server strokes — including erasing to empty.
    @State private var inkedFacades: Set<String> = []

    @State private var tool: EditorTool = .select
    @State private var brushColor: Color = .black
    @State private var zoom: CGFloat = 1
    @State private var pan: CGSize = .zero
    @State private var gestureZoom: CGFloat = 1        // live pinch factor
    @State private var gesturePan: CGSize = .zero      // live hand-drag delta
    @State private var canvasLogicalSize: CGSize = .zero

    @State private var showSignSheet = false
    @State private var showImagePicker = false
    @State private var showTextSheet = false
    @State private var show3DPreview = false
    @State private var showBackConfirm = false
    @State private var renameTarget: UUID?
    @State private var renameText = ""

    enum EditorTool { case select, hand, brush, erase }

    public init(building: BuildingFacts, client: ServerClient,
                draftStore: DraftStore? = nil, onClose: @escaping () -> Void) {
        self.building = building
        self.client = client
        self.onClose = onClose
        _vm = StateObject(wrappedValue: FacadeCanvasViewModel(
            osmId: building.osm_id,
            facade: building.primaryFacadeName,
            footprintHash: building.footprint_hash,
            client: client,
            draftStore: draftStore))
    }

    public var body: some View {
        VStack(spacing: 0) {
            topToolbar
            Divider()
            HStack(spacing: 0) {
                LayersPanel(vm: vm, client: client,
                            onRename: { id, current in renameTarget = id; renameText = current })
                    .frame(width: 232)
                Divider()
                centerColumn
                Divider()
                rightColumn
                    .frame(width: 292)
            }
        }
        .background(MVPStyle.pageBackground)
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
            TextToolSheet { text, color, font in
                showTextSheet = false
                guard let png = FacadeCanvasView.renderTextPNG(text, color: UIColor(color),
                                                               font: font) else { return }
                Task { await placeUploadedImage(png, name: text) }
            }
        }
        .sheet(isPresented: $show3DPreview) {
            Building3DPreviewSheet(building: building, client: client)
        }
        .confirmationDialog("Unsaved changes", isPresented: $showBackConfirm,
                            titleVisibility: .visible) {
            Button("Save") { Task { syncStrokes(); await vm.saveAll(); onClose() } }
            Button("Discard", role: .destructive) { onClose() }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This building has unsaved customizations.")
        }
        .alert("Rename Layer", isPresented: Binding(
            get: { renameTarget != nil },
            set: { if !$0 { renameTarget = nil } })) {
            TextField("Name", text: $renameText)
            Button("Rename") {
                if let id = renameTarget { vm.renameLayer(id: id, to: renameText) }
                renameTarget = nil
            }
            Button("Cancel", role: .cancel) { renameTarget = nil }
        }
        .task {
            await vm.load()
            await vm.loadBackdrop()
            await vm.loadAssets()
        }
    }

    // MARK: - Top toolbar

    private var topToolbar: some View {
        HStack(spacing: 12) {
            Button {
                if hasAnyUnsaved { showBackConfirm = true } else { onClose() }
            } label: {
                Label("Back", systemImage: "chevron.left")
            }
            .buttonStyle(.bordered)

            VStack(alignment: .leading, spacing: 1) {
                Text("Customize Building").font(.headline)
                Text("\(building.displayAddress) · \(building.neighborhood.isEmpty ? "Unknown district" : building.neighborhood)")
                    .font(.caption).foregroundColor(.secondary)
            }

            Divider().frame(height: 32)

            // Tool selection: Select and Brush/Erase are modes; Text / Image / AI Sign are
            // one-shot placement actions (spec: "Adds editable text layers", "Import image").
            modeButton("Select", systemImage: "cursorarrow", mode: .select)
            modeButton("Brush", systemImage: "paintbrush.pointed", mode: .brush)
            Button { showTextSheet = true } label: { Label("Text", systemImage: "textformat") }
                .buttonStyle(.bordered)
            Button { showImagePicker = true } label: { Label("Image", systemImage: "photo") }
                .buttonStyle(.bordered)
            Button { showSignSheet = true } label: { Label("AI Sign", systemImage: "sparkles") }
                .buttonStyle(.bordered)
            modeButton("Erase", systemImage: "eraser", mode: .erase)
            if tool == .brush {
                ColorPicker("Brush color", selection: $brushColor, supportsOpacity: false)
                    .labelsHidden()
            }

            Divider().frame(height: 32)

            Button { vm.undo() } label: { Image(systemName: "arrow.uturn.backward") }
                .buttonStyle(.bordered)
                .disabled(!vm.canUndo)
            Button { vm.redo() } label: { Image(systemName: "arrow.uturn.forward") }
                .buttonStyle(.bordered)
                .disabled(!vm.canRedo)

            Spacer()

            statusIndicator

            Button { fitToFace() } label: {
                Label("Fit to Face", systemImage: "arrow.up.left.and.arrow.down.right")
            }
            .buttonStyle(.bordered)
            Button { show3DPreview = true } label: {
                Label("3D Preview", systemImage: "cube")
            }
            .buttonStyle(.bordered)
            Button {
                Task { syncStrokes(); await vm.saveAll() }
            } label: {
                Label("Save", systemImage: "checkmark.square")
            }
            .buttonStyle(.borderedProminent)
            .disabled(vm.status == .saving)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color(.systemBackground))
    }

    private func modeButton(_ title: String, systemImage: String, mode: EditorTool) -> some View {
        Button { tool = mode } label: { Label(title, systemImage: systemImage) }
            .buttonStyle(.bordered)
            .tint(tool == mode ? .blue : nil)
    }

    @ViewBuilder
    private var statusIndicator: some View {
        switch vm.status {
        case .idle:            EmptyView()
        case .loading:         ProgressView()
        case .saving:          HStack(spacing: 4) { ProgressView(); Text("Saving…").font(.caption) }
        case .saved:           Label("Saved", systemImage: "checkmark.circle")
                                   .font(.caption).foregroundColor(.green)
        case .queued:          Label("Queued", systemImage: "arrow.triangle.2.circlepath")
                                   .font(.caption).foregroundColor(.orange)
        case .failed(let msg): Label(msg, systemImage: "exclamationmark.triangle")
                                   .font(.caption).foregroundColor(.red).lineLimit(1)
        }
    }

    // MARK: - Center column: facade bar + canvas + asset library

    private var centerColumn: some View {
        VStack(spacing: 0) {
            facadeBar
            canvasArea
            Divider()
            AssetLibraryBar(vm: vm, client: client, onAddImage: { showImagePicker = true })
                .frame(height: 168)
        }
        .frame(maxWidth: .infinity)
    }

    /// Facade names this building can address. Server keys stay canonical ("Back");
    /// the UI shows the spec's label ("Rear").
    private var facadeNames: [String] {
        building.street_facades.isEmpty
            ? ["Front", "Back", "Left", "Right"]
            : ["Street", "Front", "Back", "Left", "Right"]
    }

    private func facadeLabel(_ name: String) -> String { name == "Back" ? "Rear" : name }

    private var facadeBar: some View {
        HStack(spacing: 8) {
            ForEach(facadeNames, id: \.self) { name in
                Button(facadeLabel(name)) { switchFacade(to: name) }
                    .buttonStyle(.bordered)
                    .tint(vm.facade == name ? .blue : nil)
            }
            Spacer()
            Menu {
                ForEach([50, 100, 150, 250, 400], id: \.self) { pct in
                    Button("\(pct)%") { zoom = CGFloat(pct) / 100; pan = .zero }
                }
            } label: {
                Text("\(Int(zoom * 100))%").monospacedDigit()
            }
            .buttonStyle(.bordered)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var canvasArea: some View {
        GeometryReader { geo in
            let fitted = fittedSize(in: geo.size)
            ZStack {
                Color(.systemGray5)
                facadeSurface(size: fitted)
                    .frame(width: fitted.width, height: fitted.height)
                    .scaleEffect(zoom * gestureZoom)
                    .offset(x: pan.width + gesturePan.width,
                            y: pan.height + gesturePan.height)
                zoomRail
            }
            .contentShape(Rectangle())
            .clipped()
            .gesture(handPanGesture, including: tool == .hand ? .all : .subviews)
            .simultaneousGesture(magnifyGesture)
            .onTapGesture(count: 2) { fitToFace() }
            .onAppear { canvasLogicalSize = fitted }
            .onChange(of: fitted) { _, new in canvasLogicalSize = new }
        }
    }

    /// The facade fitted inside the viewport at zoom 1 (architectural elevation: the aspect
    /// ratio comes from the building's real facade dimensions).
    private func fittedSize(in available: CGSize) -> CGSize {
        let aspect = facadeWidthM / max(facadeHeightM, 0.1)
        let margin: CGFloat = 48
        let w = max(available.width - margin, 50)
        let h = max(available.height - margin, 50)
        if w / h > aspect { return CGSize(width: h * aspect, height: h) }
        return CGSize(width: w, height: w / aspect)
    }

    private func facadeSurface(size: CGSize) -> some View {
        ZStack {
            Rectangle().fill(Color(white: 0.92))
            if let data = vm.backdropData, let uiImage = UIImage(data: data) {
                Image(uiImage: uiImage)
                    .resizable().scaledToFill()
                    .opacity(0.35)
                    .clipped()
            }
            PencilCanvas(
                drawing: $drawing,
                tool: pkTool,
                interactionEnabled: tool == .brush || tool == .erase,
                onCanvasReady: { cv in
                    vm.paintUndo = { [weak cv] in cv?.undoManager?.undo() }
                    vm.paintRedo = { [weak cv] in cv?.undoManager?.redo() }
                },
                onDrawingChanged: {
                    vm.notePaintAction()
                    inkedFacades.insert(vm.facade)
                }
            )
            .opacity(vm.paintVisible ? vm.paintOpacity : 0)
            ForEach(vm.imageLayers.filter(\.visible)) { img in
                CanvasImageLayerView(
                    vm: vm, layer: img, canvasSize: size, client: client,
                    isSelected: vm.selectedLayerId == img.id,
                    selectionEnabled: tool == .select,
                    eraseActive: tool == .erase,
                    zoom: zoom * gestureZoom
                )
            }
        }
        .coordinateSpace(name: "facade")   // layer move/resize gestures resolve here (unscaled)
        .border(Color.secondary.opacity(0.6))
        // Double-tap BEFORE single-tap so the spec's double-tap zoom reset wins over the
        // deselect tap when both land on the facade surface.
        .onTapGesture(count: 2) { fitToFace() }
        .onTapGesture {
            if tool == .select { vm.selectedLayerId = nil }
        }
    }

    /// Floating zoom controls on the canvas's left edge (mock parity): hand tool, +, −, fit.
    private var zoomRail: some View {
        VStack(spacing: 8) {
            Button { tool = tool == .hand ? .select : .hand } label: {
                Image(systemName: "hand.raised")
            }
            .buttonStyle(.borderedProminent)
            .tint(tool == .hand ? .blue : Color(.systemBackground))
            .foregroundColor(tool == .hand ? .white : .primary)
            Button { zoom = min(zoom * 1.25, 8) } label: { Image(systemName: "plus") }
                .buttonStyle(.bordered).background(Color(.systemBackground)).cornerRadius(6)
            Button { zoom = max(zoom / 1.25, 0.25) } label: { Image(systemName: "minus") }
                .buttonStyle(.bordered).background(Color(.systemBackground)).cornerRadius(6)
            Button { fitToFace() } label: {
                Image(systemName: "arrow.down.right.and.arrow.up.left")
            }
            .buttonStyle(.bordered).background(Color(.systemBackground)).cornerRadius(6)
        }
        .padding(8)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private var magnifyGesture: some Gesture {
        MagnificationGesture()
            .onChanged { gestureZoom = $0 }
            .onEnded { value in
                zoom = min(max(zoom * value, 0.25), 8)
                gestureZoom = 1
            }
    }

    private var handPanGesture: some Gesture {
        DragGesture()
            .onChanged { g in if tool == .hand { gesturePan = g.translation } }
            .onEnded { g in
                if tool == .hand {
                    pan.width += g.translation.width
                    pan.height += g.translation.height
                }
                gesturePan = .zero
            }
    }

    private func fitToFace() {
        withAnimation(.easeOut(duration: 0.2)) { zoom = 1; pan = .zero }
    }

    // MARK: - Right column: properties + 3D preview card

    private var rightColumn: some View {
        VStack(spacing: 0) {
            PropertiesPanel(vm: vm, client: client,
                            facadeWidthM: facadeWidthM, facadeHeightM: facadeHeightM)
            Divider()
            ThreeDPreviewCard(building: building, client: client) { show3DPreview = true }
                .frame(height: 200)
        }
        .background(Color(.systemBackground))
    }

    /// Physical facade dimensions for the properties panel's meter readouts. Street facades
    /// use the ranked edge's real length; cardinal faces use the footprint box.
    private var facadeWidthM: Double {
        switch vm.facade {
        case "Street":
            if let e = building.street_facades.first?.edge, e.count == 4 {
                let dx = e[2] - e[0], dz = e[3] - e[1]
                let len = (dx * dx + dz * dz).squareRoot()
                if len > 0.5 { return len }
            }
            return max(building.width_m, 1)
        case "Left", "Right":
            return max(building.depth_m, 1)
        default:
            return max(building.width_m, 1)
        }
    }

    private var facadeHeightM: Double {
        let h = building.facade_height_m > 0 ? building.facade_height_m : building.height_m
        return max(h, 1)
    }

    // MARK: - Actions

    private var pkTool: PKTool {
        switch tool {
        case .brush: return PKInkingTool(.pen, color: UIColor(brushColor), width: 8)
        case .erase: return PKEraserTool(.bitmap)
        default:     return PKInkingTool(.pen, color: UIColor(brushColor), width: 8)
        }
    }

    private var hasAnyUnsaved: Bool {
        vm.hasUnsavedChanges
    }

    /// Convert the on-screen drawing to normalized strokes before any save/stash. Server
    /// strokes not yet re-hydrated into the PKDrawing are preserved unless the user inked
    /// this facade this session — including erasing to empty, which must persist (#365 rule).
    private func syncStrokes() {
        guard canvasLogicalSize != .zero else { return }
        let converted = StrokeConversion.strokes(from: drawing, canvasSize: canvasLogicalSize)
        if inkedFacades.contains(vm.facade) || vm.paintStrokes.isEmpty {
            vm.paintStrokes = converted
        }
    }

    private func switchFacade(to name: String) {
        guard name != vm.facade else { return }
        syncStrokes()
        facadeDrawings[vm.facade] = drawing
        Task {
            await vm.switchFacade(to: name)
            drawing = facadeDrawings[name] ?? PKDrawing()
            await vm.loadBackdrop()
        }
    }

    private func placeUploadedImage(_ png: Data, name: String) async {
        if let sign = await vm.uploadImageAsset(name: name, png: png) {
            vm.placeSign(sign)
        }
    }

    private func shortId() -> String {
        String(UUID().uuidString.prefix(8)).lowercased()
    }
}

// MARK: - Placed image layer (selectable, movable, resizable)

/// One placed image on the facade: renders its PNG (thumb fetched once), and under the Select
/// tool carries the full selection system — bounding box, corner resize handles, duplicate /
/// delete action menu. Rects are bottom-origin facade UV, converted to top-down screen space.
@available(iOS 17, *)
private struct CanvasImageLayerView: View {
    @ObservedObject var vm: FacadeCanvasViewModel
    let layer: FacadeCanvasViewModel.PlacedImage
    let canvasSize: CGSize
    let client: ServerClient
    let isSelected: Bool
    let selectionEnabled: Bool
    let eraseActive: Bool
    let zoom: CGFloat

    @State private var thumb: UIImage?
    @State private var didAttemptLoad = false
    @State private var dragRecorded = false

    var body: some View {
        let w = max(CGFloat(layer.rect[2] - layer.rect[0]) * canvasSize.width, 16)
        let h = max(CGFloat(layer.rect[3] - layer.rect[1]) * canvasSize.height, 16)
        let cx = CGFloat((layer.rect[0] + layer.rect[2]) / 2) * canvasSize.width
        let cy = (1 - CGFloat((layer.rect[1] + layer.rect[3]) / 2)) * canvasSize.height

        content(width: w, height: h)
            .opacity(layer.opacity)
            .scaleEffect(x: layer.flipH ? -1 : 1, y: layer.flipV ? -1 : 1)
            .rotationEffect(.degrees(layer.rotationDeg))
            .overlay { if isSelected { selectionChrome(width: w, height: h) } }
            .frame(width: w, height: h)
            .position(x: cx, y: cy)
            .onTapGesture {
                if eraseActive { vm.deleteLayer(id: layer.id) }
                else if selectionEnabled && !layer.locked { vm.selectedLayerId = layer.id }
            }
            .gesture(moveGesture, including: selectionEnabled && !layer.locked ? .all : .none)
            .task {
                guard !didAttemptLoad, !layer.signAsset.isEmpty else { return }
                didAttemptLoad = true
                if let data = try? await client.fetchSignThumb(signId: layer.signAsset) {
                    thumb = UIImage(data: data)
                }
            }
    }

    @ViewBuilder
    private func content(width: CGFloat, height: CGFloat) -> some View {
        if let thumb {
            Image(uiImage: thumb)
                .resizable()
                .frame(width: width, height: height)
        } else {
            RoundedRectangle(cornerRadius: 4)
                .strokeBorder(eraseActive ? Color.red : Color.blue.opacity(0.7), lineWidth: 1.5)
                .background(Color.blue.opacity(0.08))
                .overlay(Text(layer.displayName).font(.caption2).lineLimit(1).padding(2))
                .frame(width: width, height: height)
        }
    }

    /// Bounding box + resize handles + the duplicate/delete action menu above the box.
    private func selectionChrome(width: CGFloat, height: CGFloat) -> some View {
        ZStack {
            Rectangle()
                .strokeBorder(Color.blue, lineWidth: 1.5 / zoom)
            ForEach(Corner.allCases, id: \.self) { corner in
                Circle()
                    .fill(Color.white)
                    .overlay(Circle().strokeBorder(Color.blue, lineWidth: 1.5))
                    .frame(width: 14 / zoom, height: 14 / zoom)
                    .position(corner.point(in: CGSize(width: width, height: height)))
                    .gesture(resizeGesture(corner))
            }
            HStack(spacing: 14) {
                Button { vm.duplicateLayer(id: layer.id) } label: {
                    Image(systemName: "square.on.square")
                }
                Button(role: .destructive) { vm.deleteLayer(id: layer.id) } label: {
                    Image(systemName: "trash")
                }
            }
            .font(.system(size: 13))
            .padding(.horizontal, 10).padding(.vertical, 6)
            .background(Color(.systemBackground), in: RoundedRectangle(cornerRadius: 8))
            .shadow(radius: 2)
            .scaleEffect(1 / zoom)
            .position(x: width / 2, y: -28 / zoom)
        }
    }

    private enum Corner: CaseIterable {
        case topLeft, topRight, bottomLeft, bottomRight
        func point(in size: CGSize) -> CGPoint {
            switch self {
            case .topLeft:     return .zero
            case .topRight:    return CGPoint(x: size.width, y: 0)
            case .bottomLeft:  return CGPoint(x: 0, y: size.height)
            case .bottomRight: return CGPoint(x: size.width, y: size.height)
            }
        }
    }

    private var moveGesture: some Gesture {
        DragGesture(coordinateSpace: .named("facade"))
            .onChanged { g in
                if !dragRecorded {
                    vm.selectedLayerId = layer.id
                    vm.beginSelectedEdit()
                    dragRecorded = true
                }
                moveCenter(to: g.location)
            }
            .onEnded { _ in dragRecorded = false }
    }

    private func moveCenter(to point: CGPoint) {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return }
        let halfW = (layer.rect[2] - layer.rect[0]) / 2
        let halfH = (layer.rect[3] - layer.rect[1]) / 2
        let nx = Double(point.x / canvasSize.width)
        let ny = 1 - Double(point.y / canvasSize.height)      // screen → facade bottom-up
        let cx = min(max(nx, halfW), 1 - halfW)
        let cy = min(max(ny, halfH), 1 - halfH)
        vm.updateSelected(transient: true) { img in
            img.rect = [cx - halfW, cy - halfH, cx + halfW, cy + halfH]
        }
    }

    private func resizeGesture(_ corner: Corner) -> some Gesture {
        DragGesture(coordinateSpace: .named("facade"))
            .onChanged { g in
                if !dragRecorded {
                    vm.beginSelectedEdit()
                    dragRecorded = true
                }
                resize(corner, to: g.location)
            }
            .onEnded { _ in dragRecorded = false }
    }

    private func resize(_ corner: Corner, to point: CGPoint) {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return }
        let nx = min(max(Double(point.x / canvasSize.width), 0), 1)
        let ny = min(max(1 - Double(point.y / canvasSize.height), 0), 1)
        let minSize = 0.02
        vm.updateSelected(transient: true) { img in
            var r = img.rect
            switch corner {
            case .topLeft:     r[0] = min(nx, r[2] - minSize); r[3] = max(ny, r[1] + minSize)
            case .topRight:    r[2] = max(nx, r[0] + minSize); r[3] = max(ny, r[1] + minSize)
            case .bottomLeft:  r[0] = min(nx, r[2] - minSize); r[1] = min(ny, r[3] - minSize)
            case .bottomRight: r[2] = max(nx, r[0] + minSize); r[1] = min(ny, r[3] - minSize)
            }
            img.rect = r
        }
    }
}

// MARK: - Layers panel

/// Left panel: every element on the facade, topmost first (Figma convention) — placed images,
/// the freehand paint layer, and the immutable procedural base. Rows toggle visibility/lock,
/// context-menus offer rename/duplicate/delete, drag reorders z, and the bottom holds the
/// selected layer's opacity slider plus the spec's persistent tips.
@available(iOS 17, *)
private struct LayersPanel: View {
    @ObservedObject var vm: FacadeCanvasViewModel
    let client: ServerClient
    let onRename: (UUID, String) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Layers").font(.headline)
                Spacer()
            }
            .padding(.horizontal, 12).padding(.vertical, 10)

            List {
                // Topmost z first: reverse of the ascending storage order.
                ForEach(vm.imageLayers.reversed()) { layer in
                    layerRow(layer)
                        .listRowInsets(EdgeInsets(top: 4, leading: 8, bottom: 4, trailing: 8))
                        .listRowBackground(vm.selectedLayerId == layer.id
                                           ? Color.blue.opacity(0.12) : Color.clear)
                }
                .onMove { source, destination in
                    // List shows reversed order; map back to storage indices.
                    let n = vm.imageLayers.count
                    let src = IndexSet(source.map { n - 1 - $0 })
                    let dst = n - destination
                    vm.moveImageLayer(from: src, to: max(dst, 0))
                }
                paintRow
                    .listRowInsets(EdgeInsets(top: 4, leading: 8, bottom: 4, trailing: 8))
                baseRow
                    .listRowInsets(EdgeInsets(top: 4, leading: 8, bottom: 4, trailing: 8))
            }
            .listStyle(.plain)
            .environment(\.editMode, .constant(.active))   // always drag-reorderable
            .scrollContentBackground(.hidden)

            Divider()
            opacitySection
            tipsSection
        }
        .background(Color(.systemBackground))
    }

    private func layerRow(_ layer: FacadeCanvasViewModel.PlacedImage) -> some View {
        HStack(spacing: 8) {
            LayerThumb(signAsset: layer.signAsset, client: client)
            Text(layer.displayName)
                .font(.caption)
                .lineLimit(1)
            Spacer()
            Button { vm.toggleVisible(id: layer.id) } label: {
                Image(systemName: layer.visible ? "eye" : "eye.slash")
                    .foregroundColor(layer.visible ? .primary : .secondary)
            }
            .buttonStyle(.plain)
            Button { vm.toggleLocked(id: layer.id) } label: {
                Image(systemName: layer.locked ? "lock.fill" : "lock.open")
                    .foregroundColor(layer.locked ? .orange : .secondary)
            }
            .buttonStyle(.plain)
        }
        .contentShape(Rectangle())
        .onTapGesture { vm.selectedLayerId = layer.locked ? nil : layer.id }
        .contextMenu {
            Button("Rename") { onRename(layer.id, layer.name) }
            Button("Duplicate") { vm.duplicateLayer(id: layer.id) }
            Button("Delete", role: .destructive) { vm.deleteLayer(id: layer.id) }
        }
    }

    private var paintRow: some View {
        HStack(spacing: 8) {
            Image(systemName: "paintbrush.pointed.fill")
                .frame(width: 28, height: 28)
                .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 4))
            Text("Paint").font(.caption)
            Spacer()
            Button { vm.setPaintState(visible: !vm.paintVisible) } label: {
                Image(systemName: vm.paintVisible ? "eye" : "eye.slash")
                    .foregroundColor(vm.paintVisible ? .primary : .secondary)
            }
            .buttonStyle(.plain)
            Button { vm.setPaintState(locked: !vm.paintLocked) } label: {
                Image(systemName: vm.paintLocked ? "lock.fill" : "lock.open")
                    .foregroundColor(vm.paintLocked ? .orange : .secondary)
            }
            .buttonStyle(.plain)
        }
    }

    private var baseRow: some View {
        HStack(spacing: 8) {
            Image(systemName: "building.2")
                .frame(width: 28, height: 28)
                .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 4))
            Text("Base Facade").font(.caption).foregroundColor(.secondary)
            Spacer()
            Image(systemName: "lock.fill").foregroundColor(.secondary)
        }
    }

    private var opacitySection: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Layer Opacity").font(.caption)
                Spacer()
                Text("\(Int((vm.selectedLayer?.opacity ?? vm.paintOpacity) * 100))%")
                    .font(.caption).monospacedDigit().foregroundColor(.secondary)
            }
            Slider(value: Binding(
                get: { vm.selectedLayer?.opacity ?? vm.paintOpacity },
                set: { newValue in
                    if vm.selectedLayer != nil {
                        vm.updateSelected(transient: true) { $0.opacity = newValue }
                    } else {
                        vm.setPaintState(opacity: newValue)
                    }
                }), in: 0...1) { editing in
                    if editing, vm.selectedLayer != nil { vm.beginSelectedEdit() }
                }
        }
        .padding(12)
    }

    private var tipsSection: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Tips").font(.caption.bold())
            Group {
                Text("• Pinch to zoom")
                Text("• Hand tool to pan")
                Text("• Double tap to reset zoom")
                Text("• Drag layers to reorder")
            }
            .font(.caption2).foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(Color(.secondarySystemBackground))
    }
}

/// Small square thumbnail for a layer row; same fetch-once pattern as AssetsGridView cards.
@available(iOS 17, *)
private struct LayerThumb: View {
    let signAsset: String
    let client: ServerClient
    @State private var image: UIImage?
    @State private var didAttemptLoad = false

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: "photo").foregroundColor(.secondary)
            }
        }
        .frame(width: 28, height: 28)
        .background(Color(.secondarySystemBackground))
        .clipShape(RoundedRectangle(cornerRadius: 4))
        .task {
            guard !didAttemptLoad, !signAsset.isEmpty else { return }
            didAttemptLoad = true
            if let data = try? await client.fetchSignThumb(signId: signAsset) {
                image = UIImage(data: data)
            }
        }
    }
}

// MARK: - Properties panel

/// Right panel: context-sensitive editor for the selected object. Physical width/height and
/// X/Y offsets are shown in meters, derived from the building's real facade dimensions —
/// the canvas edits normalized UV underneath.
@available(iOS 17, *)
private struct PropertiesPanel: View {
    @ObservedObject var vm: FacadeCanvasViewModel
    let client: ServerClient
    let facadeWidthM: Double
    let facadeHeightM: Double

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Properties").font(.headline)
                if let layer = vm.selectedLayer {
                    selectedEditor(layer)
                } else {
                    emptyState
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Nothing selected")
                .font(.caption).foregroundColor(.secondary)
            Text("Select an object on the facade with the Select tool, or place one from the asset library below.")
                .font(.caption2).foregroundColor(.secondary)
            Divider()
            Text("Facade: \(vm.facade)").font(.caption)
            Text(String(format: "%.1f m × %.1f m", facadeWidthM, facadeHeightM))
                .font(.caption).foregroundColor(.secondary)
        }
    }

    @ViewBuilder
    private func selectedEditor(_ layer: FacadeCanvasViewModel.PlacedImage) -> some View {
        Text(layer.displayName).font(.subheadline.bold())

        LayerThumb(signAsset: layer.signAsset, client: client)
            .frame(maxWidth: .infinity)
            .frame(height: 64)

        let widthM = (layer.rect[2] - layer.rect[0]) * facadeWidthM
        let heightM = (layer.rect[3] - layer.rect[1]) * facadeHeightM
        let xM = (layer.rect[0] + layer.rect[2]) / 2 * facadeWidthM
        let yM = (layer.rect[1] + layer.rect[3]) / 2 * facadeHeightM

        meterSlider("Width", value: widthM, range: 0.2...facadeWidthM) { newW in
            let w = newW / facadeWidthM
            vm.updateSelected(transient: true) { img in
                let cx = (img.rect[0] + img.rect[2]) / 2
                let x0 = min(max(cx - w / 2, 0), 1 - w)
                img.rect = [x0, img.rect[1], x0 + w, img.rect[3]]
            }
        }
        meterSlider("Height", value: heightM, range: 0.2...facadeHeightM) { newH in
            let h = newH / facadeHeightM
            vm.updateSelected(transient: true) { img in
                let cy = (img.rect[1] + img.rect[3]) / 2
                let y0 = min(max(cy - h / 2, 0), 1 - h)
                img.rect = [img.rect[0], y0, img.rect[2], y0 + h]
            }
        }
        meterSlider("X Position", value: xM, range: 0...facadeWidthM) { newX in
            let cx = newX / facadeWidthM
            vm.updateSelected(transient: true) { img in
                let halfW = (img.rect[2] - img.rect[0]) / 2
                let c = min(max(cx, halfW), 1 - halfW)
                img.rect = [c - halfW, img.rect[1], c + halfW, img.rect[3]]
            }
        }
        meterSlider("Y Position", value: yM, range: 0...facadeHeightM) { newY in
            let cy = newY / facadeHeightM
            vm.updateSelected(transient: true) { img in
                let halfH = (img.rect[3] - img.rect[1]) / 2
                let c = min(max(cy, halfH), 1 - halfH)
                img.rect = [img.rect[0], c - halfH, img.rect[2], c + halfH]
            }
        }

        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text("Rotation").font(.caption)
                Spacer()
                Text("\(Int(layer.rotationDeg))°").font(.caption).monospacedDigit()
                    .foregroundColor(.secondary)
            }
            Slider(value: Binding(
                get: { layer.rotationDeg },
                set: { v in vm.updateSelected(transient: true) { $0.rotationDeg = v } }),
                in: -180...180, step: 1) { editing in
                    if editing { vm.beginSelectedEdit() }
                }
        }

        HStack(spacing: 8) {
            alignButton("text.alignleft") { vm.alignSelected(.left) }
            alignButton("text.aligncenter") { vm.alignSelected(.center) }
            alignButton("text.alignright") { vm.alignSelected(.right) }
        }

        HStack(spacing: 8) {
            Button { vm.updateSelected { $0.flipH.toggle() } } label: {
                Label("Flip H", systemImage: "arrow.left.and.right.righttriangle.left.righttriangle.right")
            }
            .buttonStyle(.bordered).font(.caption)
            Button { vm.updateSelected { $0.flipV.toggle() } } label: {
                Label("Flip V", systemImage: "arrow.up.and.down.righttriangle.up.righttriangle.down")
            }
            .buttonStyle(.bordered).font(.caption)
        }

        Divider()

        Button(role: .destructive) {
            vm.deleteLayer(id: layer.id)
        } label: {
            Label("Delete", systemImage: "trash")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
    }

    private func meterSlider(_ title: String, value: Double, range: ClosedRange<Double>,
                             set: @escaping (Double) -> Void) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(title).font(.caption)
                Spacer()
                Text(String(format: "%.2f m", value)).font(.caption).monospacedDigit()
                    .foregroundColor(.secondary)
            }
            Slider(value: Binding(get: { value }, set: set), in: range) { editing in
                if editing { vm.beginSelectedEdit() }
            }
        }
    }

    private func alignButton(_ systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage).frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
    }
}

// MARK: - Asset library

/// Bottom bar: categorized, horizontally scrolling asset cards. Every placeable asset is a
/// stored sign PNG (GET /signs) — "Signs" are AI-generated, "Uploads" are user images/text.
/// Tapping a card places it centred on the facade; richer categories (awnings, planters…)
/// arrive once those asset kinds exist server-side.
@available(iOS 17, *)
private struct AssetLibraryBar: View {
    @ObservedObject var vm: FacadeCanvasViewModel
    let client: ServerClient
    let onAddImage: () -> Void

    @State private var category: Category = .all

    enum Category: String, CaseIterable, Identifiable {
        case all = "All", signs = "Signs", uploads = "Uploads"
        var id: String { rawValue }
    }

    private var filtered: [SignDef] {
        switch category {
        case .all:     return vm.signAssets
        case .signs:   return vm.signAssets.filter { $0.provider != "upload" }
        case .uploads: return vm.signAssets.filter { $0.provider == "upload" }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 16) {
                ForEach(Category.allCases) { cat in
                    Button(cat.rawValue) { category = cat }
                        .font(.caption.bold())
                        .foregroundColor(category == cat ? .blue : .secondary)
                }
                Spacer()
            }
            .padding(.horizontal, 12).padding(.top, 8)

            ScrollView(.horizontal, showsIndicators: true) {
                HStack(spacing: 10) {
                    addImageCard
                    ForEach(filtered, id: \.signId) { sign in
                        AssetCard(sign: sign, client: client) { vm.placeSign(sign) }
                    }
                }
                .padding(.horizontal, 12).padding(.bottom, 8)
            }
        }
        .background(Color(.systemBackground))
    }

    private var addImageCard: some View {
        Button(action: onAddImage) {
            VStack(spacing: 6) {
                Image(systemName: "plus").font(.title3)
                Text("Add Image").font(.caption2)
            }
            .frame(width: 96, height: 96)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }
}

@available(iOS 17, *)
private struct AssetCard: View {
    let sign: SignDef
    let client: ServerClient
    let onPlace: () -> Void

    @State private var thumb: UIImage?
    @State private var didAttemptLoad = false

    var body: some View {
        Button(action: onPlace) {
            VStack(spacing: 4) {
                Group {
                    if let thumb {
                        Image(uiImage: thumb).resizable().scaledToFit()
                    } else {
                        Image(systemName: "photo").foregroundColor(.secondary)
                    }
                }
                .frame(width: 88, height: 64)
                Text(sign.text.isEmpty ? sign.signId : sign.text)
                    .font(.caption2).lineLimit(1)
            }
            .frame(width: 96, height: 96)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .task {
            guard !didAttemptLoad else { return }
            didAttemptLoad = true
            if let data = try? await client.fetchSignThumb(signId: sign.signId) {
                thumb = UIImage(data: data)
            }
        }
    }
}

// MARK: - 3D preview (view-only)

/// Bottom-right card: the building's Unity-rendered thumbnail with current overrides — the
/// "does this look good in game?" check. Never editable, never interactive.
@available(iOS 17, *)
private struct ThreeDPreviewCard: View {
    let building: BuildingFacts
    let client: ServerClient
    let onExpand: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("3D Preview (Face Only)").font(.caption.bold())
                Spacer()
                Button(action: onExpand) { Image(systemName: "cube") }
                    .buttonStyle(.plain)
            }
            BuildingThumbImage(osmId: building.osm_id, client: client)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 8))
        }
        .padding(10)
    }
}

@available(iOS 17, *)
private struct Building3DPreviewSheet: View {
    let building: BuildingFacts
    let client: ServerClient
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            BuildingThumbImage(osmId: building.osm_id, client: client)
                .padding()
                .navigationTitle("3D Preview — \(building.displayAddress)")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
                }
        }
    }
}

/// The building thumbnail Unity uploads after import (PUT /buildings/{id}/thumb); a friendly
/// placeholder until one exists. Renders saved state — unsaved canvas edits appear after the
/// next Save + Unity import round-trip.
@available(iOS 17, *)
private struct BuildingThumbImage: View {
    let osmId: Int
    let client: ServerClient
    @State private var image: UIImage?
    @State private var didAttemptLoad = false

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image).resizable().scaledToFit()
            } else {
                VStack(spacing: 6) {
                    Image(systemName: "cube.transparent").font(.title)
                    Text("No render yet — save and import in Unity to see this building.")
                        .font(.caption2).multilineTextAlignment(.center)
                }
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color(.secondarySystemBackground))
            }
        }
        .task {
            guard !didAttemptLoad else { return }
            didAttemptLoad = true
            if let data = try? await client.fetchBuildingThumb(osmId: osmId) {
                image = UIImage(data: data)
            }
        }
    }
}
#endif

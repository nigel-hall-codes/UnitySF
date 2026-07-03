import Combine
import Foundation
import SwiftUI

/// State + save/load for one building facade's canvas. Deliberately **PencilKit-free**: it holds
/// already-converted `paintStrokes` (the view converts ink → strokes via `StrokeConversion`), so the
/// canvas-assembly logic is unit-testable off-device. The server is the source of truth; this is an
/// optimistic local draft that POSTs to it (design #276 §5 sync model).
@MainActor
public final class FacadeCanvasViewModel: ObservableObject {

    public struct PlacedImage: Identifiable, Equatable {
        public let id: UUID
        public var rect: [Double]        // normalized [x0, y0, x1, y1] on the facade
        public var texture: String       // e.g. Signs/<id>.png
        public var signAsset: String
        // #367 canvas metadata: layers-panel state + object transform.
        public var name: String
        public var visible: Bool
        public var locked: Bool
        public var opacity: Double
        public var rotationDeg: Double
        public var flipH: Bool
        public var flipV: Bool

        public init(id: UUID = UUID(), rect: [Double], texture: String, signAsset: String = "",
                    name: String = "", visible: Bool = true, locked: Bool = false,
                    opacity: Double = 1, rotationDeg: Double = 0,
                    flipH: Bool = false, flipV: Bool = false) {
            self.id = id; self.rect = rect; self.texture = texture; self.signAsset = signAsset
            self.name = name; self.visible = visible; self.locked = locked
            self.opacity = opacity; self.rotationDeg = rotationDeg
            self.flipH = flipH; self.flipV = flipV
        }

        /// Layers-panel display name: the user label, else the sign asset, else a generic tag.
        public var displayName: String {
            if !name.isEmpty { return name }
            if !signAsset.isEmpty { return signAsset }
            return "Image"
        }
    }

    public enum Status: Equatable { case idle, loading, saving, saved, queued, failed(String) }

    public let osmId: Int
    @Published public var facade: String
    public let footprintHash: String

    @Published public var paintStrokes: [Stroke] = []      // set by the view from the PKDrawing
    /// Image layers in ascending z-order (index 0 = lowest above the paint layer).
    @Published public var imageLayers: [PlacedImage] = []
    @Published public private(set) var status: Status = .idle
    /// Selected object on the facade (#367 Select tool); drives the properties panel.
    @Published public var selectedLayerId: UUID?
    /// Paint layer's own layers-panel state (#367): visible/locked apply to the whole
    /// freehand paint layer, mirrored onto the paint CanvasLayer at save.
    @Published public var paintVisible = true
    @Published public var paintLocked = false
    @Published public var paintOpacity: Double = 1
    /// Sign assets for the bottom asset library (#367); loaded via GET /signs.
    @Published public private(set) var signAssets: [SignDef] = []
    /// True once any edit happened since the last successful save/load — the Back
    /// button's unsaved-changes guard reads this.
    @Published public private(set) var isDirty = false
    /// Raw PNG/JPEG bytes of the facade reference render (gap G2 preview, #300).
    /// The view (UIKit-guarded) converts to UIImage; storing Data here keeps the VM platform-neutral.
    @Published public private(set) var backdropData: Data?

    private let client: ServerClient
    private let draftStore: DraftStore?

    public init(osmId: Int, facade: String = "Front", footprintHash: String = "",
                client: ServerClient, draftStore: DraftStore? = nil) {
        self.osmId = osmId; self.facade = facade; self.footprintHash = footprintHash
        self.client = client; self.draftStore = draftStore
    }

    /// Assemble the layered document: paint layer at z=0, then each placed image in their
    /// current array order — matching the server's flatten-on-export contract.
    public func buildCanvas() -> FacadeCanvas {
        var layers: [CanvasLayer] = []
        if !paintStrokes.isEmpty {
            var paint = CanvasLayer.paint(paintStrokes, layer: 0)
            paint.visible = paintVisible; paint.locked = paintLocked; paint.opacity = paintOpacity
            layers.append(paint)
        }
        for (i, img) in imageLayers.enumerated() {
            var layer = CanvasLayer.image(rect: img.rect, texture: img.texture,
                                          signAsset: img.signAsset, layer: i + 1)
            layer.name = img.name; layer.visible = img.visible; layer.locked = img.locked
            layer.opacity = img.opacity; layer.rotation_deg = img.rotationDeg
            layer.flipH = img.flipH; layer.flipV = img.flipV
            layers.append(layer)
        }
        return FacadeCanvas(osm_id: osmId, facade: facade,
                            footprint_hash: footprintHash, layers: layers, version: 1)
    }

    /// Save to the server. On network failure, enqueues the canvas in the draft store outbox
    /// for retry on the next successful connection (D3 pending-write outbox).
    public func save() async {
        status = .saving
        let canvas = buildCanvas()
        do {
            _ = try await client.saveCanvas(canvas)
            await draftStore?.dequeue(osmId: osmId, facade: facade)
            status = .saved
            isDirty = false
        } catch {
            if let store = draftStore {
                await store.enqueue(canvas)
                status = .queued
                isDirty = false     // safe in the outbox; retried on reconnect
            } else {
                status = .failed(String(describing: error))
            }
        }
    }

    /// Load the server's canvas for this facade. Paint strokes are re-hydrated for a future
    /// re-edit; the on-screen PKDrawing reconstruction from strokes is a follow-up, so here we
    /// restore the image layers (sorted by their server-side layer index) and keep the strokes
    /// available on the model.
    public func load() async {
        status = .loading
        do {
            if let canvas = try await client.loadCanvas(osmId: osmId, facade: facade) {
                apply(canvas)
            }
            status = .idle
        } catch {
            status = .failed(String(describing: error))
        }
    }

    /// Upload a photo as the facade reference backdrop, then refresh it in the canvas.
    public func uploadBackdrop(_ data: Data) async {
        do {
            try await client.uploadBackdrop(osmId: osmId, facade: facade, data: data)
            await loadBackdrop()
        } catch {
            status = .failed(String(describing: error))
        }
    }

    /// Fetch the facade reference render for the backdrop. Returns silently if the server
    /// doesn't have one yet (404 → backdropData stays nil).
    public func loadBackdrop() async {
        do {
            backdropData = try await client.fetchBackdrop(osmId: osmId, facade: facade)
        } catch {
            // Non-fatal: backdrop is a drawing aid only; swallow and leave nil.
        }
    }

    public func requestSign(_ request: SignRequest) async -> SignDef? {
        do {
            return try await client.generateSign(request)
        } catch {
            status = .failed(String(describing: error))
            return nil
        }
    }

    /// Store a user-supplied PNG (the Image and Text tools, #365) as a reusable sign asset.
    /// Returns the stored record for `placeSign`, or nil (with status set) on failure.
    public func uploadImageAsset(name: String, png: Data) async -> SignDef? {
        do {
            return try await client.uploadSignImage(name: name, png: png)
        } catch {
            status = .failed(String(describing: error))
            return nil
        }
    }

    /// Place a generated/existing sign as an image layer (a centred default rect the user can move).
    public func placeSign(_ sign: SignDef, rect: [Double] = [0.4, 0.55, 0.6, 0.75]) {
        recordLayerChange()
        let name = sign.text.isEmpty ? sign.signId : sign.text
        let placed = PlacedImage(rect: rect, texture: sign.png, signAsset: sign.signId, name: name)
        imageLayers.append(placed)
        selectedLayerId = placed.id
    }

    /// Reorder image layers (z-order). Called from the layer panel's onMove.
    public func moveImageLayer(from source: IndexSet, to destination: Int) {
        recordLayerChange()
        imageLayers.move(fromOffsets: source, toOffset: destination)
    }

    // MARK: - Selection + layer operations (#367)

    public var selectedLayer: PlacedImage? {
        guard let id = selectedLayerId else { return nil }
        return imageLayers.first { $0.id == id }
    }

    /// Snapshot once at the start of a continuous edit (drag / slider scrub), then apply the
    /// scrub itself with `updateSelected(transient: true)` — one undo step per gesture.
    public func beginSelectedEdit() {
        guard let layer = selectedLayer, !layer.locked else { return }
        recordLayerChange()
    }

    /// Mutate the selected layer in place (properties panel edits). Records one undo step
    /// per call unless `transient` (mid-drag slider scrubs shouldn't flood the history).
    public func updateSelected(transient: Bool = false, _ mutate: (inout PlacedImage) -> Void) {
        guard let id = selectedLayerId,
              let idx = imageLayers.firstIndex(where: { $0.id == id }),
              !imageLayers[idx].locked else { return }
        if !transient { recordLayerChange() } else { isDirty = true }
        mutate(&imageLayers[idx])
    }

    /// Visibility works on locked layers too (locking guards edits, not the eye toggle).
    public func toggleVisible(id: UUID) {
        guard let idx = imageLayers.firstIndex(where: { $0.id == id }) else { return }
        recordLayerChange()
        imageLayers[idx].visible.toggle()
    }

    public func toggleLocked(id: UUID) {
        guard let idx = imageLayers.firstIndex(where: { $0.id == id }) else { return }
        imageLayers[idx].locked.toggle()
        isDirty = true
    }

    public func renameLayer(id: UUID, to name: String) {
        guard let idx = imageLayers.firstIndex(where: { $0.id == id }) else { return }
        recordLayerChange()
        imageLayers[idx].name = name
    }

    /// Paint-layer panel state changes (eye/lock/opacity) go through here so they trip the
    /// unsaved-changes guard.
    public func setPaintState(visible: Bool? = nil, locked: Bool? = nil, opacity: Double? = nil) {
        if let visible { paintVisible = visible }
        if let locked { paintLocked = locked }
        if let opacity { paintOpacity = opacity }
        isDirty = true
    }

    public func deleteLayer(id: UUID) {
        recordLayerChange()
        imageLayers.removeAll { $0.id == id }
        if selectedLayerId == id { selectedLayerId = nil }
    }

    /// Duplicate a layer slightly offset so the copy is visibly distinct; selects the copy.
    public func duplicateLayer(id: UUID) {
        guard let src = imageLayers.first(where: { $0.id == id }) else { return }
        recordLayerChange()
        var copy = PlacedImage(rect: src.rect, texture: src.texture, signAsset: src.signAsset,
                               name: src.name.isEmpty ? "" : "\(src.name) copy",
                               visible: src.visible, locked: false, opacity: src.opacity,
                               rotationDeg: src.rotationDeg, flipH: src.flipH, flipV: src.flipV)
        let dx = min(0.04, 1 - src.rect[2])   // nudge right/down but stay on the facade
        let dy = min(0.04, src.rect[1])
        copy.rect = [src.rect[0] + dx, src.rect[1] - dy, src.rect[2] + dx, src.rect[3] - dy]
        imageLayers.append(copy)
        selectedLayerId = copy.id
    }

    /// Horizontal alignment on the facade, preserving the rect's width (#367 properties panel).
    public enum HorizontalAlignment { case left, center, right }
    public func alignSelected(_ alignment: HorizontalAlignment) {
        updateSelected { img in
            let w = img.rect[2] - img.rect[0]
            let x0: Double
            switch alignment {
            case .left:   x0 = 0
            case .center: x0 = (1 - w) / 2
            case .right:  x0 = 1 - w
            }
            img.rect = [x0, img.rect[1], x0 + w, img.rect[3]]
        }
    }

    // MARK: - Undo / redo (#367)
    //
    // One linear action timeline across the two edit domains: placed-layer mutations
    // (snapshot-restored here) and paint strokes (delegated to PencilKit's own UndoManager
    // via the view-wired callbacks — the VM stays PencilKit-free).

    private enum CanvasAction { case layers([PlacedImage]), paint }
    private var undoStack: [CanvasAction] = []
    private var redoStack: [CanvasAction] = []
    /// Wired by the canvas view to the PKCanvasView's UndoManager.
    public var paintUndo: (() -> Void)?
    public var paintRedo: (() -> Void)?

    public var canUndo: Bool { !undoStack.isEmpty }
    public var canRedo: Bool { !redoStack.isEmpty }

    /// Snapshot the layer array BEFORE a mutation. Every layer-mutating method calls this.
    private func recordLayerChange() {
        undoStack.append(.layers(imageLayers))
        redoStack = []
        isDirty = true
    }

    /// The view reports each PencilKit drawing change so paint edits hold their place
    /// in the shared timeline (and mark the document dirty).
    public func notePaintAction() {
        undoStack.append(.paint)
        redoStack = []
        isDirty = true
    }

    public func undo() {
        guard let action = undoStack.popLast() else { return }
        switch action {
        case .layers(let snapshot):
            redoStack.append(.layers(imageLayers))
            imageLayers = snapshot
            if let id = selectedLayerId, !snapshot.contains(where: { $0.id == id }) {
                selectedLayerId = nil
            }
        case .paint:
            redoStack.append(.paint)
            paintUndo?()
        }
    }

    public func redo() {
        guard let action = redoStack.popLast() else { return }
        switch action {
        case .layers(let snapshot):
            undoStack.append(.layers(imageLayers))
            imageLayers = snapshot
        case .paint:
            undoStack.append(.paint)
            paintRedo?()
        }
    }

    // MARK: - Facade switching (#367)
    //
    // The canvas edits one facade at a time; switching stashes the current facade's draft
    // in memory and restores (or server-loads) the target's. Nothing touches the server
    // until Save, which flushes every dirty facade.

    private struct FacadeDraft {
        var paintStrokes: [Stroke]
        var paintVisible: Bool
        var paintLocked: Bool
        var paintOpacity: Double
        var imageLayers: [PlacedImage]
        var dirty: Bool
        var loaded: Bool
    }
    private var facadeDrafts: [String: FacadeDraft] = [:]

    public func switchFacade(to newFacade: String) async {
        guard newFacade != facade else { return }
        stashCurrentFacade()
        facade = newFacade
        selectedLayerId = nil
        undoStack = []; redoStack = []   // per-facade paint history lives in the PKCanvasView
        if let draft = facadeDrafts[newFacade] {
            paintStrokes = draft.paintStrokes
            paintVisible = draft.paintVisible
            paintLocked = draft.paintLocked
            paintOpacity = draft.paintOpacity
            imageLayers = draft.imageLayers
            isDirty = draft.dirty
        } else {
            paintStrokes = []; imageLayers = []
            paintVisible = true; paintLocked = false; paintOpacity = 1
            isDirty = false     // per-facade dirty: a fresh facade starts clean
            await load()
        }
    }

    private func stashCurrentFacade() {
        facadeDrafts[facade] = FacadeDraft(
            paintStrokes: paintStrokes, paintVisible: paintVisible,
            paintLocked: paintLocked, paintOpacity: paintOpacity,
            imageLayers: imageLayers, dirty: isDirty, loaded: true)
    }

    /// True when any facade (current or stashed) has unsaved edits — the Back guard.
    public var hasUnsavedChanges: Bool {
        isDirty || facadeDrafts.values.contains { $0.dirty }
    }

    /// Save every dirty facade: the current one plus any stashed drafts (#367 Save commits
    /// the whole building's canvas edits, not just the visible facade).
    public func saveAll() async {
        await save()
        guard status == .saved || status == .queued else { return }
        let currentFacade = facade
        for (name, draft) in facadeDrafts where draft.dirty && name != currentFacade {
            var layers: [CanvasLayer] = []
            if !draft.paintStrokes.isEmpty {
                var paint = CanvasLayer.paint(draft.paintStrokes, layer: 0)
                paint.visible = draft.paintVisible; paint.locked = draft.paintLocked
                paint.opacity = draft.paintOpacity
                layers.append(paint)
            }
            for (i, img) in draft.imageLayers.enumerated() {
                var layer = CanvasLayer.image(rect: img.rect, texture: img.texture,
                                              signAsset: img.signAsset, layer: i + 1)
                layer.name = img.name; layer.visible = img.visible; layer.locked = img.locked
                layer.opacity = img.opacity; layer.rotation_deg = img.rotationDeg
                layer.flipH = img.flipH; layer.flipV = img.flipV
                layers.append(layer)
            }
            let canvas = FacadeCanvas(osm_id: osmId, facade: name,
                                      footprint_hash: footprintHash, layers: layers, version: 1)
            do {
                _ = try await client.saveCanvas(canvas)
                facadeDrafts[name]?.dirty = false
            } catch {
                if let store = draftStore {
                    await store.enqueue(canvas)
                    facadeDrafts[name]?.dirty = false
                    status = .queued
                } else {
                    status = .failed(String(describing: error))
                }
            }
        }
    }

    // MARK: - Asset library (#367)

    /// Load every stored sign asset for the bottom asset library. Non-fatal on failure —
    /// the library shows empty and placement via AI Sign / upload still works.
    public func loadAssets() async {
        signAssets = (try? await client.listSigns()) ?? []
    }

    public func savePalette(_ palette: Palette) async -> Palette? {
        do {
            return try await client.createPalette(palette)
        } catch {
            status = .failed(String(describing: error))
            return nil
        }
    }

    private func apply(_ canvas: FacadeCanvas) {
        let paint = canvas.layers.first(where: { $0.kind == "paint" })
        paintStrokes = paint?.strokes ?? []
        paintVisible = paint?.visible ?? true
        paintLocked = paint?.locked ?? false
        paintOpacity = paint?.opacity ?? 1
        imageLayers = canvas.layers
            .filter { $0.kind == "image" }
            .sorted { $0.layer < $1.layer }
            .map { PlacedImage(rect: $0.rect, texture: $0.texture, signAsset: $0.signAsset,
                               name: $0.name, visible: $0.visible, locked: $0.locked,
                               opacity: $0.opacity, rotationDeg: $0.rotation_deg,
                               flipH: $0.flipH, flipV: $0.flipV) }
        isDirty = false
        selectedLayerId = nil
        undoStack = []; redoStack = []
    }
}

import Foundation

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
        public init(id: UUID = UUID(), rect: [Double], texture: String, signAsset: String = "") {
            self.id = id; self.rect = rect; self.texture = texture; self.signAsset = signAsset
        }
    }

    public enum Status: Equatable { case idle, loading, saving, saved, failed(String) }

    public let osmId: Int
    @Published public var facade: String
    public let footprintHash: String

    @Published public var paintStrokes: [Stroke] = []      // set by the view from the PKDrawing
    @Published public var imageLayers: [PlacedImage] = []
    @Published public private(set) var status: Status = .idle

    private let client: ServerClient

    public init(osmId: Int, facade: String = "Front", footprintHash: String = "",
                client: ServerClient) {
        self.osmId = osmId; self.facade = facade; self.footprintHash = footprintHash
        self.client = client
    }

    /// Assemble the layered document: one paint layer (if any strokes) at the bottom, then each
    /// placed image as its own layer above it — matching the server's flatten-on-export contract.
    public func buildCanvas() -> FacadeCanvas {
        var layers: [CanvasLayer] = []
        if !paintStrokes.isEmpty {
            layers.append(.paint(paintStrokes, layer: 0))
        }
        for (i, img) in imageLayers.enumerated() {
            layers.append(.image(rect: img.rect, texture: img.texture,
                                 signAsset: img.signAsset, layer: i + 1))
        }
        return FacadeCanvas(osm_id: osmId, facade: facade,
                            footprint_hash: footprintHash, layers: layers, version: 1)
    }

    public func save() async {
        status = .saving
        do {
            _ = try await client.saveCanvas(buildCanvas())
            status = .saved
        } catch {
            status = .failed(String(describing: error))
        }
    }

    /// Load the server's canvas for this facade. Paint strokes are re-hydrated for a future
    /// re-edit; the on-screen PKDrawing reconstruction from strokes is a follow-up, so here we
    /// restore the image layers and keep the strokes available on the model.
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

    public func requestSign(_ request: SignRequest) async -> SignDef? {
        do {
            return try await client.generateSign(request)
        } catch {
            status = .failed(String(describing: error))
            return nil
        }
    }

    /// Place a generated/existing sign as an image layer (a centred default rect the user can move).
    public func placeSign(_ sign: SignDef, rect: [Double] = [0.4, 0.55, 0.6, 0.75]) {
        imageLayers.append(.init(rect: rect, texture: sign.png, signAsset: sign.signId))
    }

    private func apply(_ canvas: FacadeCanvas) {
        paintStrokes = canvas.layers.first(where: { $0.kind == "paint" })?.strokes ?? []
        imageLayers = canvas.layers
            .filter { $0.kind == "image" }
            .map { PlacedImage(rect: $0.rect, texture: $0.texture, signAsset: $0.signAsset) }
    }
}

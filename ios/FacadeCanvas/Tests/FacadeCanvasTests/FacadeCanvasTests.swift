import XCTest
@testable import FacadeCanvas

// Pure model / view-model logic (no PencilKit/SwiftUI), so `swift test` runs it off-device. The
// critical property: the canvas JSON matches the server's shape (server/app/models.py) so it
// round-trips through POST/GET /canvas and drives the Unity export (#280/#281).
final class FacadeCanvasTests: XCTestCase {

    func testFacadeCanvasEncodesWithServerKeys() throws {
        let canvas = FacadeCanvas(
            osm_id: 65307880, facade: "Front", footprint_hash: "a3f1c9d2",
            layers: [
                .paint([Stroke(points: [[0.1, 0.1], [0.9, 0.9]], color: "#ff0000", width: 0.05)], layer: 0),
                .image(rect: [0.4, 0.6, 0.6, 0.82], texture: "Signs/sign_lucca_deli.png",
                       signAsset: "sign_lucca_deli", layer: 1),
            ])
        let data = try JSONEncoder().encode(canvas)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(obj["osm_id"] as? Int, 65307880)
        XCTAssertEqual(obj["footprint_hash"] as? String, "a3f1c9d2")
        let layers = try XCTUnwrap(obj["layers"] as? [[String: Any]])
        XCTAssertEqual(layers[0]["kind"] as? String, "paint")
        let stroke = try XCTUnwrap((layers[0]["strokes"] as? [[String: Any]])?.first)
        XCTAssertEqual(stroke["color"] as? String, "#ff0000")
        XCTAssertEqual(layers[1]["kind"] as? String, "image")
        XCTAssertEqual(layers[1]["mountDepth_m"] as? Double, 0.03)
        XCTAssertEqual(layers[1]["signAsset"] as? String, "sign_lucca_deli")
    }

    func testDecodesServerCanvas() throws {
        let json = Data("""
        {"osm_id":7,"facade":"Front","footprint_hash":"h","version":1,
         "layers":[{"kind":"paint","layer":0,"mountDepth_m":0.02,
                    "strokes":[{"points":[[0.0,0.0]],"color":"#000000","width":0.01}],
                    "rect":[0,0,1,1],"texture":"","signAsset":""}]}
        """.utf8)
        let c = try JSONDecoder().decode(FacadeCanvas.self, from: json)
        XCTAssertEqual(c.osm_id, 7)
        XCTAssertEqual(c.layers.first?.kind, "paint")
        XCTAssertEqual(c.layers.first?.strokes.first?.width, 0.01)
    }

    @MainActor
    func testBuildCanvasAssemblesPaintThenImageLayers() {
        let client = ServerClient(baseURL: URL(string: "http://localhost:8000")!)
        let vm = FacadeCanvasViewModel(osmId: 42, facade: "Front", footprintHash: "abcd", client: client)
        vm.paintStrokes = [Stroke(points: [[0, 0], [1, 1]], color: "#123456", width: 0.02)]
        vm.imageLayers = [.init(rect: [0.3, 0.4, 0.5, 0.6], texture: "Signs/x.png", signAsset: "x")]

        let canvas = vm.buildCanvas()
        XCTAssertEqual(canvas.osm_id, 42)
        XCTAssertEqual(canvas.footprint_hash, "abcd")
        XCTAssertEqual(canvas.layers.map(\.kind), ["paint", "image"])
        XCTAssertEqual(canvas.layers[0].layer, 0)
        XCTAssertEqual(canvas.layers[1].layer, 1)
        XCTAssertEqual(canvas.layers[1].signAsset, "x")
    }

    @MainActor
    func testPlaceSignAddsImageLayer() {
        let client = ServerClient(baseURL: URL(string: "http://localhost:8000")!)
        let vm = FacadeCanvasViewModel(osmId: 1, client: client)
        let sign = SignDef(signId: "sign_lucca_deli", png: "Signs/sign_lucca_deli.png",
                           thumb: "Signs/sign_lucca_deli.thumb.png", provider: "local-stub", version: 1,
                           businessType: "deli", neighborhood: "Mission", text: "LUCCA",
                           aspectRatio: "3:1", stylePreset: "")
        vm.placeSign(sign)
        XCTAssertEqual(vm.imageLayers.count, 1)
        XCTAssertEqual(vm.imageLayers[0].texture, "Signs/sign_lucca_deli.png")
        XCTAssertEqual(vm.imageLayers[0].signAsset, "sign_lucca_deli")
    }

    // MARK: - PartGlbGenerator (#327: traced outline -> flat polygon mesh)

    // Parses just the glTF JSON chunk out of the binary GLB container so tests can assert
    // on vertex/index counts without a full glTF reader.
    private func glbJSON(_ data: Data) throws -> [String: Any] {
        let bytes = [UInt8](data)
        func u32(_ offset: Int) -> UInt32 {
            UInt32(bytes[offset]) | (UInt32(bytes[offset + 1]) << 8)
                | (UInt32(bytes[offset + 2]) << 16) | (UInt32(bytes[offset + 3]) << 24)
        }
        let jsonLength = Int(u32(12))
        let jsonData = data.subdata(in: 20..<(20 + jsonLength))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: jsonData) as? [String: Any])
    }

    private func accessorCounts(_ json: [String: Any]) throws -> [Int] {
        let accessors = try XCTUnwrap(json["accessors"] as? [[String: Any]])
        return try accessors.map { try XCTUnwrap($0["count"] as? Int) }
    }

    func testPartGlbGeneratorFallsBackToQuadWithoutOutline() throws {
        let counts = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1.2, height: 1.6)))
        XCTAssertEqual(counts, [4, 4, 4, 6])   // POSITION, NORMAL, TEXCOORD_0: 4 verts; INDICES: 2 triangles
    }

    func testPartGlbGeneratorFallsBackOnDegenerateOutline() throws {
        // Only 2 points — not enough to form a polygon.
        let outline = [TracePoint(0, 0), TracePoint(1, 0)]
        let counts = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1, height: 1, outline: outline)))
        XCTAssertEqual(counts, [4, 4, 4, 6])
    }

    func testPartGlbGeneratorTriangulatesTracedOutline() throws {
        let outline = [TracePoint(0, 0), TracePoint(2, 0), TracePoint(1, 2), TracePoint(0, 1)]
        let counts = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1.2, height: 1.6, outline: outline)))
        XCTAssertEqual(counts, [4, 4, 4, 6])   // no near-collinear points to simplify away
    }

    func testPartGlbGeneratorSimplifiesNearCollinearPoints() throws {
        // A near-straight bottom edge (0,0)-(1,0.001)-(2,0) should collapse to one segment.
        let outline = [TracePoint(0, 0), TracePoint(1, 0.001), TracePoint(2, 0),
                        TracePoint(2, 2), TracePoint(0, 2)]
        let counts = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1, height: 1, outline: outline)))
        XCTAssertEqual(counts, [4, 4, 4, 6])   // 5 traced points -> 4 after simplification
    }

    func testPartGlbGeneratorAcceptsEitherWindingDirection() throws {
        let ccw = [TracePoint(0, 0), TracePoint(2, 0), TracePoint(2, 2), TracePoint(0, 2)]
        let cw = Array(ccw.reversed())
        let countsCCW = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1, height: 1, outline: ccw)))
        let countsCW = try accessorCounts(glbJSON(PartGlbGenerator.generate(width: 1, height: 1, outline: cw)))
        XCTAssertEqual(countsCCW, [4, 4, 4, 6])
        XCTAssertEqual(countsCW, [4, 4, 4, 6])
    }
}

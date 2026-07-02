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

    // MARK: - DashboardViewModel (#345)

    func testDistrictDefRoundTripsWithServerKeys() throws {
        let district = DistrictDef(id: "mission", name: "Mission", neighborhoods: ["Mission"],
                                   templateWeights: [TemplateWeight(template: "victorian_a", weight: 50)],
                                   palette: "Mission", signStyle: "Bilingual")
        let data = try JSONEncoder().encode(district)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(obj["id"] as? String, "mission")
        XCTAssertEqual(obj["neighborhoods"] as? [String], ["Mission"])
        let weights = try XCTUnwrap(obj["templateWeights"] as? [[String: Any]])
        XCTAssertEqual(weights[0]["template"] as? String, "victorian_a")
        XCTAssertEqual(weights[0]["weight"] as? Double, 50)
        XCTAssertEqual(obj["signStyle"] as? String, "Bilingual")

        let decoded = try JSONDecoder().decode(DistrictDef.self, from: data)
        XCTAssertEqual(decoded, district)
    }

    func testRecentFromReturnsTailReversed() {
        let templates = (1...8).map { TemplateDef(id: "t\($0)") }
        let recent = DashboardViewModel.recentFrom(templates, limit: 5)
        XCTAssertEqual(recent.map(\.id), ["t8", "t7", "t6", "t5", "t4"])
    }

    func testRecentFromHandlesFewerThanLimit() {
        let templates = [TemplateDef(id: "only")]
        XCTAssertEqual(DashboardViewModel.recentFrom(templates, limit: 5).map(\.id), ["only"])
    }

    func testRecentFromEmptyInputsYieldsEmpty() {
        XCTAssertEqual(DashboardViewModel.recentFrom([], limit: 5), [])
        XCTAssertEqual(DashboardViewModel.recentFrom([TemplateDef(id: "a")], limit: 0), [])
    }

    // MARK: - GenerationViewModel (#346)

    func testExportRequestEncodesScopeFields() throws {
        let req = ExportRequest(outDir: "/tmp/out", scope: .building, osm_id: 1001, neighborhood: "")
        let data = try JSONEncoder().encode(req)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(obj["scope"] as? String, "building")
        XCTAssertEqual(obj["osm_id"] as? Int, 1001)
    }

    func testExportResultDecodesScopeField() throws {
        let json = Data("""
        {"outDir":"/x","version":1,"scope":"neighborhood","parts":1,"templates":1,
         "palettes":1,"overrides":2,"glbsCopied":0,"signs":0,"facadeDecals":0,"paintTextures":0}
        """.utf8)
        let result = try JSONDecoder().decode(ExportResult.self, from: json)
        XCTAssertEqual(result.scope, "neighborhood")
        XCTAssertEqual(result.overrides, 2)
    }

    func testExportScopeDisplayNameCapitalizesRawValue() {
        XCTAssertEqual(ExportScope.building.displayName, "Building")
        XCTAssertEqual(ExportScope.neighborhood.displayName, "Neighborhood")
        XCTAssertEqual(ExportScope.city.displayName, "City")
        XCTAssertEqual(ExportScope.block.displayName, "Block")
    }

    func testParseOsmIdAcceptsDigitsRejectsGarbage() {
        XCTAssertEqual(GenerationViewModel.parseOsmId("65307880"), 65307880)
        XCTAssertEqual(GenerationViewModel.parseOsmId("  65307880  "), 65307880)
        XCTAssertNil(GenerationViewModel.parseOsmId(""))
        XCTAssertNil(GenerationViewModel.parseOsmId("not a number"))
    }

    @MainActor
    func testCanPublishRequiresScopeSpecificInput() {
        let client = ServerClient(baseURL: URL(string: "http://localhost:8000")!)
        let vm = GenerationViewModel(client: client)

        vm.scope = .city
        XCTAssertTrue(vm.canPublish)

        vm.scope = .block
        XCTAssertFalse(vm.canPublish, "block is never publishable — the server can't scope to it yet")

        vm.scope = .building
        XCTAssertFalse(vm.canPublish)
        vm.osmIdText = "not a number"
        XCTAssertFalse(vm.canPublish)
        vm.osmIdText = "65307880"
        XCTAssertTrue(vm.canPublish)

        vm.scope = .neighborhood
        XCTAssertFalse(vm.canPublish)
        vm.neighborhoodText = "   "
        XCTAssertFalse(vm.canPublish, "whitespace-only neighborhood should not count as specified")
        vm.neighborhoodText = "Mission"
        XCTAssertTrue(vm.canPublish)
    }

    // MARK: - TemplateBrowserViewModel (#337)

    func testCardSubtitleCountsExactAndRules() {
        let template = TemplateDef(
            id: "t1",
            exact: [ExactPlacement(part: "a", facade: "Front"), ExactPlacement(part: "b", facade: "Front")],
            rules: [ProceduralRule(part: "c", facade: "Front")]
        )
        XCTAssertEqual(TemplateBrowserViewModel.cardSubtitle(template), "2 exact · 1 rules")
    }

    func testCardSubtitleHandlesEmptyTemplate() {
        XCTAssertEqual(TemplateBrowserViewModel.cardSubtitle(TemplateDef(id: "empty")), "0 exact · 0 rules")
    }

    // MARK: - AssetsGridViewModel (#344)

    func testFallbackColorNameCoversAllKnownCategories() {
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Window"), "blue")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Door"), "brown")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Garage"), "gray")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "BayWindow"), "teal")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Storefront"), "orange")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Sign"), "red")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Roof"), "indigo")
    }

    func testFallbackColorNameDefaultsForUnknownCategory() {
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: "Balcony"), "secondary")
        XCTAssertEqual(AssetsGridViewModel.fallbackColorName(for: ""), "secondary")
    }

    func testFallbackIconCoversAllKnownCategories() {
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Window"), "rectangle")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "BayWindow"), "rectangle")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Door"), "rectangle.portrait")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Garage"), "door.garage.closed")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Storefront"), "storefront")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Sign"), "signpost.right")
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Roof"), "house")
    }

    func testFallbackIconDefaultsForUnknownCategory() {
        XCTAssertEqual(AssetsGridViewModel.fallbackIcon(for: "Balcony"), "cube")
    }

    // MARK: - Zone Codable (#338)

    func testZoneEncodesServerKeysAndExcludesEditorOnlyState() throws {
        var zone = Zone(id: "window_1", type: "Window", facade: "Front",
                        shape: ZoneShape(kind: "rect", points: [[0.2, 0.0], [0.8, 0.0], [0.8, 1.0], [0.2, 1.0]]),
                        floorRange: IntRange(min: 1, max: 2))
        zone.isHidden = true
        zone.isLocked = true

        let data = try JSONEncoder().encode(zone)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(obj["id"] as? String, "window_1")
        XCTAssertEqual(obj["type"] as? String, "Window")
        XCTAssertNil(obj["isHidden"], "editor-only state must never reach the wire")
        XCTAssertNil(obj["isLocked"], "editor-only state must never reach the wire")
        let shape = try XCTUnwrap(obj["shape"] as? [String: Any])
        XCTAssertEqual(shape["kind"] as? String, "rect")

        // Round-trips back with isHidden/isLocked reset to false (server never sent them).
        let decoded = try JSONDecoder().decode(Zone.self, from: data)
        XCTAssertEqual(decoded.id, "window_1")
        XCTAssertFalse(decoded.isHidden)
        XCTAssertFalse(decoded.isLocked)
    }

    func testZoneDecodesServerPayloadMissingOptionalFields() throws {
        let json = Data("""
        {"id":"z1","type":"Door"}
        """.utf8)
        let zone = try JSONDecoder().decode(Zone.self, from: json)
        XCTAssertEqual(zone.id, "z1")
        XCTAssertEqual(zone.type, "Door")
        XCTAssertEqual(zone.facade, "Front")
        XCTAssertEqual(zone.shape.kind, "rect")
        XCTAssertFalse(zone.isHidden)
    }

    // MARK: - ZoneDrawingViewModel geometry (#338)

    // XCTAssertEqual's `accuracy:` overload is only defined for a single FloatingPoint value,
    // not [Double] — this compares element-by-element instead. Used for rects AND for any other
    // [Double] comparison (e.g. weight percentages) that needs a tolerance.
    private func assertDoublesEqual(_ a: [Double], _ b: [Double], accuracy: Double,
                                     file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(a.count, b.count, file: file, line: line)
        for (x, y) in zip(a, b) {
            XCTAssertEqual(x, y, accuracy: accuracy, file: file, line: line)
        }
    }

    func testNormalizedRectSortsAndFlipsY() {
        // Drag from bottom-right (60,80) to top-left (20,20) on a 100x100 canvas — screen space
        // is top-down, facade UV is bottom-up, so screen-y=80 (near bottom) -> UV-y=0.2 (low),
        // and screen-y=20 (near top) -> UV-y=0.8 (high).
        let rect = ZoneDrawingViewModel.normalizedRect(
            from: (x: 60, y: 80), to: (x: 20, y: 20), canvasWidth: 100, canvasHeight: 100)
        assertDoublesEqual(rect, [0.2, 0.2, 0.6, 0.8], accuracy: 0.0001)
    }

    func testNormalizedRectEnforcesMinimumSize() {
        // A near-zero drag must not produce a degenerate rect.
        let rect = ZoneDrawingViewModel.normalizedRect(
            from: (x: 50, y: 50), to: (x: 50.1, y: 50.1), canvasWidth: 100, canvasHeight: 100, minSize: 0.1)
        XCTAssertGreaterThanOrEqual(rect[2] - rect[0], 0.1 - 0.0001)
        XCTAssertGreaterThanOrEqual(rect[3] - rect[1], 0.1 - 0.0001)
    }

    func testNormalizedRectClampsToUnitSquare() {
        // A drag that overshoots the canvas bounds must clamp into [0,1].
        let rect = ZoneDrawingViewModel.normalizedRect(
            from: (x: -50, y: -50), to: (x: 150, y: 150), canvasWidth: 100, canvasHeight: 100)
        assertDoublesEqual(rect, [0, 0, 1, 1], accuracy: 0.0001)
    }

    func testNormalizedRectHandlesZeroCanvasSize() {
        let rect = ZoneDrawingViewModel.normalizedRect(
            from: (x: 10, y: 10), to: (x: 20, y: 20), canvasWidth: 0, canvasHeight: 0)
        XCTAssertEqual(rect.count, 4)
    }

    func testMovedRectKeepsSizeAndClampsCenter() {
        let rect = [0.4, 0.4, 0.6, 0.6]   // 0.2 x 0.2, centred at (0.5, 0.5)
        // Drag the centre toward the top-left screen corner (0,0) -> UV (0, 1) -> clamp so the
        // rect's half-size (0.1) keeps it fully inside [0,1].
        let moved = ZoneDrawingViewModel.movedRect(
            rect, centerTo: (x: 0, y: 0), canvasWidth: 100, canvasHeight: 100)
        XCTAssertEqual(moved[2] - moved[0], 0.2, accuracy: 0.0001, "size must be preserved")
        XCTAssertEqual(moved[3] - moved[1], 0.2, accuracy: 0.0001)
        XCTAssertEqual(moved[0], 0.0, accuracy: 0.0001)
        XCTAssertEqual(moved[3], 1.0, accuracy: 0.0001)
    }

    @MainActor
    func testCommitNewZoneRequiresActiveTool() {
        let vm = ZoneDrawingViewModel()
        vm.activeTool = nil
        let id = vm.commitNewZone(from: (x: 0, y: 0), to: (x: 50, y: 50), canvasWidth: 100, canvasHeight: 100)
        XCTAssertNil(id)
        XCTAssertTrue(vm.zones.isEmpty)
    }

    @MainActor
    func testCommitNewZoneAddsRectZoneOfActiveType() {
        let vm = ZoneDrawingViewModel()
        vm.activeTool = .storefront
        let id = vm.commitNewZone(from: (x: 20, y: 80), to: (x: 60, y: 20), canvasWidth: 100, canvasHeight: 100)
        XCTAssertEqual(id, "storefront")
        XCTAssertEqual(vm.zones.count, 1)
        XCTAssertEqual(vm.zones[0].type, "Storefront")
        XCTAssertEqual(vm.zones[0].shape.kind, "rect")
        assertDoublesEqual(ZoneDrawingViewModel.rect(of: vm.zones[0]), [0.2, 0.2, 0.6, 0.8], accuracy: 0.0001)
    }

    @MainActor
    func testCommitNewZoneGeneratesUniqueIdsForSameType() {
        let vm = ZoneDrawingViewModel()
        vm.activeTool = .window
        _ = vm.commitNewZone(from: (x: 0, y: 60), to: (x: 20, y: 80), canvasWidth: 100, canvasHeight: 100)
        let secondId = vm.commitNewZone(from: (x: 30, y: 60), to: (x: 50, y: 80), canvasWidth: 100, canvasHeight: 100)
        XCTAssertEqual(vm.zones.map(\.id), ["window", "window_2"])
        XCTAssertEqual(secondId, "window_2")
    }

    @MainActor
    func testMoveZoneNoOpsWhenLocked() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1", shape: ZoneShape(points: [[0.4, 0.4], [0.6, 0.4], [0.6, 0.6], [0.4, 0.6]]))])
        vm.toggleLocked(id: "z1")
        XCTAssertTrue(vm.zones[0].isLocked)
        let before = ZoneDrawingViewModel.rect(of: vm.zones[0])
        vm.moveZone(id: "z1", to: (x: 0, y: 0), canvasWidth: 100, canvasHeight: 100)
        XCTAssertEqual(ZoneDrawingViewModel.rect(of: vm.zones[0]), before, "locked zone must not move")
    }

    @MainActor
    func testToggleHiddenAndLocked() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1")])
        XCTAssertFalse(vm.zones[0].isHidden)
        vm.toggleHidden(id: "z1")
        XCTAssertTrue(vm.zones[0].isHidden)
        vm.toggleHidden(id: "z1")
        XCTAssertFalse(vm.zones[0].isHidden)

        XCTAssertFalse(vm.zones[0].isLocked)
        vm.toggleLocked(id: "z1")
        XCTAssertTrue(vm.zones[0].isLocked)
    }

    @MainActor
    func testRenameValidation() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1"), Zone(id: "z2")])
        vm.rename(id: "z1", to: "front_window")
        XCTAssertEqual(vm.zones.map(\.id), ["front_window", "z2"])

        vm.rename(id: "front_window", to: "z2")   // collides with an existing id -> no-op
        XCTAssertEqual(vm.zones.map(\.id), ["front_window", "z2"])

        vm.rename(id: "front_window", to: "   ")  // blank -> no-op
        XCTAssertEqual(vm.zones.map(\.id), ["front_window", "z2"])
    }

    @MainActor
    func testDuplicateCreatesZoneWithNewId() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1", type: "Sign")])
        vm.duplicate(id: "z1")
        XCTAssertEqual(vm.zones.map(\.id), ["z1", "z1_2"])
        XCTAssertEqual(vm.zones[1].type, "Sign")

        vm.duplicate(id: "z1")
        XCTAssertEqual(vm.zones.map(\.id), ["z1", "z1_2", "z1_3"])
    }

    @MainActor
    func testDeleteRemovesZone() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1"), Zone(id: "z2")])
        vm.delete(id: "z1")
        XCTAssertEqual(vm.zones.map(\.id), ["z2"])
    }

    // MARK: - Properties pane (#339)

    func testWeightPercentagesMatchesIssueExample() {
        // "Victorian A 50% / B 30% / C 20%" — the issue's own worked example.
        let parts = [WeightedPart(part: "a", weight: 50), WeightedPart(part: "b", weight: 30),
                     WeightedPart(part: "c", weight: 20)]
        let pct = ZoneDrawingViewModel.weightPercentages(parts)
        assertDoublesEqual(pct, [50, 30, 20], accuracy: 0.0001)
    }

    func testWeightPercentagesNormalizesArbitraryWeights() {
        let parts = [WeightedPart(part: "a", weight: 1), WeightedPart(part: "b", weight: 1),
                     WeightedPart(part: "c", weight: 2)]
        let pct = ZoneDrawingViewModel.weightPercentages(parts)
        assertDoublesEqual(pct, [25, 25, 50], accuracy: 0.0001)
    }

    func testWeightPercentagesHandlesEmptyAndZeroTotal() {
        XCTAssertEqual(ZoneDrawingViewModel.weightPercentages([]), [])
        let allZero = [WeightedPart(part: "a", weight: 0), WeightedPart(part: "b", weight: 0)]
        XCTAssertEqual(ZoneDrawingViewModel.weightPercentages(allZero), [0, 0])
    }

    func testWeightPercentagesIgnoresNegativeWeights() {
        // max(weight, 0) clamps a negative (invalid) weight to 0 rather than skewing the total.
        let parts = [WeightedPart(part: "a", weight: -10), WeightedPart(part: "b", weight: 40)]
        XCTAssertEqual(ZoneDrawingViewModel.weightPercentages(parts), [0, 100])
    }

    @MainActor
    func testUpdateZoneReplacesMatchingZoneOnly() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1", type: "Window"), Zone(id: "z2", type: "Door")])
        var updated = vm.zones[0]
        updated.rules.alignment = "Free"
        vm.updateZone(updated)
        XCTAssertEqual(vm.zones[0].rules.alignment, "Free")
        XCTAssertEqual(vm.zones[1].type, "Door", "unrelated zone must be untouched")
    }

    @MainActor
    func testUpdateZoneNoOpsForUnknownId() {
        let vm = ZoneDrawingViewModel(zones: [Zone(id: "z1")])
        vm.updateZone(Zone(id: "ghost", type: "Sign"))
        XCTAssertEqual(vm.zones.map(\.id), ["z1"])
    }
}

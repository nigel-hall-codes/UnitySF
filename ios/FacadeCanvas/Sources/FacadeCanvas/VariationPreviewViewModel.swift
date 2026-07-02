import Combine
import Foundation

/// State + load for the variation preview strip (#340, UX spec "Variation Preview"): resolves
/// a template against a fixed synthetic building for seeds 1..N via POST /templates/{id}/resolve
/// (#336), so a template can be evaluated for variety in seconds without a real building or a
/// Unity round-trip. Deliberately UIKit- and SwiftUI-free (matching the rest of this package's
/// view-models) so the schematic geometry is testable via `swift test` without an iOS Simulator.
@MainActor
public final class VariationPreviewViewModel: ObservableObject {
    @Published public var variants: [ResolvedFacade] = []
    @Published public var partsById: [String: PartDef] = [:]
    @Published public var isLoading = false
    @Published public var errorMessage: String?

    private let client: ServerClient
    private let templateId: String
    public let seedCount: Int

    public init(client: ServerClient, templateId: String, seedCount: Int = 5) {
        self.client = client
        self.templateId = templateId
        self.seedCount = max(seedCount, 1)
    }

    /// A fixed, reasonable synthetic building for previewing a template out of context — no real
    /// osm_id required, only the facade geometry (one street facade, floorCount floors) the
    /// placement algorithm needs to size procedural rule counts (design #326 §3.2: "resolve
    /// against real (osm_id) or synthetic building facts"). osm_id -1 flags it as not-a-real-
    /// building; the server only reads it when scope/osm_id lookup is requested, which this isn't.
    nonisolated static let facadeWidthM = 10.0
    nonisolated static let floorCount = 3
    nonisolated static let floorHeightM = 3.0   // matches BuildingAssembler.FloorHeightMeters
    nonisolated static var facadeHeightM: Double { Double(floorCount) * floorHeightM }

    nonisolated static func syntheticFacts(neighborhood: String = "") -> BuildingFacts {
        BuildingFacts(osm_id: -1, neighborhood: neighborhood, floor_count: floorCount,
                     street_facades: [StreetFacade(edge_index: 0, edge: [0, 0, facadeWidthM, 0])])
    }

    /// Resolves seeds 1...seedCount sequentially (not concurrently — seedCount is small, ~5, and
    /// sequential keeps this simple and avoids introducing TaskGroup-based concurrency this
    /// screen doesn't need).
    public func load() async {
        isLoading = true
        errorMessage = nil
        do {
            let parts = try await client.listParts()
            partsById = Dictionary(uniqueKeysWithValues: parts.map { ($0.id, $0) })

            var results: [ResolvedFacade] = []
            for seed in 1...seedCount {
                let facade = try await client.resolveTemplate(
                    templateId: templateId, facts: Self.syntheticFacts(), seed: seed)
                results.append(facade)
            }
            variants = results
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    /// The category of a placement's part, or "" if the part isn't in the currently-loaded
    /// parts list (an unauthored/deleted part id referenced by a stale template rule).
    public func category(forPart partId: String) -> String {
        partsById[partId]?.category ?? ""
    }

    // MARK: - Schematic geometry (pure, testable)

    /// A placement's vertical position on the WHOLE facade (0-1, bottom-origin), combining its
    /// floor index with its within-floor y offset — ResolvedPlacement.y is normalized within one
    /// floor's band (design #326 D2), not the whole facade, so this is (floor + y) / floorCount.
    /// x needs no equivalent transform: ResolvedPlacement.x is already normalized to the whole
    /// facade width, the same convention Exact/ProceduralRule placements already use.
    nonisolated static func schematicY(floor: Int, y: Double, floorCount: Int) -> Double {
        let fc = max(floorCount, 1)
        return min(max((Double(floor) + y) / Double(fc), 0), 1)
    }

    /// A placement's rendered size in normalized facade UV, from its real part dimensions
    /// (meters) and the facade's real dimensions (meters). A part with no size on record
    /// (w_m/h_m <= 0 — e.g. no PartDef loaded for it) gets a small fixed placeholder size rather
    /// than collapsing to zero-size/invisible.
    nonisolated static func schematicSize(wM: Double, hM: Double,
                                          facadeWidthM: Double, facadeHeightM: Double) -> (w: Double, h: Double) {
        guard facadeWidthM > 0, facadeHeightM > 0 else { return (0.05, 0.05) }
        let w = wM > 0 ? wM / facadeWidthM : 0.05
        let h = hM > 0 ? hM / facadeHeightM : 0.05
        return (min(max(w, 0.01), 1), min(max(h, 0.01), 1))
    }
}

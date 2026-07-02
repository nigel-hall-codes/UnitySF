import Combine
import Foundation

/// State + load for the district schematic preview (#342, UX spec "District Preview" — "Generate
/// Street: 10-50 buildings using district rules"). Resolves buildingCount synthetic buildings,
/// each picking a template weighted by the district's templateWeights (design #326 D4/#343),
/// then resolving that template via POST /templates/{id}/resolve (#336) same as the Variation
/// Preview (#340). Deliberately UIKit- and SwiftUI-free (matching the rest of this package's
/// view-models) so the weighted-pick geometry is testable via `swift test` without an iOS
/// Simulator.
///
/// The template-weighted PICK here is a client-side, preview-only approximation — the
/// AUTHORITATIVE weighted selection happens at Unity assembly time (BuildingAssembler.PickWeighted,
/// #343), reading the district weights the server annotates into library.json at export. This
/// screen has no equivalent "resolve a building under district rules" server endpoint to call
/// (POST /templates/{id}/resolve needs a specific template id, it doesn't do selection itself),
/// so picking a template client-side + resolving it is the closest preview obtainable without
/// adding new server surface (consistent with #344's Unity-push-not-rasterizer and #340's
/// synthetic-facts approach elsewhere in this feature set).
@MainActor
public final class DistrictPreviewViewModel: ObservableObject {
    public struct Building: Equatable {
        public let templateId: String
        public let facade: ResolvedFacade
    }

    @Published public var buildings: [Building] = []
    @Published public var partsById: [String: PartDef] = [:]
    @Published public var isLoading = false
    @Published public var errorMessage: String?

    private let client: ServerClient
    public let buildingCount: Int

    public init(client: ServerClient, buildingCount: Int = 6) {
        self.client = client
        self.buildingCount = max(buildingCount, 1)
    }

    /// Deterministic weighted pick over allowedParts-style weights, seeded so the same district +
    /// seed always yields the same street (design's determinism contract). The xorshift32 mix
    /// itself is bit-for-bit the same technique used server-side (server/app/resolve.py's _Rng)
    /// and Unity-side (BuildingAssembler.Rng) for PLACEMENT randomness. This deliberately
    /// diverges from BuildingAssembler.PickWeighted specifically, though: that method draws
    /// straight from its (already-hashed) seed's low 24 bits with no xorshift mix, since its
    /// seed input is a per-osm_id hash. This preview's seeds are small sequential ints (0..5),
    /// which would produce a badly-skewed draw without the mix — so mixing first is the correct
    /// choice for this seed shape, not a literal mirror of PickWeighted's steps.
    /// nil when there's nothing positively weighted to pick (an unauthored/all-zero district).
    nonisolated static func pickTemplate(from weights: [TemplateWeight], seed: Int) -> String? {
        let positive = weights.filter { $0.weight > 0 }
        guard !positive.isEmpty else { return nil }
        let total = positive.reduce(0.0) { $0 + $1.weight }
        guard total > 0 else { return nil }

        var s = UInt32(bitPattern: Int32(truncatingIfNeeded: seed))
        if s == 0 { s = 1 }
        s ^= s << 13; s ^= s >> 17; s ^= s << 5
        let draw = Double(s & 0xFFFFFF) / 16_777_216.0 * total

        var acc = 0.0
        for w in positive {
            acc += w.weight
            if draw < acc { return w.template }
        }
        return positive.last?.template   // float-rounding guard, matches the server/Unity pattern
    }

    public func load(district: DistrictDef) async {
        isLoading = true
        errorMessage = nil
        do {
            let parts = try await client.listParts()
            partsById = Dictionary(uniqueKeysWithValues: parts.map { ($0.id, $0) })

            let neighborhood = district.neighborhoods.first ?? ""
            var results: [Building] = []
            for seed in 0..<buildingCount {
                guard let templateId = Self.pickTemplate(from: district.templateWeights, seed: seed) else { continue }
                let facts = VariationPreviewViewModel.syntheticFacts(neighborhood: neighborhood)
                let facade = try await client.resolveTemplate(templateId: templateId, facts: facts, seed: seed)
                results.append(Building(templateId: templateId, facade: facade))
            }
            buildings = results
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    public func category(forPart partId: String) -> String {
        partsById[partId]?.category ?? ""
    }
}

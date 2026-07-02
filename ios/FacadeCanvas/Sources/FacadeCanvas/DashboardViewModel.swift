import Combine
import Foundation

/// State + load for the Dashboard screen (#345, UX spec "Dashboard"): counts across the
/// library plus a recently-authored templates list. Deliberately UIKit-free (like
/// FacadeCanvasViewModel) so it's testable via `swift test` without an iOS Simulator —
/// DashboardView (SwiftUI/UIKit) is the thin presentation layer over this.
@MainActor
public final class DashboardViewModel: ObservableObject {
    @Published public var templateCount = 0
    @Published public var partCount = 0
    @Published public var buildingCount = 0
    @Published public var districtCount = 0
    @Published public var recentTemplates: [TemplateDef] = []
    @Published public var isLoading = false
    @Published public var errorMessage: String?

    private let client: ServerClient
    private let recentLimit: Int

    public init(client: ServerClient, recentLimit: Int = 5) {
        self.client = client
        self.recentLimit = recentLimit
    }

    /// Thin aggregation over existing list endpoints (GET /templates|parts|districts, GET
    /// /buildings for its paginated `total`) rather than new dedicated count endpoints —
    /// these lists are authoring-scale (dozens), not building-scale (thousands), so counting
    /// the fetched array is cheap and avoids adding server surface for this screen alone.
    public func load() async {
        isLoading = true
        errorMessage = nil
        do {
            async let templates = client.listTemplates()
            async let parts = client.listParts()
            async let districts = client.listDistricts()
            async let buildingsPage = client.listBuildings(limit: 1)

            let (tpls, prts, dists, page) = try await (templates, parts, districts, buildingsPage)
            templateCount = tpls.count
            partCount = prts.count
            districtCount = dists.count
            buildingCount = page.total
            recentTemplates = Self.recentFrom(tpls, limit: recentLimit)
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    /// Best-effort "recent" ordering: the server doesn't track a last-modified timestamp per
    /// template (#345 scope — adding one is a separate change), so this takes the tail of
    /// GET /templates' list order (SQLite insertion order) and reverses it, newest-authored
    /// first among that proxy. Not a substitute for real recency once the server tracks it.
    nonisolated static func recentFrom(_ templates: [TemplateDef], limit: Int) -> [TemplateDef] {
        guard limit > 0 else { return [] }
        return Array(templates.suffix(limit).reversed())
    }
}

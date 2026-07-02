import Combine
import Foundation

/// State + publish for the Generation screen (#346, UX spec "Generation"): scope picker
/// (building/block/neighborhood/city) over POST /export/unity's scope param, plus a static
/// pipeline-stage list for "pipeline visibility" — the actual generation runs in Unity at
/// import time, so the server has no live per-stage progress to report; this is informational,
/// not a progress tracker. Deliberately UIKit-free (like DashboardViewModel) so it's testable
/// via `swift test` without an iOS Simulator.
@MainActor
public final class GenerationViewModel: ObservableObject {
    @Published public var scope: ExportScope = .city
    @Published public var osmIdText: String = ""
    @Published public var neighborhoodText: String = ""
    @Published public var isPublishing = false
    @Published public var publishResult: ExportResult?
    @Published public var errorMessage: String?

    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
    }

    /// The pipeline stages the UX spec's Generation diagram names, shown as a static,
    /// informational list — not a live progress tracker (see type doc).
    public nonisolated static let pipelineStages = [
        "OSM Footprint", "District Rules", "Template Selection", "Region Evaluation",
        "Asset Placement", "Color Assignment", "Building Generation", "Unity Export",
    ]

    /// Whether the current scope + its required input is publishable. `.block` is never
    /// publishable — the server accepts it but treats it identically to `.city` (no block-level
    /// spatial grouping exists in the data model yet), and offering a "successful" export that
    /// silently didn't narrow anything would be misleading rather than thin.
    public var canPublish: Bool {
        switch scope {
        case .building: return Self.parseOsmId(osmIdText) != nil
        case .neighborhood: return !neighborhoodText.trimmingCharacters(in: .whitespaces).isEmpty
        case .city: return true
        case .block: return false
        }
    }

    nonisolated static func parseOsmId(_ text: String) -> Int? {
        Int(text.trimmingCharacters(in: .whitespaces))
    }

    public func publish() async {
        guard canPublish else { return }
        isPublishing = true
        errorMessage = nil
        do {
            publishResult = try await client.publishToUnity(
                scope: scope,
                osmId: scope == .building ? Self.parseOsmId(osmIdText) : nil,
                neighborhood: scope == .neighborhood ? neighborhoodText : ""
            )
        } catch {
            errorMessage = error.localizedDescription
        }
        isPublishing = false
    }
}

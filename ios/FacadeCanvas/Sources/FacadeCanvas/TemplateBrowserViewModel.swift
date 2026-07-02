import Combine
import Foundation

/// State + load for the Template Browser (#337, UX spec "Templates" — primary screen, ~40-50%
/// of usage). Deliberately UIKit-free (like DashboardViewModel/GenerationViewModel) so it's
/// testable via `swift test` without an iOS Simulator.
@MainActor
public final class TemplateBrowserViewModel: ObservableObject {
    @Published public var templates: [TemplateDef] = []
    @Published public var isLoading = false
    @Published public var errorMessage: String?

    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
    }

    public func load() async {
        isLoading = true
        errorMessage = nil
        do {
            templates = try await client.listTemplates()
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    /// Card subtitle text. The UX spec's Template Browser wants "Name, Building Count, District
    /// Usage, Last Modified" — none of which the server tracks yet (no per-template usage/
    /// timestamp fields exist; same gap already documented on the Dashboard's Recent Work,
    /// #345). exact/rule counts stand in as the closest honest signal for "how built-out is
    /// this template" until that data exists.
    nonisolated static func cardSubtitle(_ template: TemplateDef) -> String {
        "\(template.exact.count) exact · \(template.rules.count) rules"
    }
}

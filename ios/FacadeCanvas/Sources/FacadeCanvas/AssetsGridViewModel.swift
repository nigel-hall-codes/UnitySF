import Combine
import Foundation

/// State + load for the Assets grid (#344, UX spec "Assets"): a visual PartDef grid replacing
/// the text list. Deliberately UIKit- and SwiftUI-free (matching FacadeCanvasTests.swift's
/// stated goal of testing "pure model / view-model logic (no PencilKit/SwiftUI)") so it's
/// testable via `swift test` without an iOS Simulator — the fallback color/icon lookups return
/// semantic string keys here; AssetsGridView.swift maps them to real SwiftUI types.
@MainActor
public final class AssetsGridViewModel: ObservableObject {
    @Published public var parts: [PartDef] = []
    @Published public var isLoading = false
    @Published public var errorMessage: String?

    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
    }

    /// Thumbnail source decision (#344, design #326 open question R2): a server-side GLB→PNG
    /// rasterizer vs Unity push, same model as building thumbnails (#318). Chose Unity push —
    /// no new server-side rendering component, fully reuses the already-shipped pattern, and is
    /// unit-testable server-side the same way building thumbnails already are. Until a part has
    /// one uploaded, the grid falls back to a colored rect (explicitly sanctioned by the issue).
    public func load() async {
        isLoading = true
        errorMessage = nil
        do {
            parts = try await client.listParts()
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    /// Colored-rect fallback swatch, keyed by category, shown until a part has a real Unity-
    /// pushed thumbnail (#344, explicitly sanctioned by the issue). Returns a semantic color
    /// name, not a `SwiftUI.Color`, to keep this file SwiftUI-free — AssetsGridView.swift maps
    /// the name to a real `Color`.
    nonisolated static func fallbackColorName(for category: String) -> String {
        switch category {
        case "Window": return "blue"
        case "Door": return "brown"
        case "Garage": return "gray"
        case "BayWindow": return "teal"
        case "Storefront": return "orange"
        case "Sign": return "red"
        case "Roof": return "indigo"
        default: return "secondary"
        }
    }

    // Only SF Symbol names with high confidence of existing are used here — this environment
    // has no way to render/verify SF Symbols, and an unknown name fails silently (blank icon,
    // not a build error), so a wrong-but-plausible guess would be an invisible bug.
    nonisolated static func fallbackIcon(for category: String) -> String {
        switch category {
        case "Window", "BayWindow": return "rectangle"
        case "Door": return "rectangle.portrait"
        case "Garage": return "door.garage.closed"
        case "Storefront": return "storefront"
        case "Sign": return "signpost.right"
        case "Roof": return "house"
        default: return "cube"
        }
    }
}

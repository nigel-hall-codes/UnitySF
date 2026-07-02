import Combine
import Foundation

/// State + load/save for the Districts browser + editor (#342, UX spec "District Editor").
/// Deliberately UIKit- and SwiftUI-free (matching the rest of this package's view-models) so
/// it's testable via `swift test` without an iOS Simulator.
@MainActor
public final class DistrictEditorViewModel: ObservableObject {
    @Published public var districts: [DistrictDef] = []
    @Published public var isLoading = false
    @Published public var isSaving = false
    @Published public var errorMessage: String?

    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
    }

    public func load() async {
        isLoading = true
        errorMessage = nil
        do {
            districts = try await client.listDistricts()
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    /// Upsert-by-id, matching POST /districts' server-side semantics (#341) — updates the local
    /// list in place on success so the browser reflects the save without a full reload.
    @discardableResult
    public func save(_ district: DistrictDef) async -> Bool {
        isSaving = true
        errorMessage = nil
        do {
            let saved = try await client.createDistrict(district)
            if let i = districts.firstIndex(where: { $0.id == saved.id }) {
                districts[i] = saved
            } else {
                districts.append(saved)
            }
            isSaving = false
            return true
        } catch {
            errorMessage = error.localizedDescription
            isSaving = false
            return false
        }
    }

    /// A fresh, blank district for the "+" (new district) flow — a real id is required by the
    /// server (DistrictDef.id has no default), so this can't be an empty DistrictDef; the editor
    /// disables Save until the user has typed a non-blank id (see DistrictsView).
    nonisolated static func blank() -> DistrictDef {
        DistrictDef(id: "")
    }
}

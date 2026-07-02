#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Top-level iPad authoring entry point (#301): a NavigationSplitView whose sidebar lists
/// buildings from GET /buildings (with neighborhood + type filters), whose content pane shows
/// one building's facts + facade picker, and whose detail pane is a FacadeCanvasView.
///
/// Requires iOS 16+ for NavigationSplitView.
@available(iOS 16, *)
public struct BuildingBrowserView: View {
    private let client: ServerClient
    @StateObject private var vm: BuildingBrowserViewModel
    @State private var showPublishConfirm = false
    @State private var publishResult: ExportResult?
    @State private var showPublishResult = false
    @State private var isPublishing = false
    @State private var publishError: String?

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: BuildingBrowserViewModel(client: client))
    }

    public var body: some View {
        NavigationSplitView {
            sidebar
        } content: {
            if let building = vm.selectedBuilding {
                BuildingDetailView(building: building, client: client, draftStore: vm.draftStore)
            } else {
                ContentUnavailableView("Select a building",
                                       systemImage: "building.2",
                                       description: Text("Choose a building from the sidebar to pick a facade and start authoring."))
            }
        } detail: {
            // FacadeCanvasView is pushed from BuildingDetailView via NavigationLink.
            ContentUnavailableView("Pick a facade",
                                   systemImage: "rectangle.on.rectangle",
                                   description: Text("Select a facade from the building panel."))
        }
        // Publish confirmation alert (D4 — deliberate operator action, not automatic on save).
        .confirmationDialog("Publish to Unity?",
                            isPresented: $showPublishConfirm,
                            titleVisibility: .visible) {
            Button("Publish", role: .destructive) { Task { await publish() } }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This materialises Assets/SFBuildingTemplates/ on the server. The Unity import is a separate manual step.")
        }
        // Publish result alert.
        .alert("Published", isPresented: $showPublishResult, presenting: publishResult) { _ in
            Button("OK") { }
        } message: { result in
            Text("Export v\(result.version) → \(result.outDir)\n\(result.parts) parts · \(result.templates) templates · \(result.signs) signs · \(result.facadeDecals) facade decals")
        }
        .alert("Publish failed", isPresented: Binding(get: { publishError != nil },
                                                      set: { if !$0 { publishError = nil } })) {
            Button("OK") { }
        } message: { Text(publishError ?? "") }
    }

    private func publish() async {
        isPublishing = true
        do {
            let result = try await client.publishToUnity()
            publishResult = result
            showPublishResult = true
        } catch {
            publishError = error.localizedDescription
        }
        isPublishing = false
    }

    // MARK: - Sidebar

    private var sidebar: some View {
        VStack(spacing: 0) {
            filterBar
            buildingList
        }
        .navigationTitle("Buildings")
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) {
                SyncStatusBadge(status: vm.syncStatus)
            }
            ToolbarItem(placement: .navigationBarTrailing) {
                if vm.isLoading || isPublishing {
                    ProgressView()
                } else {
                    Button {
                        showPublishConfirm = true
                    } label: {
                        Label("Publish", systemImage: "arrow.up.to.line.circle")
                    }
                }
            }
        }
        .task { await vm.load() }
        .refreshable { await vm.load() }
    }

    private var filterBar: some View {
        VStack(spacing: 6) {
            HStack {
                Image(systemName: "magnifyingglass").foregroundColor(.secondary)
                TextField("Neighborhood", text: $vm.filterNeighborhood)
                    .autocorrectionDisabled()
                    .submitLabel(.search)
                    .onSubmit { Task { await vm.load() } }
                if !vm.filterNeighborhood.isEmpty {
                    Button { vm.filterNeighborhood = ""; Task { await vm.load() } } label: {
                        Image(systemName: "xmark.circle.fill").foregroundColor(.secondary)
                    }
                }
            }
            HStack {
                Image(systemName: "tag").foregroundColor(.secondary)
                TextField("Building type", text: $vm.filterType)
                    .autocorrectionDisabled()
                    .submitLabel(.search)
                    .onSubmit { Task { await vm.load() } }
                if !vm.filterType.isEmpty {
                    Button { vm.filterType = ""; Task { await vm.load() } } label: {
                        Image(systemName: "xmark.circle.fill").foregroundColor(.secondary)
                    }
                }
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
        .background(Color(uiColor: .secondarySystemBackground))
    }

    private var buildingList: some View {
        List(selection: $vm.selectedBuilding) {
            ForEach(vm.buildings) { building in
                BuildingRowView(building: building)
                    .tag(building)
            }
            if vm.hasMore {
                ProgressView()
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .onAppear { Task { await vm.loadMore() } }
            }
        }
        .listStyle(.sidebar)
        .overlay {
            if vm.buildings.isEmpty && !vm.isLoading {
                ContentUnavailableView("No buildings",
                                       systemImage: "building.2.slash",
                                       description: Text(vm.errorMessage ?? "Try adjusting the filters."))
            }
        }
    }
}

// MARK: - BuildingRowView

private struct BuildingRowView: View {
    let building: BuildingFacts

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("OSM \(building.osm_id)")
                .font(.headline)
            HStack(spacing: 4) {
                if !building.building_type.isEmpty {
                    Text(building.building_type)
                        .font(.caption)
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(Color.blue.opacity(0.15))
                        .cornerRadius(4)
                }
                if !building.neighborhood.isEmpty {
                    Text(building.neighborhood)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            Text(String(format: "%.0f×%.0f m  %d fl",
                        building.width_m, building.depth_m, building.floor_count))
                .font(.caption2)
                .foregroundColor(.secondary)
        }
        .padding(.vertical, 2)
    }
}

// MARK: - BuildingDetailView

/// Shows the selected building's facts and a facade picker. Tapping a facade name pushes
/// a FacadeCanvasView into the detail column.
@available(iOS 16, *)
private struct BuildingDetailView: View {
    let building: BuildingFacts
    let client: ServerClient
    let draftStore: DraftStore

    var body: some View {
        List {
            Section("Facts") {
                factsGrid
            }
            Section("Facades") {
                facadeLinks
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle("OSM \(building.osm_id)")
        .navigationSubtitle(building.neighborhood)
    }

    private var factsGrid: some View {
        LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 8) {
            FactCell(label: "Type",      value: building.building_type.isEmpty ? "—" : building.building_type)
            FactCell(label: "Shape",     value: building.footprint_shape.isEmpty ? "—" : building.footprint_shape)
            FactCell(label: "Width",     value: String(format: "%.1f m", building.width_m))
            FactCell(label: "Depth",     value: String(format: "%.1f m", building.depth_m))
            FactCell(label: "Height",    value: String(format: "%.1f m", building.height_m))
            FactCell(label: "Floors",    value: "\(building.floor_count)")
            FactCell(label: "Street facades", value: "\(building.street_facades.count)")
            FactCell(label: "Facade h.", value: String(format: "%.1f m", building.facade_height_m))
        }
        .padding(.vertical, 4)
    }

    private var facadeLinks: some View {
        // Street facades (ranked by score) then cardinal faces.
        let facadeEntries = facadeList()
        return ForEach(facadeEntries, id: \.name) { entry in
            NavigationLink(value: entry) {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(entry.name).font(.body)
                        if let subtitle = entry.subtitle {
                            Text(subtitle).font(.caption).foregroundColor(.secondary)
                        }
                    }
                    Spacer()
                    if entry.isStreet {
                        Image(systemName: "star.fill")
                            .foregroundColor(.yellow)
                            .font(.caption)
                    }
                }
            }
        }
        .navigationDestination(for: FacadeEntry.self) { entry in
            FacadeCanvasView(viewModel: FacadeCanvasViewModel(
                osmId: building.osm_id,
                facade: entry.serverFacadeName,
                footprintHash: building.footprint_hash,
                client: client,
                draftStore: draftStore
            ))
            .navigationTitle(entry.name)
        }
    }

    private func facadeList() -> [FacadeEntry] {
        // The server stores one canvas per named facade slot. "Street" is the single slot for
        // the primary (highest-scored) street facade. Secondary facades (i > 0) share this
        // slot — the user authors the same canvas no matter which ranked entry they pick.
        // When the server gains per-edge-index canvas storage this mapping can be updated.
        var entries: [FacadeEntry] = building.street_facades.enumerated().map { i, sf in
            let shared = i > 0 ? " · shared canvas" : ""
            FacadeEntry(
                name: "Street \(i + 1) (\(sf.cardinalLabel))",
                serverFacadeName: "Street",
                subtitle: String(format: "Score %.2f · bearing %.0f°\(shared)", sf.score, sf.bearing_deg),
                isStreet: true
            )
        }
        entries += ["Front", "Back", "Left", "Right"].map {
            FacadeEntry(name: $0, serverFacadeName: $0, subtitle: nil, isStreet: false)
        }
        return entries
    }
}

private struct FactCell: View {
    let label: String
    let value: String
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundColor(.secondary)
            Text(value).font(.body)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Compact sync-status indicator shown in the sidebar toolbar.
private struct SyncStatusBadge: View {
    let status: DraftStore.SyncStatus
    var body: some View {
        switch status {
        case .synced:
            Label("Synced", systemImage: "checkmark.icloud")
                .font(.caption).foregroundColor(.secondary)
        case .pending(let count):
            Label("\(count) pending", systemImage: "arrow.triangle.2.circlepath")
                .font(.caption).foregroundColor(.orange)
        case .syncing:
            HStack(spacing: 4) { ProgressView().scaleEffect(0.7); Text("Syncing").font(.caption) }
        case .error(let msg):
            Label(msg, systemImage: "exclamationmark.icloud")
                .font(.caption).foregroundColor(.red)
        }
    }
}

/// Hashable/Codable wrapper for NavigationLink values (facade entries).
private struct FacadeEntry: Hashable {
    let name: String
    let serverFacadeName: String
    let subtitle: String?
    let isStreet: Bool
}

// MARK: - BuildingBrowserViewModel

@MainActor
final class BuildingBrowserViewModel: ObservableObject {
    @Published var buildings: [BuildingFacts] = []
    @Published var selectedBuilding: BuildingFacts?
    @Published var filterNeighborhood: String = ""
    @Published var filterType: String = ""
    @Published var isLoading = false
    @Published var errorMessage: String?
    @Published var total: Int = 0
    @Published var syncStatus: DraftStore.SyncStatus = .synced

    var hasMore: Bool { buildings.count < total }

    private let client: ServerClient
    let draftStore: DraftStore     // internal so BuildingDetailView can pass it to canvas VMs
    private let pageSize = 50

    init(client: ServerClient, draftStore: DraftStore = DraftStore()) {
        self.client = client
        self.draftStore = draftStore
        // Seed from local snapshot for offline start.
        Task { await self.loadFromSnapshot() }
    }

    private func loadFromSnapshot() async {
        let cached = await draftStore.buildings
        if !cached.isEmpty {
            buildings = cached
            total = cached.count
        }
        syncStatus = await draftStore.syncStatus
    }

    func load() async {
        isLoading = true
        errorMessage = nil
        do {
            let page = try await client.listBuildings(
                neighborhood: filterNeighborhood.isEmpty ? nil : filterNeighborhood,
                type: filterType.isEmpty ? nil : filterType,
                limit: pageSize,
                offset: 0
            )
            buildings = page.buildings
            total = page.total
            // Flush outbox now that we know the server is reachable.
            await draftStore.updateBuildings(page.buildings)
            await draftStore.flush(using: client)
        } catch {
            errorMessage = error.localizedDescription
            // Fall back to snapshot if network is unavailable.
            let cached = await draftStore.buildings
            if !cached.isEmpty { buildings = cached; total = cached.count }
        }
        syncStatus = await draftStore.syncStatus
        isLoading = false
    }

    func loadMore() async {
        guard hasMore, !isLoading else { return }
        isLoading = true
        do {
            let page = try await client.listBuildings(
                neighborhood: filterNeighborhood.isEmpty ? nil : filterNeighborhood,
                type: filterType.isEmpty ? nil : filterType,
                limit: pageSize,
                offset: buildings.count
            )
            buildings += page.buildings
            total = page.total
            await draftStore.updateBuildings(buildings)
        } catch {
            errorMessage = error.localizedDescription
        }
        syncStatus = await draftStore.syncStatus
        isLoading = false
    }
}
#endif

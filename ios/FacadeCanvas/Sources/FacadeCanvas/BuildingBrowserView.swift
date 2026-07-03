#if canImport(SwiftUI) && canImport(UIKit)
import Combine
import SwiftUI
import UIKit

/// The MVP Buildings tab (#365): a three-column iPad productivity-dashboard layout —
/// dark navy nav sidebar, filterable 4-column building card grid, right inspector panel.
/// One job: pick a building and open its customization canvas (browse → select →
/// customize → save). Templates, stats, bulk actions and export are intentionally absent.
///
/// Requires iOS 17+ for ContentUnavailableView.
@available(iOS 17, *)
public struct BuildingBrowserView: View {
    private let client: ServerClient
    @StateObject private var vm: BuildingBrowserViewModel
    @State private var navSelection: NavItem = .buildings
    @State private var customizeTarget: CustomizeTarget?
    @State private var showResetConfirm = false

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: BuildingBrowserViewModel(client: client))
    }

    public var body: some View {
        HStack(spacing: 0) {
            SidebarView(selection: $navSelection, districtCount: vm.districtCount)
            Group {
                switch navSelection {
                case .dashboard:
                    NavigationStack { DashboardView(client: client) }
                case .buildings:
                    buildingsPane
                case .settings:
                    settingsPane
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(MVPStyle.pageBackground)
            if navSelection == .buildings {
                InspectorPanel(
                    building: vm.selectedBuilding,
                    isCustomized: vm.selectedBuilding.map(vm.isCustomized) ?? false,
                    client: client,
                    onCustomize: { building in customizeTarget = CustomizeTarget(building: building) },
                    onReset: { showResetConfirm = true }
                )
            }
        }
        .task { await vm.initialLoad() }
        // Refresh the customized index whenever the canvas closes — the server is the
        // source of truth for whether the save actually landed.
        .fullScreenCover(item: $customizeTarget,
                         onDismiss: { Task { await vm.refreshCustomized() } }) { target in
            NavigationStack {
                FacadeCanvasView(viewModel: FacadeCanvasViewModel(
                    osmId: target.building.osm_id,
                    facade: target.building.primaryFacadeName,
                    footprintHash: target.building.footprint_hash,
                    client: client,
                    draftStore: vm.draftStore
                ))
                .navigationTitle(target.building.displayAddress)
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button("Done") { customizeTarget = nil }
                    }
                }
            }
        }
        .confirmationDialog("Reset to Generated?",
                            isPresented: $showResetConfirm,
                            titleVisibility: .visible) {
            Button("Reset", role: .destructive) { Task { await vm.resetSelected() } }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This removes everything drawn on this building. It renders from its generated look again.")
        }
        .alert("Something went wrong",
               isPresented: Binding(get: { vm.errorMessage != nil },
                                    set: { if !$0 { vm.errorMessage = nil } })) {
            Button("OK") { }
        } message: { Text(vm.errorMessage ?? "") }
    }

    // MARK: - Buildings pane (filter row + grid + pagination)

    private var buildingsPane: some View {
        VStack(spacing: 0) {
            filterRow
            grid
            if !vm.isSearching {
                paginationBar
            }
        }
    }

    private var filterRow: some View {
        HStack(spacing: 12) {
            HStack {
                Image(systemName: "magnifyingglass").foregroundColor(.secondary)
                TextField("Search address", text: $vm.searchText)
                    .autocorrectionDisabled()
                    .onSubmit { Task { await vm.searchChanged() } }
                    .onChange(of: vm.searchText) { _, _ in Task { await vm.searchChanged() } }
                if !vm.searchText.isEmpty {
                    Button { vm.searchText = "" } label: {
                        Image(systemName: "xmark.circle.fill").foregroundColor(.secondary)
                    }
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(filterFieldBackground)
            .frame(maxWidth: 340)

            filterMenu(label: vm.filterNeighborhood.isEmpty ? "Neighborhood" : vm.filterNeighborhood) {
                Button("All Neighborhoods") { vm.filterNeighborhood = ""; Task { await vm.applyFilters() } }
                ForEach(vm.neighborhoods, id: \.self) { n in
                    Button(n) { vm.filterNeighborhood = n; Task { await vm.applyFilters() } }
                }
            }

            filterMenu(label: vm.filterType.isEmpty ? "Type" : vm.filterType) {
                Button("All Types") { vm.filterType = ""; Task { await vm.applyFilters() } }
                ForEach(vm.buildingTypes.filter { !$0.isEmpty }, id: \.self) { t in
                    Button(t) { vm.filterType = t; Task { await vm.applyFilters() } }
                }
            }

            filterMenu(label: "More Filters", icon: "slider.horizontal.3") {
                Text("No additional filters yet")
            }

            Spacer()
            if vm.isLoading { ProgressView() }
        }
        .padding(16)
    }

    private func filterMenu(label: String, icon: String = "chevron.down",
                            @ViewBuilder content: () -> some View) -> some View {
        Menu(content: content) {
            HStack(spacing: 6) {
                Text(label)
                Image(systemName: icon).font(.caption)
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(filterFieldBackground)
        }
        .foregroundColor(.primary)
    }

    private var filterFieldBackground: some View {
        RoundedRectangle(cornerRadius: 10)
            .fill(Color.white)
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(MVPStyle.cardBorder))
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16), count: 4),
                      spacing: 16) {
                ForEach(vm.displayedBuildings) { building in
                    BuildingCardView(
                        building: building,
                        client: client,
                        isSelected: vm.selectedBuilding == building,
                        isCustomized: vm.isCustomized(building),
                        onCustomize: { customizeTarget = CustomizeTarget(building: building) },
                        onReset: {
                            vm.selectedBuilding = building
                            showResetConfirm = true
                        }
                    )
                    .onTapGesture { vm.selectedBuilding = building }
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 16)
        }
        .overlay {
            if vm.displayedBuildings.isEmpty && !vm.isLoading {
                ContentUnavailableView("No buildings",
                                       systemImage: "building.2",
                                       description: Text("Import buildings from a bake, or adjust the filters."))
            }
        }
        .refreshable { await vm.reload() }
    }

    private var paginationBar: some View {
        HStack {
            Text(vm.pageSummary)
                .font(.caption)
                .foregroundColor(.secondary)
            Spacer()
            Button { Task { await vm.loadPage(vm.pageIndex - 1) } } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(vm.pageIndex == 0 || vm.isLoading)
            Text("Page \(vm.pageIndex + 1) of \(vm.pageCount)")
                .font(.caption)
                .monospacedDigit()
            Button { Task { await vm.loadPage(vm.pageIndex + 1) } } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(vm.pageIndex + 1 >= vm.pageCount || vm.isLoading)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.white)
        .overlay(alignment: .top) { Divider() }
    }

    private var settingsPane: some View {
        VStack(spacing: 12) {
            Image(systemName: "gearshape").font(.largeTitle).foregroundColor(.secondary)
            Text("Settings").font(.title2.bold())
            Text("The server connection is configured at app launch.\nNothing to tweak here yet.")
                .multilineTextAlignment(.center)
                .foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Identifiable wrapper so fullScreenCover(item:) re-presents when the building changes.
private struct CustomizeTarget: Identifiable {
    let building: BuildingFacts
    var id: Int { building.osm_id }
}

private enum NavItem { case dashboard, buildings, settings }

// MARK: - Style

/// The MVP visual language (#365): calm off-white workspace, white cards with subtle
/// borders, dark navy nav, blue reserved for the active tab and primary actions.
enum MVPStyle {
    static let navy = Color(red: 0.10, green: 0.12, blue: 0.19)
    static let navyCard = Color.white.opacity(0.06)
    static let pageBackground = Color(red: 0.965, green: 0.97, blue: 0.98)
    static let cardBorder = Color.black.opacity(0.08)
    static let accent = Color(red: 0.15, green: 0.45, blue: 0.95)
    static let customizedGreen = Color(red: 0.13, green: 0.65, blue: 0.32)
}

// MARK: - Sidebar

@available(iOS 17, *)
private struct SidebarView: View {
    @Binding var selection: NavItem
    let districtCount: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "building.2.crop.circle.fill")
                    .font(.title2)
                    .foregroundColor(MVPStyle.accent)
                Text("SF Studio").font(.headline).foregroundColor(.white)
            }
            .padding(.horizontal, 16)
            .padding(.top, 20)
            .padding(.bottom, 12)

            navButton(.dashboard, label: "Dashboard", systemImage: "square.grid.2x2")
            navButton(.buildings, label: "Buildings", systemImage: "building.2")

            districtStatusCard
                .padding(.horizontal, 12)
                .padding(.top, 8)

            Spacer()

            navButton(.settings, label: "Settings", systemImage: "gearshape")
                .padding(.bottom, 16)
        }
        .frame(width: 230)
        .frame(maxHeight: .infinity)
        .background(MVPStyle.navy)
    }

    private func navButton(_ item: NavItem, label: String, systemImage: String) -> some View {
        Button {
            selection = item
        } label: {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                Text(label)
                Spacer()
            }
            .font(.subheadline.weight(selection == item ? .semibold : .regular))
            .foregroundColor(selection == item ? .white : .white.opacity(0.65))
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(
                RoundedRectangle(cornerRadius: 10)
                    .fill(selection == item ? MVPStyle.accent : .clear)
            )
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 12)
    }

    private var districtStatusCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label("Districts", systemImage: "map")
                .font(.caption)
                .foregroundColor(.white.opacity(0.65))
            Text(districtCount == 0 ? "None configured yet"
                                    : "\(districtCount) configured")
                .font(.subheadline.weight(.medium))
                .foregroundColor(.white)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(MVPStyle.navyCard))
    }
}

// MARK: - Building card

@available(iOS 17, *)
private struct BuildingCardView: View {
    let building: BuildingFacts
    let client: ServerClient
    let isSelected: Bool
    let isCustomized: Bool
    let onCustomize: () -> Void
    let onReset: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            BuildingThumbView(osmId: building.osm_id, client: client)
                .aspectRatio(4 / 3, contentMode: .fit)
                .clipShape(UnevenRoundedRectangle(topLeadingRadius: 12, topTrailingRadius: 12))
                .overlay(alignment: .topTrailing) { overflowMenu }
            VStack(alignment: .leading, spacing: 4) {
                Text(building.displayAddress)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                Text(subtitle)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                if isCustomized {
                    HStack(spacing: 4) {
                        Circle().fill(MVPStyle.customizedGreen).frame(width: 6, height: 6)
                        Text("Customized")
                            .font(.caption2.weight(.medium))
                            .foregroundColor(MVPStyle.customizedGreen)
                    }
                }
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color.white)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(isSelected ? MVPStyle.accent : MVPStyle.cardBorder,
                              lineWidth: isSelected ? 2 : 1)
        )
        .shadow(color: .black.opacity(0.05), radius: 4, y: 2)
        .contentShape(RoundedRectangle(cornerRadius: 12))
    }

    private var subtitle: String {
        let type = building.building_type.isEmpty ? "Building" : building.building_type
        return building.neighborhood.isEmpty ? type : "\(type) · \(building.neighborhood)"
    }

    private var overflowMenu: some View {
        Menu {
            Button("Customize", action: onCustomize)
            if isCustomized {
                Button("Reset to Generated", role: .destructive, action: onReset)
            }
        } label: {
            Image(systemName: "ellipsis")
                .font(.caption.bold())
                .foregroundColor(.primary)
                .padding(6)
                .background(Circle().fill(.white.opacity(0.9)))
        }
        .padding(6)
    }
}

/// Server-rendered building preview with a stable placeholder while it loads (or 404s).
/// The card's own @State is the cache — SwiftUI reuses the view across scroll while the
/// identity (osm_id) is unchanged.
private struct BuildingThumbView: View {
    let osmId: Int
    let client: ServerClient

    @State private var image: UIImage?
    @State private var didAttemptLoad = false

    var body: some View {
        ZStack {
            Rectangle().fill(Color(uiColor: .secondarySystemBackground))
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                Image(systemName: "building.2")
                    .font(.title)
                    .foregroundColor(.secondary)
            }
        }
        .clipped()
        .task(id: osmId) {
            guard !didAttemptLoad else { return }
            didAttemptLoad = true
            if let data = try? await client.fetchBuildingThumb(osmId: osmId) {
                image = UIImage(data: data)
            }
        }
    }
}

// MARK: - Inspector

@available(iOS 17, *)
private struct InspectorPanel: View {
    let building: BuildingFacts?
    let isCustomized: Bool
    let client: ServerClient
    let onCustomize: (BuildingFacts) -> Void
    let onReset: () -> Void

    private enum PreviewMode: String, CaseIterable { case street = "Street View", threeD = "3D View" }
    @State private var previewMode: PreviewMode = .threeD
    @State private var backdropImage: UIImage?

    var body: some View {
        Group {
            if let building {
                details(for: building)
            } else {
                ContentUnavailableView("Select a building",
                                       systemImage: "building.2",
                                       description: Text("Pick a card from the grid to see its details."))
            }
        }
        .frame(width: 320)
        .frame(maxHeight: .infinity)
        .background(Color.white)
        .overlay(alignment: .leading) { Divider() }
    }

    private func details(for building: BuildingFacts) -> some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(building.displayAddress).font(.title3.bold())
                        if isCustomized {
                            HStack(spacing: 4) {
                                Circle().fill(MVPStyle.customizedGreen).frame(width: 6, height: 6)
                                Text("Customized")
                                    .font(.caption.weight(.medium))
                                    .foregroundColor(MVPStyle.customizedGreen)
                            }
                        }
                    }

                    preview(for: building)

                    Picker("Preview", selection: $previewMode) {
                        ForEach(PreviewMode.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.segmented)

                    detailRows(for: building)
                }
                .padding(16)
            }
            actions(for: building)
        }
        // Reload the street backdrop when the selection changes.
        .task(id: building.osm_id) {
            backdropImage = nil
            if let data = try? await client.fetchBackdrop(osmId: building.osm_id,
                                                          facade: building.primaryFacadeName),
               let ui = UIImage(data: data) {
                backdropImage = ui
            }
        }
    }

    @ViewBuilder
    private func preview(for building: BuildingFacts) -> some View {
        Group {
            switch previewMode {
            case .threeD:
                BuildingThumbView(osmId: building.osm_id, client: client)
            case .street:
                if let backdropImage {
                    Image(uiImage: backdropImage).resizable().scaledToFill()
                } else {
                    ZStack {
                        Rectangle().fill(Color(uiColor: .secondarySystemBackground))
                        Text("No street view yet")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
            }
        }
        .aspectRatio(4 / 3, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(MVPStyle.cardBorder))
    }

    private func detailRows(for building: BuildingFacts) -> some View {
        VStack(spacing: 8) {
            detailRow("Address", building.displayAddress)
            detailRow("Neighborhood", building.neighborhood.isEmpty ? "—" : building.neighborhood)
            detailRow("Type", building.building_type.isEmpty ? "—" : building.building_type)
            detailRow("Floors", "\(building.floor_count)")
            detailRow("Footprint", String(format: "%.0f × %.0f m%@",
                                          building.width_m, building.depth_m,
                                          building.footprint_shape.isEmpty ? "" : " · \(building.footprint_shape)"))
        }
    }

    private func detailRow(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label).font(.caption).foregroundColor(.secondary)
            Spacer()
            Text(value).font(.caption.weight(.medium)).multilineTextAlignment(.trailing)
        }
    }

    private func actions(for building: BuildingFacts) -> some View {
        VStack(spacing: 8) {
            Button {
                onCustomize(building)
            } label: {
                Text("Customize Building")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
            }
            .buttonStyle(.borderedProminent)
            .tint(MVPStyle.accent)

            if isCustomized {
                Button(role: .destructive, action: onReset) {
                    Text("Reset to Generated")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
            }
        }
        .padding(16)
        .overlay(alignment: .top) { Divider() }
    }
}

// MARK: - BuildingBrowserViewModel

@MainActor
final class BuildingBrowserViewModel: ObservableObject {
    @Published var buildings: [BuildingFacts] = []       // current page, or the search pool
    @Published var selectedBuilding: BuildingFacts?
    @Published var searchText: String = ""
    @Published var filterNeighborhood: String = ""       // "" = all
    @Published var filterType: String = ""               // "" = all
    @Published var neighborhoods: [String] = []
    @Published var buildingTypes: [String] = []
    @Published var customizedIds: Set<Int> = []
    @Published var districtCount: Int = 0
    @Published var isLoading = false
    @Published var errorMessage: String?
    @Published var total: Int = 0
    @Published private(set) var pageIndex: Int = 0

    let pageSize = 24                                     // 6 rows of the 4-column grid
    /// Server max page size — the search pool is one fetch of this, filtered client-side
    /// (the server has no text-search param; #365 documents the address-data gap).
    private let searchPoolSize = 1000

    private let client: ServerClient
    let draftStore: DraftStore     // passed into canvas VMs so offline saves queue
    private var isSearchPool = false

    init(client: ServerClient, draftStore: DraftStore = DraftStore()) {
        self.client = client
        self.draftStore = draftStore
    }

    var pageCount: Int { max(1, (total + pageSize - 1) / pageSize) }
    var isSearching: Bool { !searchText.trimmingCharacters(in: .whitespaces).isEmpty }

    var pageSummary: String {
        guard total > 0 else { return "No buildings" }
        let first = pageIndex * pageSize + 1
        let last = min(total, first + buildings.count - 1)
        return "Showing \(first)–\(last) of \(total)"
    }

    /// The grid's contents: the current page, narrowed by the search text when present.
    var displayedBuildings: [BuildingFacts] {
        guard isSearching else { return buildings }
        let needle = searchText.trimmingCharacters(in: .whitespaces).lowercased()
        return buildings.filter {
            $0.displayAddress.lowercased().contains(needle)
                || $0.neighborhood.lowercased().contains(needle)
                || $0.building_type.lowercased().contains(needle)
        }
    }

    func isCustomized(_ building: BuildingFacts) -> Bool {
        customizedIds.contains(building.osm_id)
    }

    func initialLoad() async {
        // Seed from the offline snapshot so the grid isn't blank while the network runs.
        let cached = await draftStore.buildings
        if !cached.isEmpty && buildings.isEmpty {
            buildings = cached
            total = cached.count
        }
        await loadVocabularies()
        await loadPage(0)
        // Flush any queued canvas saves now that the server proved reachable.
        await draftStore.flush(using: client)
    }

    func reload() async {
        await loadPage(pageIndex)
        await refreshCustomized()
    }

    /// Filters changed: back to the first page (and rebuild the search pool if searching).
    func applyFilters() async {
        isSearchPool = false
        await searchChanged(forceReload: true)
    }

    /// Search text changed. Entering search swaps the page for a one-shot larger pool that
    /// `displayedBuildings` narrows live; clearing it restores server-side pagination.
    func searchChanged(forceReload: Bool = false) async {
        if isSearching {
            guard !isSearchPool || forceReload else { return }   // pool already loaded
            isLoading = true
            do {
                let page = try await client.listBuildings(
                    neighborhood: filterNeighborhood.isEmpty ? nil : filterNeighborhood,
                    type: filterType.isEmpty ? nil : filterType,
                    limit: searchPoolSize, offset: 0)
                buildings = page.buildings
                total = page.total
                isSearchPool = true
            } catch {
                errorMessage = error.localizedDescription
            }
            isLoading = false
        } else if isSearchPool || forceReload {
            isSearchPool = false
            await loadPage(0)
        }
    }

    func loadPage(_ index: Int) async {
        let index = max(0, index)
        isLoading = true
        do {
            let page = try await client.listBuildings(
                neighborhood: filterNeighborhood.isEmpty ? nil : filterNeighborhood,
                type: filterType.isEmpty ? nil : filterType,
                limit: pageSize, offset: index * pageSize)
            buildings = page.buildings
            total = page.total
            pageIndex = index
            isSearchPool = false
            mergeNeighborhoods(from: page.buildings)
            await draftStore.updateBuildings(page.buildings)
            await refreshCustomized()
        } catch {
            errorMessage = error.localizedDescription
            // Fall back to the snapshot if the network is unavailable.
            let cached = await draftStore.buildings
            if !cached.isEmpty && buildings.isEmpty {
                buildings = cached
                total = cached.count
            }
        }
        isLoading = false
    }

    func refreshCustomized() async {
        if let ids = try? await client.listCustomizedBuildingIds() {
            customizedIds = Set(ids)
        }
    }

    /// 'Reset to Generated' for the selected building: server delete, then badge removal.
    func resetSelected() async {
        guard let building = selectedBuilding else { return }
        do {
            try await client.resetBuilding(osmId: building.osm_id)
            customizedIds.remove(building.osm_id)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    /// GET /neighborhoods only lists palette-backed neighborhoods, which can be empty long
    /// before palettes are authored — so the dropdown also learns from every building page
    /// it sees. Union, sorted, so the menu stays stable as pages stream through.
    private func mergeNeighborhoods(from page: [BuildingFacts]) {
        let seen = Set(neighborhoods).union(page.map(\.neighborhood).filter { !$0.isEmpty })
        neighborhoods = seen.sorted()
    }

    private func loadVocabularies() async {
        if let infos = try? await client.listNeighborhoods() {
            neighborhoods = Set(neighborhoods)
                .union(infos.map(\.neighborhood).filter { !$0.isEmpty })
                .sorted()
        }
        if let types = try? await client.listBuildingTypes() {
            buildingTypes = types
        }
        if let districts = try? await client.listDistricts() {
            districtCount = districts.count
        }
    }
}
#endif

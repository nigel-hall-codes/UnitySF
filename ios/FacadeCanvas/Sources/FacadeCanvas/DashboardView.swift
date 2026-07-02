#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Project overview (#345, UX spec "Dashboard"): counts (Templates, Assets, Buildings,
/// Districts), a recently-authored templates list, and a "Generate District Preview"
/// shortcut. Requires iOS 16+ to match the rest of the app shell's NavigationSplitView usage.
@available(iOS 16, *)
public struct DashboardView: View {
    @StateObject private var vm: DashboardViewModel

    public init(client: ServerClient) {
        _vm = StateObject(wrappedValue: DashboardViewModel(client: client))
    }

    public var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                countsGrid
                recentWorkSection
                districtPreviewButton
            }
            .padding()
        }
        .navigationTitle("Dashboard")
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                if vm.isLoading { ProgressView() }
            }
        }
        .task { await vm.load() }
        .refreshable { await vm.load() }
        .alert("Couldn't load dashboard",
               isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
            Button("OK") { }
        } message: { Text(vm.errorMessage ?? "") }
    }

    private var countsGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 12)], spacing: 12) {
            CountCard(title: "Templates", count: vm.templateCount, systemImage: "square.stack.3d.up")
            CountCard(title: "Assets", count: vm.partCount, systemImage: "cube")
            CountCard(title: "Buildings", count: vm.buildingCount, systemImage: "building.2")
            CountCard(title: "Districts", count: vm.districtCount, systemImage: "map")
        }
    }

    private var recentWorkSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Recent Work").font(.headline)
            if vm.recentTemplates.isEmpty {
                if !vm.isLoading {
                    Text("No templates yet.").foregroundColor(.secondary)
                }
            } else {
                ForEach(vm.recentTemplates) { template in
                    HStack {
                        Image(systemName: "square.stack.3d.up").foregroundColor(.accentColor)
                        VStack(alignment: .leading) {
                            Text(template.displayName.isEmpty ? template.id : template.displayName)
                            Text(template.id).font(.caption).foregroundColor(.secondary)
                        }
                        Spacer()
                    }
                    .padding(.vertical, 4)
                    Divider()
                }
            }
        }
    }

    private var districtPreviewButton: some View {
        // Disabled until the Districts screen (#342) exists to preview into — a stub action
        // here would navigate nowhere real. Wire this up alongside #342.
        Button {
        } label: {
            Label("Generate District Preview", systemImage: "wand.and.stars")
        }
        .disabled(true)
        .help("Coming with the Districts screen (#342)")
    }
}

private struct CountCard: View {
    let title: String
    let count: Int
    let systemImage: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Image(systemName: systemImage).foregroundColor(.accentColor)
            Text("\(count)").font(.title).bold()
            Text(title).font(.caption).foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
        .background(Color(.secondarySystemBackground))
        .cornerRadius(12)
    }
}
#endif

#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Assets grid (#344, UX spec "Assets"): a visual PartDef grid — thumbnail, category — replacing
/// the text list `TemplateAuthorView`'s sidebar used for part selection. Tags and usage count
/// (from the UX spec's card description) aren't tracked server-side yet — same documented gap as
/// the Dashboard's Recent Work (#345) and the Template Browser's card subtitle (#337).
@available(iOS 17, *)
public struct AssetsGridView: View {
    @StateObject private var vm: AssetsGridViewModel
    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: AssetsGridViewModel(client: client))
    }

    public var body: some View {
        NavigationStack {
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 16)], spacing: 16) {
                    ForEach(vm.parts) { part in
                        AssetCard(part: part, client: client)
                    }
                }
                .padding()
            }
            .navigationTitle("Assets")
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    if vm.isLoading { ProgressView() }
                }
            }
            .overlay {
                if vm.parts.isEmpty && !vm.isLoading {
                    ContentUnavailableView("No assets", systemImage: "cube",
                                           description: Text("Author a part using Part Authoring to see it here."))
                }
            }
            .task { await vm.load() }
            .refreshable { await vm.load() }
            .alert("Couldn't load assets",
                   isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
                Button("OK") { }
            } message: { Text(vm.errorMessage ?? "") }
        }
    }
}

private struct AssetCard: View {
    let part: PartDef
    let client: ServerClient

    @State private var thumbImage: UIImage?
    @State private var didAttemptLoad = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            thumbnail
                .frame(height: 100)
                .frame(maxWidth: .infinity)
                .background(fallbackColor.opacity(0.2))
                .cornerRadius(8)
            Text(part.id).font(.subheadline).lineLimit(1)
            Text(part.category.isEmpty ? "Uncategorized" : part.category)
                .font(.caption).foregroundColor(.secondary)
        }
        .padding()
        .background(Color(.secondarySystemBackground))
        .cornerRadius(12)
        .task {
            // Load once per card appearance; the card's own @State is the cache — mirrors
            // BuildingBrowserView's per-row thumbnail loading pattern exactly.
            guard !didAttemptLoad else { return }
            didAttemptLoad = true
            if let data = try? await client.fetchPartThumb(partId: part.id) {
                thumbImage = UIImage(data: data)
            }
        }
    }

    @ViewBuilder
    private var thumbnail: some View {
        if let thumbImage {
            Image(uiImage: thumbImage).resizable().scaledToFit()
        } else {
            // Colored-rect fallback (#344, explicitly sanctioned by the issue): a category-keyed
            // swatch with a category glyph, distinguishable at a glance even with no thumb yet.
            Image(systemName: AssetsGridViewModel.fallbackIcon(for: part.category))
                .font(.title)
                .foregroundColor(fallbackColor)
        }
    }

    // Maps the view-model's SwiftUI-free semantic color name to a real Color — the lookup logic
    // itself lives in AssetsGridViewModel (nonisolated static, testable via swift test);
    // this is just the name->Color table, which needs SwiftUI and so stays view-side.
    private var fallbackColor: Color {
        switch AssetsGridViewModel.fallbackColorName(for: part.category) {
        case "blue": return .blue
        case "brown": return .brown
        case "gray": return .gray
        case "teal": return .teal
        case "orange": return .orange
        case "red": return .red
        case "indigo": return .indigo
        default: return .secondary
        }
    }
}
#endif

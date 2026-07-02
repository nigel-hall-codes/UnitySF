#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Template Browser (#337, UX spec "Templates"): cards opening a three-pane workspace. Replaces
/// the entry point TemplateAuthorView.swift used to be for browsing — TemplateAuthorView itself
/// is NOT deleted yet, since its form (compatibility/exact/rules editing) covers ground the new
/// Properties pane doesn't reimplement until #339 lands; both coexist until that migration.
@available(iOS 17, *)
public struct TemplateBrowserView: View {
    @StateObject private var vm: TemplateBrowserViewModel
    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: TemplateBrowserViewModel(client: client))
    }

    public var body: some View {
        NavigationStack {
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 220), spacing: 16)], spacing: 16) {
                    ForEach(vm.templates) { template in
                        NavigationLink {
                            TemplateWorkspaceView(template: template, client: client)
                        } label: {
                            TemplateCard(template: template)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding()
            }
            .navigationTitle("Templates")
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    if vm.isLoading { ProgressView() }
                }
            }
            .overlay {
                if vm.templates.isEmpty && !vm.isLoading {
                    ContentUnavailableView("No templates", systemImage: "square.stack.3d.up",
                                           description: Text("Author a template using Template Authoring to see it here."))
                }
            }
            .task { await vm.load() }
            .refreshable { await vm.load() }
            .alert("Couldn't load templates",
                   isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
                Button("OK") { }
            } message: { Text(vm.errorMessage ?? "") }
        }
    }
}

private struct TemplateCard: View {
    let template: TemplateDef

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Image(systemName: "square.stack.3d.up").font(.title2).foregroundColor(.accentColor)
            Text(template.displayName.isEmpty ? template.id : template.displayName)
                .font(.headline).lineLimit(1)
            Text(template.id).font(.caption).foregroundColor(.secondary).lineLimit(1)
            Text(TemplateBrowserViewModel.cardSubtitle(template))
                .font(.caption2).foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
        .background(Color(.secondarySystemBackground))
        .cornerRadius(12)
    }
}

/// Three-pane workspace (Layers | Facade Canvas | Properties) for one template — the UX spec's
/// primary authoring surface. Panes are navigational stubs for now, per #337's explicit scope
/// ("panes may stub initially"): zone drawing + the layers list (#338), the resolve-backed
/// variant preview canvas (#340), and the per-zone-type properties editor (#339) are separate
/// issues. This is the real three-pane shell, wired to a real template, that those issues fill in.
@available(iOS 17, *)
public struct TemplateWorkspaceView: View {
    let template: TemplateDef
    let client: ServerClient

    public init(template: TemplateDef, client: ServerClient) {
        self.template = template
        self.client = client
    }

    public var body: some View {
        NavigationSplitView {
            layersPane
        } content: {
            canvasPane
        } detail: {
            propertiesPane
        }
        .navigationTitle(template.displayName.isEmpty ? template.id : template.displayName)
    }

    private var layersPane: some View {
        // TemplateDef has no `zones` field on the Swift side yet (server-only since #335) —
        // real content lands with #338's zone model + drawing canvas.
        ContentUnavailableView("No layers yet", systemImage: "square.3.layers.3d",
                               description: Text("Zone drawing lands in #338."))
            .navigationTitle("Layers")
    }

    private var canvasPane: some View {
        ContentUnavailableView("Facade canvas", systemImage: "rectangle.on.rectangle",
                               description: Text("Zone drawing (#338) and variant preview (#340) land here."))
            .navigationTitle("Canvas")
    }

    private var propertiesPane: some View {
        ContentUnavailableView("Properties", systemImage: "slider.horizontal.3",
                               description: Text("Per-zone-type rule editing lands in #339."))
            .navigationTitle("Properties")
    }
}
#endif

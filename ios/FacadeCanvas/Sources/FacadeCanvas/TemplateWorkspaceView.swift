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
/// primary authoring surface. All three panes are real: Layers + Canvas (#338), Properties
/// (#339), plus a "Preview Variants" sheet (#340) resolving Generate-Variants schematics against
/// the saved template. Zones are edited locally and POSTed back to the server explicitly via
/// Save (matching FacadeCanvasView/BuildingBrowserView's existing explicit-save convention — no
/// silent autosave); Preview Variants saves first for the same reason (see its own doc comment).
@available(iOS 17, *)
public struct TemplateWorkspaceView: View {
    let template: TemplateDef
    let client: ServerClient
    @StateObject private var zoneVM: ZoneDrawingViewModel
    @State private var selectedZoneId: String?
    @State private var isSaving = false
    @State private var saveError: String?
    @State private var showVariationPreview = false
    @State private var isPreviewing = false

    public init(template: TemplateDef, client: ServerClient) {
        self.template = template
        self.client = client
        _zoneVM = StateObject(wrappedValue: ZoneDrawingViewModel(zones: template.zones))
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
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                if isSaving {
                    ProgressView()
                } else {
                    Button("Save") { Task { await save() } }
                }
            }
            ToolbarItem(placement: .navigationBarTrailing) {
                if isPreviewing {
                    ProgressView()
                } else {
                    Button { Task { await saveAndPreview() } } label: {
                        Label("Preview Variants", systemImage: "square.grid.3x3")
                    }
                    .disabled(isSaving)
                }
            }
        }
        .alert("Save failed", isPresented: Binding(get: { saveError != nil }, set: { if !$0 { saveError = nil } })) {
            Button("OK") { }
        } message: { Text(saveError ?? "") }
        // Variation Preview (#340) resolves whatever the SERVER has stored for this template id
        // — there's no "preview these unsaved local edits" endpoint — so this saves first, then
        // opens the strip, matching this view's existing explicit-Save convention (an implicit
        // save as a documented side effect of a deliberate button press, not a background
        // autosave) rather than showing a preview that's silently stale relative to the canvas.
        .sheet(isPresented: $showVariationPreview) {
            NavigationStack {
                VariationPreviewStripView(client: client, templateId: template.id)
                    .navigationTitle("Variants")
                    .toolbar {
                        ToolbarItem(placement: .navigationBarTrailing) {
                            Button("Done") { showVariationPreview = false }
                        }
                    }
            }
        }
    }

    private func saveAndPreview() async {
        isPreviewing = true
        await save()
        if saveError == nil { showVariationPreview = true }
        isPreviewing = false
    }

    private func save() async {
        isSaving = true
        var updated = template
        updated.zones = zoneVM.zones
        do {
            _ = try await client.createTemplate(updated)
        } catch {
            saveError = error.localizedDescription
        }
        isSaving = false
    }

    private var layersPane: some View {
        Group {
            if zoneVM.zones.isEmpty {
                ContentUnavailableView("No zones yet", systemImage: "square.3.layers.3d",
                                       description: Text("Pick a zone tool on the canvas and drag to draw one."))
            } else {
                List {
                    ForEach(zoneVM.zones) { zone in
                        LayerRow(zone: zone, isSelected: zone.id == selectedZoneId, vm: zoneVM)
                            .contentShape(Rectangle())
                            .onTapGesture { selectedZoneId = zone.id }
                    }
                }
                .listStyle(.plain)
            }
        }
        .navigationTitle("Layers")
    }

    private var canvasPane: some View {
        ZoneDrawingCanvasView(vm: zoneVM, selectedZoneId: $selectedZoneId)
            .navigationTitle("Canvas")
    }

    private var propertiesPane: some View {
        Group {
            if let selectedZoneId {
                PropertiesPaneView(zoneId: selectedZoneId, vm: zoneVM)
            } else {
                ContentUnavailableView("No zone selected", systemImage: "slider.horizontal.3",
                                       description: Text("Select a zone from the Layers pane or canvas to edit its rules."))
            }
        }
        .navigationTitle("Properties")
    }
}

/// One layers-pane row: hide/lock/rename/duplicate/delete (#338's explicit scope; delete added
/// alongside them — without it a mis-drawn zone can never be removed, and FacadeCanvasView's
/// own layer panel already supports delete on its image layers, so this isn't unrequested scope,
/// just parity with the sibling panel in this same package).
@available(iOS 17, *)
private struct LayerRow: View {
    let zone: Zone
    let isSelected: Bool
    @ObservedObject var vm: ZoneDrawingViewModel
    @State private var showRename = false
    @State private var renameText = ""

    var body: some View {
        HStack {
            Image(systemName: zone.isLocked ? "lock.fill" : "square.dashed")
                .foregroundColor(zone.isLocked ? .secondary : .accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text(zone.id).font(.subheadline)
                Text(zone.type).font(.caption2).foregroundColor(.secondary)
            }
            Spacer()
            if zone.isHidden {
                Image(systemName: "eye.slash").foregroundColor(.secondary)
            }
        }
        .opacity(zone.isHidden ? 0.5 : 1.0)
        .listRowBackground(isSelected ? Color.accentColor.opacity(0.15) : Color.clear)
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) { vm.delete(id: zone.id) } label: {
                Label("Delete", systemImage: "trash")
            }
            Button { vm.duplicate(id: zone.id) } label: {
                Label("Duplicate", systemImage: "plus.square.on.square")
            }
            .tint(.blue)
        }
        .swipeActions(edge: .leading) {
            Button { vm.toggleLocked(id: zone.id) } label: {
                Label(zone.isLocked ? "Unlock" : "Lock", systemImage: zone.isLocked ? "lock.open" : "lock")
            }
            .tint(.orange)
            Button { vm.toggleHidden(id: zone.id) } label: {
                Label(zone.isHidden ? "Show" : "Hide", systemImage: zone.isHidden ? "eye" : "eye.slash")
            }
            .tint(.gray)
        }
        .contextMenu {
            Button {
                renameText = zone.id
                showRename = true
            } label: {
                Label("Rename", systemImage: "pencil")
            }
            Button { vm.duplicate(id: zone.id) } label: {
                Label("Duplicate", systemImage: "plus.square.on.square")
            }
            Button {
                vm.toggleHidden(id: zone.id)
            } label: {
                Label(zone.isHidden ? "Show" : "Hide", systemImage: zone.isHidden ? "eye" : "eye.slash")
            }
            Button {
                vm.toggleLocked(id: zone.id)
            } label: {
                Label(zone.isLocked ? "Unlock" : "Lock", systemImage: zone.isLocked ? "lock.open" : "lock")
            }
            Button(role: .destructive) { vm.delete(id: zone.id) } label: {
                Label("Delete", systemImage: "trash")
            }
        }
        .alert("Rename Zone", isPresented: $showRename) {
            TextField("Zone name", text: $renameText)
            Button("Cancel", role: .cancel) { }
            Button("Rename") { vm.rename(id: zone.id, to: renameText) }
        }
    }
}
#endif

#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Districts browser (#342, UX spec "Districts"): list of districts opening an editor (template
/// distribution weights, palette, sign style) with a "Preview Street" sheet.
@available(iOS 17, *)
public struct DistrictsView: View {
    @StateObject private var vm: DistrictEditorViewModel
    private let client: ServerClient

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: DistrictEditorViewModel(client: client))
    }

    public var body: some View {
        NavigationStack {
            List {
                ForEach(vm.districts) { district in
                    NavigationLink {
                        DistrictEditorView(district: district, client: client, vm: vm)
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(district.name.isEmpty ? district.id : district.name)
                                .font(.headline)
                            Text("\(district.templateWeights.count) weighted templates · \(district.signStyle)")
                                .font(.caption).foregroundColor(.secondary)
                        }
                    }
                }
            }
            .navigationTitle("Districts")
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    if vm.isLoading { ProgressView() }
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    NavigationLink {
                        DistrictEditorView(district: DistrictEditorViewModel.blank(), client: client, vm: vm)
                    } label: {
                        Label("New District", systemImage: "plus")
                    }
                }
            }
            .overlay {
                if vm.districts.isEmpty && !vm.isLoading {
                    ContentUnavailableView("No districts", systemImage: "map",
                                           description: Text("Tap + to author a district."))
                }
            }
            .task { await vm.load() }
            .refreshable { await vm.load() }
            .alert("Couldn't load districts",
                   isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
                Button("OK") { }
            } message: { Text(vm.errorMessage ?? "") }
        }
    }
}

/// District editor: identity, neighborhoods (comma-separated, matching TemplateAuthorView's csv
/// convention), weighted template distribution, palette ref, sign style — plus a "Preview
/// Street" sheet. Unlike the Variation Preview (#340), Preview Street does NOT require saving
/// first: the weighted pick only needs the district's templateWeights, which this editor already
/// holds locally — no server round-trip needed before previewing unsaved edits.
@available(iOS 17, *)
private struct DistrictEditorView: View {
    let isNew: Bool
    let client: ServerClient
    @ObservedObject var vm: DistrictEditorViewModel

    @State private var id: String
    @State private var name: String
    @State private var neighborhoodsText: String
    @State private var weights: [TemplateWeight]
    @State private var palette: String
    @State private var signStyle: String
    @State private var showPreview = false

    init(district: DistrictDef, client: ServerClient, vm: DistrictEditorViewModel) {
        self.isNew = district.id.isEmpty
        self.client = client
        self.vm = vm
        _id = State(initialValue: district.id)
        _name = State(initialValue: district.name)
        _neighborhoodsText = State(initialValue: district.neighborhoods.joined(separator: ", "))
        _weights = State(initialValue: district.templateWeights)
        _palette = State(initialValue: district.palette)
        _signStyle = State(initialValue: district.signStyle.isEmpty ? "Modern" : district.signStyle)
    }

    private var currentDistrict: DistrictDef {
        let neighborhoods = neighborhoodsText.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        return DistrictDef(id: id, name: name, neighborhoods: neighborhoods,
                           templateWeights: weights, palette: palette, signStyle: signStyle)
    }

    var body: some View {
        Form {
            Section("Identity") {
                TextField("District id", text: $id)
                    .disabled(!isNew)
                    .autocorrectionDisabled()
                TextField("Name", text: $name)
            }
            Section("Neighborhoods") {
                TextField("Comma-separated, e.g. Mission, Bernal Heights", text: $neighborhoodsText)
                    .autocorrectionDisabled()
            }
            Section("Template Distribution (weighted)") {
                ForEach(weights.indices, id: \.self) { i in
                    HStack {
                        TextField("Template id", text: templateBinding(i))
                            .autocorrectionDisabled()
                        TextField("Weight", value: weightBinding(i), format: .number)
                            .frame(width: 56)
                            .keyboardType(.decimalPad)
                    }
                }
                .onDelete { weights.remove(atOffsets: $0) }
                Button {
                    weights.append(TemplateWeight(template: "", weight: 1))
                } label: {
                    Label("Add Template", systemImage: "plus")
                }
            }
            Section("Palette & Sign Style") {
                TextField("Palette (neighborhood ref)", text: $palette)
                    .autocorrectionDisabled()
                Picker("Sign style", selection: $signStyle) {
                    ForEach(["Modern", "Vintage", "Bilingual", "Tourist", "Mixed"], id: \.self) { Text($0) }
                }
            }
            Section {
                Button {
                    Task { await vm.save(currentDistrict) }
                } label: {
                    if vm.isSaving { ProgressView() } else { Text("Save") }
                }
                .disabled(id.trimmingCharacters(in: .whitespaces).isEmpty || vm.isSaving)

                Button {
                    showPreview = true
                } label: {
                    Label("Preview Street", systemImage: "building.2")
                }
                .disabled(weights.isEmpty)
            }
        }
        .navigationTitle(name.isEmpty ? (id.isEmpty ? "New District" : id) : name)
        .sheet(isPresented: $showPreview) {
            NavigationStack {
                DistrictPreviewStripView(client: client, district: currentDistrict)
                    .navigationTitle("Street Preview")
                    .toolbar {
                        ToolbarItem(placement: .navigationBarTrailing) {
                            Button("Done") { showPreview = false }
                        }
                    }
            }
        }
    }

    private func templateBinding(_ index: Int) -> Binding<String> {
        Binding(
            get: { index < weights.count ? weights[index].template : "" },
            set: { newValue in if index < weights.count { weights[index].template = newValue } }
        )
    }

    private func weightBinding(_ index: Int) -> Binding<Double> {
        Binding(
            get: { index < weights.count ? weights[index].weight : 1 },
            set: { newValue in if index < weights.count { weights[index].weight = newValue } }
        )
    }
}
#endif

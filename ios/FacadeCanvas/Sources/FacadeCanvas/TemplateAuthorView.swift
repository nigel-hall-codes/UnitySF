#if canImport(SwiftUI) && canImport(UIKit)
import Combine
import SwiftUI
import UIKit

/// Template & rule authoring for the iPad client (#303).
///
/// A TemplateDef defines how the procedural assembler places parts on a building:
///   - Compatibility bands filter which buildings the template applies to
///   - Exact placements pin a specific part at a facade UV position
///   - Procedural rules scatter parts with spacing/count randomization
///
/// Saves via POST /templates. Existing templates are browsable in the sidebar.
@available(iOS 17, *)
public struct TemplateAuthorView: View {
    private let client: ServerClient
    @StateObject private var vm: TemplateAuthorViewModel

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: TemplateAuthorViewModel(client: client))
    }

    public var body: some View {
        NavigationSplitView {
            templateList
        } detail: {
            authoringForm
        }
        .navigationTitle("Template Authoring")
        .task { await vm.load() }
    }

    // MARK: - Sidebar

    private var templateList: some View {
        List(vm.templates, selection: $vm.selectedId) { tpl in
            VStack(alignment: .leading, spacing: 2) {
                Text(tpl.displayName.isEmpty ? tpl.id : tpl.displayName)
                    .font(.headline).lineLimit(1)
                Text(tpl.id).font(.caption).foregroundColor(.secondary).lineLimit(1)
                Text("\(tpl.exact.count) exact · \(tpl.rules.count) rules")
                    .font(.caption2).foregroundColor(.secondary)
            }
            .tag(tpl.id)
            .padding(.vertical, 2)
        }
        .listStyle(.sidebar)
        .navigationTitle("Templates (\(vm.templates.count))")
        .overlay {
            if vm.templates.isEmpty && !vm.isLoading {
                ContentUnavailableView("No templates",
                                       systemImage: "square.grid.3x3",
                                       description: Text("Author a new template using the form."))
            }
        }
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                if vm.isLoading { ProgressView() }
            }
        }
        .refreshable { await vm.load() }
    }

    // MARK: - Authoring form

    private var authoringForm: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                identitySection
                compatibilitySection
                exactSection
                rulesSection
                roofSection
                submitSection
            }
            .padding()
        }
        .navigationTitle(vm.templateId.isEmpty ? "New Template" : vm.templateId)
    }

    // MARK: - Identity

    private var identitySection: some View {
        GroupBox("Identity") {
            VStack(alignment: .leading, spacing: 8) {
                tplTextField("Template ID", text: $vm.templateId, prompt: "e.g. brownstone_window_bay")
                    .autocorrectionDisabled().textInputAutocapitalization(.never)
                tplTextField("Display name", text: $vm.displayName, prompt: "e.g. Brownstone Bay Window")
            }
        }
    }

    // MARK: - Compatibility bands

    private var compatibilitySection: some View {
        GroupBox("Compatibility") {
            VStack(alignment: .leading, spacing: 10) {
                tplTextField("Neighborhoods (comma-separated)", text: $vm.compatNeighborhoods,
                             prompt: "SoMa, Mission, …")
                tplTextField("Building types (comma-separated)", text: $vm.compatTypes,
                             prompt: "residential, commercial, …")
                tplTextField("Footprint shapes (comma-separated)", text: $vm.compatShapes,
                             prompt: "rectangle, L-shape, …")

                HStack(spacing: 12) {
                    VStack(alignment: .leading) {
                        Text("Width range (m)").font(.caption).foregroundColor(.secondary)
                        HStack {
                            TextField("min", text: $vm.widthMin).keyboardType(.decimalPad)
                                .textFieldStyle(.roundedBorder)
                            Text("–")
                            TextField("max", text: $vm.widthMax).keyboardType(.decimalPad)
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                    VStack(alignment: .leading) {
                        Text("Depth range (m)").font(.caption).foregroundColor(.secondary)
                        HStack {
                            TextField("min", text: $vm.depthMin).keyboardType(.decimalPad)
                                .textFieldStyle(.roundedBorder)
                            Text("–")
                            TextField("max", text: $vm.depthMax).keyboardType(.decimalPad)
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                    VStack(alignment: .leading) {
                        Text("Floors").font(.caption).foregroundColor(.secondary)
                        HStack {
                            TextField("min", text: $vm.floorMin).keyboardType(.numberPad)
                                .textFieldStyle(.roundedBorder)
                            Text("–")
                            TextField("max", text: $vm.floorMax).keyboardType(.numberPad)
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                }
            }
        }
    }

    // MARK: - Exact placements

    private var exactSection: some View {
        GroupBox("Exact placements") {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(vm.exact.indices, id: \.self) { i in
                    ExactRow(placement: $vm.exact[i], parts: vm.parts,
                             onDelete: { vm.exact.remove(at: i) })
                    if i < vm.exact.count - 1 { Divider() }
                }
                Button {
                    vm.exact.append(ExactPlacement(
                        part: vm.parts.first?.id ?? "",
                        facade: "Front"
                    ))
                } label: {
                    Label("Add exact placement", systemImage: "plus.circle")
                }
                .font(.subheadline)
            }
        }
    }

    // MARK: - Procedural rules

    private var rulesSection: some View {
        GroupBox("Procedural rules") {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(vm.rules.indices, id: \.self) { i in
                    RuleRow(rule: $vm.rules[i], parts: vm.parts,
                            onDelete: { vm.rules.remove(at: i) })
                    if i < vm.rules.count - 1 { Divider() }
                }
                Button {
                    vm.rules.append(ProceduralRule(
                        part: vm.parts.first?.id ?? "",
                        facade: "Front"
                    ))
                } label: {
                    Label("Add procedural rule", systemImage: "plus.circle")
                }
                .font(.subheadline)
            }
        }
    }

    // MARK: - Roof parts

    private var roofSection: some View {
        GroupBox("Roof parts (part IDs)") {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(vm.roofParts.indices, id: \.self) { i in
                    HStack {
                        TextField("Part ID", text: $vm.roofParts[i])
                            .textFieldStyle(.roundedBorder)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                        Button { vm.roofParts.remove(at: i) } label: {
                            Image(systemName: "minus.circle").foregroundColor(.red)
                        }
                        .buttonStyle(.plain)
                    }
                }
                Button { vm.roofParts.append("") } label: {
                    Label("Add roof part", systemImage: "plus.circle")
                }
                .font(.subheadline)
            }
        }
    }

    // MARK: - Submit

    private var submitSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let err = vm.errorMessage {
                Label(err, systemImage: "exclamationmark.triangle").foregroundColor(.red)
                    .font(.caption)
            }
            if let ok = vm.successMessage {
                Label(ok, systemImage: "checkmark.circle").foregroundColor(.green).font(.caption)
            }
            Button {
                Task { await vm.save() }
            } label: {
                if vm.isSaving {
                    ProgressView().padding(.horizontal)
                } else {
                    Text("Save template to server").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(vm.templateId.isEmpty || vm.isSaving)
        }
    }

    // MARK: - Helpers

    private func tplTextField(_ label: String, text: Binding<String>, prompt: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundColor(.secondary)
            TextField(prompt, text: text).textFieldStyle(.roundedBorder)
        }
    }
}

// MARK: - ExactRow

@available(iOS 16, *)
private struct ExactRow: View {
    @Binding var placement: ExactPlacement
    let parts: [PartDef]
    let onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Part").font(.caption).foregroundColor(.secondary).frame(width: 36)
                if parts.isEmpty {
                    TextField("Part ID", text: $placement.part)
                        .textFieldStyle(.roundedBorder).autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                } else {
                    Picker("Part", selection: $placement.part) {
                        ForEach(parts) { Text($0.id).tag($0.id) }
                    }
                    .pickerStyle(.menu)
                }
                Spacer()
                Button(action: onDelete) {
                    Image(systemName: "minus.circle").foregroundColor(.red)
                }
                .buttonStyle(.plain)
            }
            HStack(spacing: 8) {
                facadeField
                numField("Floor", value: Binding(
                    get: { Double(placement.floor) },
                    set: { placement.floor = Int($0) }
                ), step: 1)
                numField("X", value: $placement.x, step: 0.1)
                numField("Y", value: $placement.y, step: 0.1)
                numField("Scale", value: $placement.scale, step: 0.1)
                numField("Rot°", value: $placement.rotation, step: 15)
            }
        }
        .padding(.vertical, 2)
    }

    private var facadeField: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Facade").font(.caption2).foregroundColor(.secondary)
            Picker("Facade", selection: $placement.facade) {
                ForEach(["Front", "Back", "Left", "Right", "Street"], id: \.self) { Text($0) }
            }
            .pickerStyle(.menu)
        }
    }

    private func numField(_ label: String, value: Binding<Double>, step: Double) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption2).foregroundColor(.secondary)
            Stepper(value: value, step: step) {
                Text(String(format: "%.2f", value.wrappedValue)).font(.caption).frame(minWidth: 36)
            }
        }
    }
}

// MARK: - RuleRow

@available(iOS 16, *)
private struct RuleRow: View {
    @Binding var rule: ProceduralRule
    let parts: [PartDef]
    let onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Part").font(.caption).foregroundColor(.secondary).frame(width: 36)
                if parts.isEmpty {
                    TextField("Part ID", text: $rule.part)
                        .textFieldStyle(.roundedBorder).autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                } else {
                    Picker("Part", selection: $rule.part) {
                        ForEach(parts) { Text($0.id).tag($0.id) }
                    }
                    .pickerStyle(.menu)
                }
                Spacer()
                Button(action: onDelete) {
                    Image(systemName: "minus.circle").foregroundColor(.red)
                }
                .buttonStyle(.plain)
            }
            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Facade").font(.caption2).foregroundColor(.secondary)
                    Picker("Facade", selection: $rule.facade) {
                        ForEach(["Front", "Back", "Left", "Right", "Street"], id: \.self) { Text($0) }
                    }
                    .pickerStyle(.menu)
                }
                numField("Prob", value: $rule.probability, step: 0.05, range: 0...1)
                numField("Spacing", value: $rule.repeatRule.spacingMeters, step: 0.5)
                numField("Min", value: Binding(
                    get: { Double(rule.repeatRule.countMin) },
                    set: { rule.repeatRule.countMin = max(0, Int($0)) }
                ), step: 1)
                numField("Max", value: Binding(
                    get: { Double(rule.repeatRule.countMax) },
                    set: { rule.repeatRule.countMax = max(0, Int($0)) }
                ), step: 1)
            }
            HStack(spacing: 8) {
                numField("FloorMin", value: Binding(
                    get: { Double(rule.floorRange.min) },
                    set: { rule.floorRange.min = Int($0) }
                ), step: 1)
                numField("FloorMax", value: Binding(
                    get: { Double(rule.floorRange.max) },
                    set: { rule.floorRange.max = Int($0) }
                ), step: 1)
                numField("EdgeMargin", value: $rule.constraints.edgeMargin, step: 0.1)
                numField("MinSpacing", value: $rule.constraints.minSpacingMeters, step: 0.5)
            }
        }
        .padding(.vertical, 2)
    }

    private func numField(_ label: String, value: Binding<Double>, step: Double,
                           range: ClosedRange<Double> = 0...1000) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption2).foregroundColor(.secondary)
            Stepper(value: value, in: range, step: step) {
                Text(String(format: "%.2f", value.wrappedValue)).font(.caption).frame(minWidth: 36)
            }
        }
    }
}

// MARK: - TemplateAuthorViewModel

@MainActor
final class TemplateAuthorViewModel: ObservableObject {
    @Published var templates: [TemplateDef] = []
    @Published var parts: [PartDef] = []
    @Published var selectedId: String?
    @Published var isLoading = false

    // Form fields
    @Published var templateId = ""
    @Published var displayName = ""

    // Compatibility
    @Published var compatNeighborhoods = ""
    @Published var compatTypes = ""
    @Published var compatShapes = ""
    @Published var widthMin = "0"; @Published var widthMax = "1000"
    @Published var depthMin = "0"; @Published var depthMax = "1000"
    @Published var floorMin = "1"; @Published var floorMax = "100"

    @Published var exact: [ExactPlacement] = []
    @Published var rules: [ProceduralRule] = []
    @Published var roofParts: [String] = []

    @Published var isSaving = false
    @Published var errorMessage: String?
    @Published var successMessage: String?

    private let client: ServerClient

    init(client: ServerClient) { self.client = client }

    func load() async {
        isLoading = true
        defer { isLoading = false }
        async let tpls = (try? await client.listTemplates()) ?? []
        async let pts = (try? await client.listParts()) ?? []
        (templates, parts) = await (tpls, pts)
    }

    func save() async {
        errorMessage = nil; successMessage = nil
        isSaving = true; defer { isSaving = false }

        func csv(_ s: String) -> [String] {
            s.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        }

        let compat = Compatibility(
            neighborhoods: csv(compatNeighborhoods),
            building_types: csv(compatTypes),
            footprint_shapes: csv(compatShapes),
            width_m: FloatRange(min: Double(widthMin) ?? 0, max: Double(widthMax) ?? 1000),
            depth_m: FloatRange(min: Double(depthMin) ?? 0, max: Double(depthMax) ?? 1000),
            floor_count: IntRange(min: Int(floorMin) ?? 1, max: Int(floorMax) ?? 100)
        )

        let tpl = TemplateDef(
            id: templateId,
            displayName: displayName,
            compatibility: compat,
            exact: exact,
            rules: rules,
            roofParts: roofParts.filter { !$0.isEmpty },
            version: 1
        )

        do {
            _ = try await client.createTemplate(tpl)
        } catch {
            errorMessage = "POST /templates failed: \(error.localizedDescription)"
            return
        }

        successMessage = "Saved '\(templateId)' (\(exact.count) exact, \(rules.count) rules)"
        await load()
    }
}
#endif

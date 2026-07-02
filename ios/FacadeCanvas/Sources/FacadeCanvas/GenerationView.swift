#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Generation screen (#346, UX spec "Generation"): scope picker (building/block/neighborhood/
/// city) + pipeline visibility + publish. Extends the existing whole-library publish flow
/// (BuildingBrowserView's toolbar button, unaffected — it still defaults to .city) with a
/// dedicated scoped-export surface.
@available(iOS 16, *)
public struct GenerationView: View {
    @StateObject private var vm: GenerationViewModel
    @State private var showPublishConfirm = false

    public init(client: ServerClient) {
        _vm = StateObject(wrappedValue: GenerationViewModel(client: client))
    }

    public var body: some View {
        Form {
            Section("Scope") {
                Picker("Scope", selection: $vm.scope) {
                    ForEach(ExportScope.allCases) { s in
                        Text(s.displayName).tag(s)
                    }
                }
                .pickerStyle(.segmented)

                switch vm.scope {
                case .building:
                    TextField("Building osm_id", text: $vm.osmIdText)
                        .keyboardType(.numberPad)
                case .neighborhood:
                    TextField("Neighborhood", text: $vm.neighborhoodText)
                        .autocorrectionDisabled()
                case .block:
                    Text("Block-level scoping isn't available yet — no block grouping exists in the data model. Pick City for now.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                case .city:
                    Text("Exports the entire library — every part, template, palette, and building override.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            Section("Pipeline") {
                ForEach(Array(GenerationViewModel.pipelineStages.enumerated()), id: \.offset) { index, stage in
                    HStack {
                        Text("\(index + 1).").foregroundColor(.secondary)
                        Text(stage)
                        Spacer()
                        if index == GenerationViewModel.pipelineStages.count - 1 {
                            Image(systemName: "arrow.up.to.line.circle").foregroundColor(.accentColor)
                        }
                    }
                }
                Text("Building Generation happens in Unity at import time — this list is informational, not a live progress tracker.")
                    .font(.caption2)
                    .foregroundColor(.secondary)
            }

            Section {
                Button {
                    showPublishConfirm = true
                } label: {
                    if vm.isPublishing {
                        ProgressView()
                    } else {
                        Label("Publish", systemImage: "arrow.up.to.line.circle")
                    }
                }
                .disabled(!vm.canPublish || vm.isPublishing)
            }
        }
        .navigationTitle("Generation")
        .confirmationDialog("Publish to Unity?", isPresented: $showPublishConfirm, titleVisibility: .visible) {
            Button("Publish", role: .destructive) { Task { await vm.publish() } }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This materialises Assets/SFBuildingTemplates/ on the server, scoped to \(vm.scope.displayName). The Unity import is a separate manual step.")
        }
        .alert("Published", isPresented: Binding(get: { vm.publishResult != nil }, set: { if !$0 { vm.publishResult = nil } }),
               presenting: vm.publishResult) { _ in
            Button("OK") { }
        } message: { result in
            Text("Export v\(result.version) (\(result.scope)) → \(result.outDir)\n\(result.parts) parts · \(result.templates) templates · \(result.overrides) overrides")
        }
        .alert("Publish failed", isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
            Button("OK") { }
        } message: { Text(vm.errorMessage ?? "") }
    }
}
#endif

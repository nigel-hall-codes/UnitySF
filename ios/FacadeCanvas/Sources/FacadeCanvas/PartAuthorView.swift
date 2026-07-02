#if canImport(SwiftUI) && canImport(UIKit)
import Combine
import SwiftUI
import UIKit
import PencilKit
import PhotosUI

/// Part authoring mode for the iPad client (design #266 D5: roles only, no colour picker).
///
/// Workflow:
///   1. Pick a reference screenshot of the facade element (stays on-device per D6 — never uploaded)
///   2. Trace over the reference with Pencil to confirm element bounds (visual guide only)
///   3. Enter physical size in metres + material role assignments (submesh → role name)
///   4. Tap Save → POST /parts (PartDef) + PUT /parts/{id}/glb (generated flat quad)
///
/// The generated GLB is a plain white quad in the XZ plane (Y-up). Material colours
/// come from the palette at runtime; the GLB stores geometry + UVs only.
@available(iOS 16, *)
public struct PartAuthorView: View {
    private let client: ServerClient
    @StateObject private var vm: PartAuthorViewModel

    public init(client: ServerClient) {
        self.client = client
        _vm = StateObject(wrappedValue: PartAuthorViewModel(client: client))
    }

    public var body: some View {
        NavigationSplitView {
            partList
        } detail: {
            authoringForm
        }
        .navigationTitle("Part Authoring")
        .task { await vm.loadParts() }
    }

    // MARK: - Sidebar: existing parts

    private var partList: some View {
        List(vm.parts, selection: $vm.selectedPartId) { part in
            VStack(alignment: .leading, spacing: 2) {
                Text(part.id).font(.headline).lineLimit(1)
                Text("\(part.category) · \(String(format: "%.1f×%.1f×%.1f m", part.size_m.w, part.size_m.h, part.size_m.d))")
                    .font(.caption).foregroundColor(.secondary)
                Text(part.roleSubmeshes.map { "s\($0.submesh):\($0.role)" }.joined(separator: ", "))
                    .font(.caption2).foregroundColor(.secondary).lineLimit(1)
            }
            .tag(part.id)
            .padding(.vertical, 2)
        }
        .listStyle(.sidebar)
        .navigationTitle("Parts (\(vm.parts.count))")
        .overlay {
            if vm.parts.isEmpty && !vm.isLoading {
                ContentUnavailableView("No parts yet",
                                       systemImage: "square.grid.3x3",
                                       description: Text("Author a new part using the form."))
            }
        }
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                if vm.isLoading { ProgressView() }
            }
        }
        .refreshable { await vm.loadParts() }
    }

    // MARK: - Authoring form

    private var authoringForm: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                identitySection
                sizeSection
                rolesSection
                referenceSection
                traceSection
                submitSection
            }
            .padding()
        }
        .navigationTitle(vm.partId.isEmpty ? "New Part" : vm.partId)
        .sheet(isPresented: $vm.showPhotoPicker) {
            PhotoPickerView(image: $vm.referenceImage)
        }
    }

    private var identitySection: some View {
        GroupBox("Identity") {
            VStack(alignment: .leading, spacing: 8) {
                LabeledTextField("Part ID", text: $vm.partId,
                                 prompt: "e.g. window_sunset_2x3")
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                HStack {
                    Text("Category").font(.subheadline)
                    Spacer()
                    Picker("Category", selection: $vm.category) {
                        ForEach(["Window", "Door", "Cornice", "Balcony", "Ledge",
                                 "Column", "Sign", "Awning", "Other"], id: \.self) { Text($0) }
                    }
                }
                HStack {
                    Text("Anchor").font(.subheadline)
                    Spacer()
                    Picker("Anchor", selection: $vm.anchor) {
                        ForEach(["BottomCenter", "TopCenter", "BottomLeft",
                                 "BottomRight", "Center"], id: \.self) { Text($0) }
                    }
                }
                LabeledTextField("Mount depth (m)", text: $vm.mountDepth,
                                 prompt: "0.08")
                    .keyboardType(.decimalPad)
            }
        }
    }

    private var sizeSection: some View {
        GroupBox("Size (metres)") {
            HStack(spacing: 12) {
                LabeledTextField("W", text: $vm.width, prompt: "1.2").keyboardType(.decimalPad)
                LabeledTextField("H", text: $vm.height, prompt: "1.6").keyboardType(.decimalPad)
                LabeledTextField("D", text: $vm.depth, prompt: "0.15").keyboardType(.decimalPad)
            }
        }
    }

    private var rolesSection: some View {
        GroupBox("Material roles") {
            VStack(alignment: .leading, spacing: 6) {
                // One row per GLB submesh (#328: bounded by PartGlbGenerator.submeshCount,
                // never free-form) — a role can't be assigned to a submesh that doesn't exist.
                ForEach(vm.roles.indices, id: \.self) { i in
                    HStack(spacing: 8) {
                        Text("s\(vm.roles[i].submesh)").font(.caption).foregroundColor(.secondary)
                            .frame(width: 24)
                        Picker("Role", selection: $vm.roles[i].role) {
                            ForEach(PartAuthorViewModel.materialRoles, id: \.self) { Text($0) }
                        }
                        .font(.body)
                    }
                }
            }
        }
    }

    private var referenceSection: some View {
        GroupBox("Reference photo (on-device only)") {
            HStack {
                if let img = vm.referenceImage {
                    Image(uiImage: img)
                        .resizable().scaledToFit()
                        .frame(height: 120)
                        .cornerRadius(8)
                } else {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color(uiColor: .secondarySystemBackground))
                        .frame(height: 120)
                        .overlay(Text("No photo").foregroundColor(.secondary))
                }
                Spacer()
                Button("Pick photo") { vm.showPhotoPicker = true }
                    .buttonStyle(.bordered)
            }
        }
    }

    private var traceSection: some View {
        GroupBox("Trace (visual guide — not used in GLB)") {
            PartTraceCanvas(referenceImage: vm.referenceImage, drawing: $vm.drawing)
                .aspectRatio(4 / 3, contentMode: .fit)
                .cornerRadius(8)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary, lineWidth: 0.5))
        }
    }

    private var submitSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let err = vm.errorMessage {
                Label(err, systemImage: "exclamationmark.triangle").foregroundColor(.red)
                    .font(.caption)
            }
            if let ok = vm.successMessage {
                Label(ok, systemImage: "checkmark.circle").foregroundColor(.green)
                    .font(.caption)
            }
            Button {
                Task { await vm.save() }
            } label: {
                if vm.isSaving {
                    ProgressView().padding(.horizontal)
                } else {
                    Text("Save part to server").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(vm.partId.isEmpty || vm.isSaving)
        }
    }
}

// MARK: - PartTraceCanvas

/// PencilKit canvas shown over the reference photo. The drawing is for the author's
/// visual reference only; the exported GLB geometry comes from the entered size values.
@available(iOS 16, *)
private struct PartTraceCanvas: UIViewRepresentable {
    let referenceImage: UIImage?
    @Binding var drawing: PKDrawing

    func makeUIView(context: Context) -> UIView {
        let root = UIView()
        root.backgroundColor = .secondarySystemBackground

        if let img = referenceImage {
            let iv = UIImageView(image: img)
            iv.contentMode = .scaleAspectFit
            iv.translatesAutoresizingMaskIntoConstraints = false
            root.addSubview(iv)
            NSLayoutConstraint.activate([
                iv.leadingAnchor.constraint(equalTo: root.leadingAnchor),
                iv.trailingAnchor.constraint(equalTo: root.trailingAnchor),
                iv.topAnchor.constraint(equalTo: root.topAnchor),
                iv.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            ])
        }

        let canvas = PKCanvasView()
        canvas.drawingPolicy = .anyInput
        canvas.backgroundColor = .clear
        canvas.isOpaque = false
        canvas.delegate = context.coordinator
        canvas.drawing = drawing
        canvas.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(canvas)
        NSLayoutConstraint.activate([
            canvas.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            canvas.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            canvas.topAnchor.constraint(equalTo: root.topAnchor),
            canvas.bottomAnchor.constraint(equalTo: root.bottomAnchor),
        ])
        context.coordinator.canvas = canvas
        return root
    }

    func updateUIView(_ view: UIView, context: Context) {
        // Attach tool picker after the view is in the window hierarchy.
        if let canvas = context.coordinator.canvas,
           !context.coordinator.pickerAttached, canvas.window != nil {
            context.coordinator.pickerAttached = true
            let picker = context.coordinator.toolPicker
            picker.setVisible(true, forFirstResponder: canvas)
            picker.addObserver(canvas)
            canvas.becomeFirstResponder()
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, PKCanvasViewDelegate {
        let parent: PartTraceCanvas
        let toolPicker = PKToolPicker()
        var pickerAttached = false
        weak var canvas: PKCanvasView?
        init(_ parent: PartTraceCanvas) { self.parent = parent }
        func canvasViewDrawingDidChange(_ canvasView: PKCanvasView) {
            parent.drawing = canvasView.drawing
        }
    }
}

// MARK: - PhotoPickerView

private struct PhotoPickerView: UIViewControllerRepresentable {
    @Binding var image: UIImage?
    @Environment(\.dismiss) private var dismiss

    func makeUIViewController(context: Context) -> PHPickerViewController {
        var config = PHPickerConfiguration()
        config.filter = .images
        config.selectionLimit = 1
        let picker = PHPickerViewController(configuration: config)
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ vc: PHPickerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, PHPickerViewControllerDelegate {
        let parent: PhotoPickerView
        init(_ parent: PhotoPickerView) { self.parent = parent }

        func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
            parent.dismiss()
            guard let provider = results.first?.itemProvider,
                  provider.canLoadObject(ofClass: UIImage.self) else { return }
            provider.loadObject(ofClass: UIImage.self) { obj, _ in
                DispatchQueue.main.async { self.parent.image = obj as? UIImage }
            }
        }
    }
}

// MARK: - LabeledTextField helper

private struct LabeledTextField: View {
    let label: String
    @Binding var text: String
    let prompt: String

    init(_ label: String, text: Binding<String>, prompt: String = "") {
        self.label = label
        self._text = text
        self.prompt = prompt
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundColor(.secondary)
            TextField(prompt, text: $text)
                .textFieldStyle(.roundedBorder)
        }
    }
}

// MARK: - PartAuthorViewModel

@MainActor
final class PartAuthorViewModel: ObservableObject {
    /// Fixed #266 role vocabulary (design D5: roles only, never free text or a colour picker).
    static let materialRoles = ["Base", "Accent1", "Accent2", "Glass", "Metal", "Sign"]

    @Published var parts: [PartDef] = []
    @Published var selectedPartId: String?
    @Published var isLoading = false

    // Form fields
    @Published var partId = ""
    @Published var category = "Window"
    @Published var width = "1.2"
    @Published var height = "1.6"
    @Published var depth = "0.15"
    @Published var anchor = "BottomCenter"
    @Published var mountDepth = "0.08"
    // One row per GLB submesh (#328) — never more rows than the generated GLB has submeshes.
    @Published var roles: [RoleSubmesh] = (0..<PartGlbGenerator.submeshCount).map {
        RoleSubmesh(submesh: $0, role: "Base")
    }
    @Published var referenceImage: UIImage?
    @Published var drawing = PKDrawing()
    @Published var showPhotoPicker = false

    // Status
    @Published var isSaving = false
    @Published var errorMessage: String?
    @Published var successMessage: String?

    private let client: ServerClient

    init(client: ServerClient) { self.client = client }

    func loadParts() async {
        isLoading = true
        defer { isLoading = false }
        do {
            parts = try await client.listParts()
        } catch {
            // Non-fatal — the part list is informational.
        }
    }

    func save() async {
        errorMessage = nil
        successMessage = nil
        isSaving = true
        defer { isSaving = false }

        let w = Double(width) ?? 1.2
        let h = Double(height) ?? 1.6
        let d = Double(depth) ?? 0.15
        let md = Double(mountDepth) ?? 0.08

        let part = PartDef(
            id: partId,
            category: category,
            glb: "Parts/\(partId).glb",
            size_m: SizeM(w: w, h: h, d: d),
            roleSubmeshes: roles.filter { !$0.role.isEmpty },
            anchor: anchor,
            mountDepth_m: md,
            version: 1
        )

        do {
            _ = try await client.createPart(part)
        } catch {
            errorMessage = "POST /parts failed: \(error.localizedDescription)"
            return
        }

        let glbData = PartGlbGenerator.generate(width: Float(w), height: Float(h))

        do {
            try await client.uploadPartGlb(partId: partId, data: glbData)
        } catch {
            errorMessage = "GLB upload failed: \(error.localizedDescription)"
            return
        }

        successMessage = "Saved '\(partId)' (\(String(format: "%.1f×%.1f×%.1f m", w, h, d)))"
        await loadParts()
    }
}
#endif

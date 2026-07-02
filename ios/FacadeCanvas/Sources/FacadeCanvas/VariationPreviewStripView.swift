#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Variation preview strip (#340, UX spec "Variation Preview"): Generate Variants 1..N via
/// POST /templates/{id}/resolve (#336) against a fixed synthetic building, rendered as small
/// 2.5D schematics — colored part rects + category glyphs (reusing AssetsGridViewModel's
/// category color/icon lookup, #344, rather than palette-role colors) — for evaluating variety
/// in seconds without a real building or a Unity round-trip.
@available(iOS 17, *)
struct VariationPreviewStripView: View {
    @StateObject private var vm: VariationPreviewViewModel

    init(client: ServerClient, templateId: String, seedCount: Int = 5) {
        _vm = StateObject(wrappedValue: VariationPreviewViewModel(
            client: client, templateId: templateId, seedCount: seedCount))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Variation Preview").font(.headline)
                Spacer()
                if vm.isLoading { ProgressView() }
                Button {
                    Task { await vm.load() }
                } label: {
                    Label("Regenerate", systemImage: "arrow.clockwise")
                }
                .disabled(vm.isLoading)
            }
            .padding(.horizontal)

            if vm.variants.isEmpty && !vm.isLoading {
                ContentUnavailableView("No variants yet", systemImage: "square.grid.3x3",
                                       description: Text("Tap Regenerate to resolve this template against a synthetic building."))
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 16) {
                        ForEach(Array(vm.variants.enumerated()), id: \.offset) { index, variant in
                            VariantSchematicView(seed: index + 1, facade: variant, vm: vm)
                        }
                    }
                    .padding(.horizontal)
                }
                .frame(height: 220)
            }
        }
        .task { await vm.load() }
        .alert("Couldn't generate variants",
               isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
            Button("OK") { }
        } message: { Text(vm.errorMessage ?? "") }
    }
}

private struct VariantSchematicView: View {
    let seed: Int
    let facade: ResolvedFacade
    @ObservedObject var vm: VariationPreviewViewModel

    var body: some View {
        VStack(spacing: 4) {
            Text("Seed \(seed)").font(.caption).foregroundColor(.secondary)
            GeometryReader { geo in
                ZStack {
                    Rectangle().fill(Color(white: 0.92))
                    ForEach(Array(facade.placements.enumerated()), id: \.offset) { _, placement in
                        placementRect(placement, canvasSize: geo.size)
                    }
                }
                .border(Color.secondary)
            }
            // .aspectRatio(_:contentMode:) takes CGFloat?, not Double — this is a computed
            // expression (not a literal), so it needs an explicit CGFloat(...) conversion;
            // Swift won't implicitly bridge Double -> CGFloat here.
            .aspectRatio(CGFloat(VariationPreviewViewModel.facadeWidthM / VariationPreviewViewModel.facadeHeightM),
                        contentMode: .fit)
            Text("\(facade.placements.count) placements").font(.caption2).foregroundColor(.secondary)
        }
        .frame(width: 160)
    }

    private func placementRect(_ placement: ResolvedPlacement, canvasSize: CGSize) -> some View {
        let category = vm.category(forPart: placement.part)
        let color = Self.color(forName: AssetsGridViewModel.fallbackColorName(for: category))
        let size = VariationPreviewViewModel.schematicSize(
            wM: placement.w_m, hM: placement.h_m,
            facadeWidthM: VariationPreviewViewModel.facadeWidthM,
            facadeHeightM: VariationPreviewViewModel.facadeHeightM)
        let overallY = VariationPreviewViewModel.schematicY(
            floor: placement.floor, y: placement.y, floorCount: VariationPreviewViewModel.floorCount)

        let w = CGFloat(size.w) * canvasSize.width
        let h = CGFloat(size.h) * canvasSize.height
        let cx = CGFloat(placement.x) * canvasSize.width
        // Bottom-origin facade UV -> top-down screen space, same Y-flip as ZoneOverlayView.
        let cy = (1 - CGFloat(overallY)) * canvasSize.height

        return RoundedRectangle(cornerRadius: 2)
            .fill(color.opacity(0.6))
            .overlay(RoundedRectangle(cornerRadius: 2).strokeBorder(color, lineWidth: 1))
            .overlay(
                // Only draw the category glyph when the rect is large enough to hold it —
                // avoids visual clutter on the small rects a schematic strip mostly has.
                Group {
                    if w >= 16 && h >= 16 {
                        Image(systemName: AssetsGridViewModel.fallbackIcon(for: category))
                            .font(.system(size: min(w, h) * 0.5))
                            .foregroundColor(.white)
                    }
                }
            )
            .frame(width: max(w, 4), height: max(h, 4))
            .position(x: cx, y: cy)
    }

    // Duplicates AssetCard.fallbackColor's name->Color table (AssetsGridView.swift) rather than
    // exposing that `private` mapping across files — same small, low-risk switch, kept local.
    private static func color(forName name: String) -> Color {
        switch name {
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

#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// District schematic street preview (#342, UX spec "District Preview" — "Generate Street: 10-50
/// buildings using district rules"): renders DistrictPreviewViewModel's weighted-picked buildings
/// as small 2.5D schematics, same rendering technique as VariationPreviewStripView (#340) — colored
/// part rects via AssetsGridViewModel's category color lookup.
@available(iOS 17, *)
struct DistrictPreviewStripView: View {
    @StateObject private var vm: DistrictPreviewViewModel
    private let district: DistrictDef

    init(client: ServerClient, district: DistrictDef, buildingCount: Int = 6) {
        self.district = district
        _vm = StateObject(wrappedValue: DistrictPreviewViewModel(client: client, buildingCount: buildingCount))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Street Preview").font(.headline)
                Spacer()
                if vm.isLoading { ProgressView() }
            }
            .padding(.horizontal)

            if district.templateWeights.isEmpty {
                ContentUnavailableView("No weighted templates", systemImage: "building.2",
                                       description: Text("Add at least one weighted template to preview a street."))
            } else if vm.buildings.isEmpty && !vm.isLoading {
                ContentUnavailableView("No buildings yet", systemImage: "building.2",
                                       description: Text("Pull to refresh to generate a street."))
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 12) {
                        ForEach(Array(vm.buildings.enumerated()), id: \.offset) { index, building in
                            DistrictBuildingSchematicView(index: index, building: building, vm: vm)
                        }
                    }
                    .padding(.horizontal)
                }
                .frame(height: 200)
            }
        }
        .task { await vm.load(district: district) }
        .refreshable { await vm.load(district: district) }
        .alert("Couldn't generate street",
               isPresented: Binding(get: { vm.errorMessage != nil }, set: { if !$0 { vm.errorMessage = nil } })) {
            Button("OK") { }
        } message: { Text(vm.errorMessage ?? "") }
    }
}

private struct DistrictBuildingSchematicView: View {
    let index: Int
    let building: DistrictPreviewViewModel.Building
    @ObservedObject var vm: DistrictPreviewViewModel

    var body: some View {
        VStack(spacing: 4) {
            Text(building.templateId).font(.caption2).foregroundColor(.secondary).lineLimit(1)
            GeometryReader { geo in
                ZStack {
                    Rectangle().fill(Color(white: 0.92))
                    ForEach(Array(building.facade.placements.enumerated()), id: \.offset) { _, placement in
                        placementRect(placement, canvasSize: geo.size)
                    }
                }
                .border(Color.secondary)
            }
            // Same explicit CGFloat(...) conversion VariationPreviewStripView needs — this is a
            // computed Double expression, not a literal, so it won't implicitly bridge.
            .aspectRatio(CGFloat(VariationPreviewViewModel.facadeWidthM / VariationPreviewViewModel.facadeHeightM),
                        contentMode: .fit)
        }
        .frame(width: 130)
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
        let cy = (1 - CGFloat(overallY)) * canvasSize.height

        return RoundedRectangle(cornerRadius: 2)
            .fill(color.opacity(0.6))
            .frame(width: max(w, 3), height: max(h, 3))
            .position(x: cx, y: cy)
    }

    // Duplicates the small name->Color table VariationPreviewStripView/AssetCard already have
    // (AssetsGridView.swift) rather than exposing that `private` mapping across files.
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

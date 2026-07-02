#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

/// Properties pane (#339, UX spec "Properties Panel"): editors for the selected zone's typed
/// rule payload — weighted allowed parts (the "Victorian A 50% / B 30% / C 20%" example),
/// count range, spacing, random offset, alignment, plus the zone's floor range.
///
/// The UX spec's Properties Panel description gives Window/Storefront/Sign each their own
/// field set (e.g. Storefront: Glass Ratio, Awning Chance, Corner Handling; Sign: Sign Types,
/// Max Width, AI Sign Generation) — but the actual server schema (`ZoneRules`, #335, already
/// shipped) is uniform across every zone type: allowedParts/countRange/spacingMeters/
/// randomOffset/alignment only, no type-specific fields. This pane edits the schema that
/// actually exists on the wire; the per-type fields the spec envisions would need a server-side
/// schema change first (out of scope for this iPad-only issue) — Window/Storefront/Sign all get
/// the same editor today, distinguished only by the zone's `type` label at the top.
@available(iOS 17, *)
struct PropertiesPaneView: View {
    let zoneId: String
    @ObservedObject var vm: ZoneDrawingViewModel

    private var zone: Zone? {
        vm.zones.first(where: { $0.id == zoneId })
    }

    var body: some View {
        if let zone {
            Form {
                Section("Zone") {
                    LabeledContent("Type", value: zone.type)
                    LabeledContent("Facade", value: zone.facade)
                }
                Section("Floor Range") {
                    Stepper("Min floor: \(zone.floorRange.min)", value: floorMinBinding, in: 0...50)
                    Stepper("Max floor: \(zone.floorRange.max)", value: floorMaxBinding, in: 0...50)
                }
                Section("Allowed Parts (weighted)") {
                    let percentages = ZoneDrawingViewModel.weightPercentages(zone.rules.allowedParts)
                    ForEach(Array(zone.rules.allowedParts.enumerated()), id: \.offset) { index, _ in
                        HStack {
                            TextField("Part id", text: partTextBinding(index))
                                .autocorrectionDisabled()
                            TextField("Weight", value: weightBinding(index), format: .number)
                                .frame(width: 56)
                                .keyboardType(.decimalPad)
                            Text(String(format: "%.0f%%", index < percentages.count ? percentages[index] : 0))
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .frame(width: 40, alignment: .trailing)
                        }
                    }
                    .onDelete { removeAllowedParts(at: $0) }
                    Button {
                        addAllowedPart()
                    } label: {
                        Label("Add Part", systemImage: "plus")
                    }
                }
                Section("Placement") {
                    Stepper("Count min: \(zone.rules.countRange.min)", value: countMinBinding, in: 0...50)
                    Stepper("Count max: \(zone.rules.countRange.max)", value: countMaxBinding, in: 0...50)
                    HStack {
                        Text("Spacing (m)")
                        Spacer()
                        TextField("min", value: spacingMinBinding, format: .number)
                            .frame(width: 56).keyboardType(.decimalPad)
                        Text("–")
                        TextField("max", value: spacingMaxBinding, format: .number)
                            .frame(width: 56).keyboardType(.decimalPad)
                    }
                    HStack {
                        Text("Random offset")
                        Spacer()
                        TextField("offset", value: randomOffsetBinding, format: .number)
                            .frame(width: 56).keyboardType(.decimalPad)
                    }
                    Picker("Alignment", selection: alignmentBinding) {
                        ForEach(["Grid", "FloorLine", "Free"], id: \.self) { Text($0) }
                    }
                }
            }
            .navigationTitle("Properties")
        } else {
            ContentUnavailableView("No zone selected", systemImage: "slider.horizontal.3",
                                   description: Text("Select a zone from the Layers pane or canvas to edit its rules."))
                .navigationTitle("Properties")
        }
    }

    // MARK: - Bindings
    // Each binding reads the current zone fresh from the view model and, on write, applies the
    // change to a local copy and writes the whole zone back via vm.updateZone — avoids editing
    // through a stale snapshot if the zone changed elsewhere (e.g. moved on the canvas) between
    // this binding being created and the user editing a field.

    private func binding<T>(_ get: @escaping (Zone) -> T, _ set: @escaping (inout Zone, T) -> Void) -> Binding<T> {
        Binding(
            get: { get(zone ?? Zone(id: zoneId)) },
            set: { newValue in
                guard var z = zone else { return }
                set(&z, newValue)
                vm.updateZone(z)
            }
        )
    }

    private var floorMinBinding: Binding<Int> { binding({ $0.floorRange.min }, { $0.floorRange.min = $1 }) }
    private var floorMaxBinding: Binding<Int> { binding({ $0.floorRange.max }, { $0.floorRange.max = $1 }) }
    private var countMinBinding: Binding<Int> { binding({ $0.rules.countRange.min }, { $0.rules.countRange.min = $1 }) }
    private var countMaxBinding: Binding<Int> { binding({ $0.rules.countRange.max }, { $0.rules.countRange.max = $1 }) }
    private var spacingMinBinding: Binding<Double> { binding({ $0.rules.spacingMeters.min }, { $0.rules.spacingMeters.min = $1 }) }
    private var spacingMaxBinding: Binding<Double> { binding({ $0.rules.spacingMeters.max }, { $0.rules.spacingMeters.max = $1 }) }
    private var randomOffsetBinding: Binding<Double> { binding({ $0.rules.randomOffset }, { $0.rules.randomOffset = $1 }) }
    private var alignmentBinding: Binding<String> { binding({ $0.rules.alignment }, { $0.rules.alignment = $1 }) }

    private func partTextBinding(_ index: Int) -> Binding<String> {
        binding(
            { z in index < z.rules.allowedParts.count ? z.rules.allowedParts[index].part : "" },
            { z, newValue in
                guard index < z.rules.allowedParts.count else { return }
                z.rules.allowedParts[index].part = newValue
            }
        )
    }

    private func weightBinding(_ index: Int) -> Binding<Double> {
        binding(
            { z in index < z.rules.allowedParts.count ? z.rules.allowedParts[index].weight : 1 },
            { z, newValue in
                guard index < z.rules.allowedParts.count else { return }
                z.rules.allowedParts[index].weight = newValue
            }
        )
    }

    private func addAllowedPart() {
        guard var z = zone else { return }
        z.rules.allowedParts.append(WeightedPart(part: "", weight: 1))
        vm.updateZone(z)
    }

    private func removeAllowedParts(at offsets: IndexSet) {
        guard var z = zone else { return }
        z.rules.allowedParts.remove(atOffsets: offsets)
        vm.updateZone(z)
    }
}
#endif

#if canImport(SwiftUI) && canImport(UIKit)
import SwiftUI
import UIKit

// A DragGesture's default (.local) coordinate space is relative to the specific view the
// gesture is attached to, NOT an ancestor — so a gesture attached to a small zone rect would
// report locations relative to that rect's own small frame, not the canvas, breaking the
// canvasWidth/canvasHeight normalization both gestures below rely on. Every DragGesture in this
// file is pinned to this SAME named ancestor space (applied to the ZStack in
// ZoneDrawingCanvasView.body) so `g.location` always means "canvas-relative," regardless of
// which child view the gesture is physically attached to.
private let zoneCanvasSpace = "ZoneDrawingCanvas"

/// Zone drawing canvas (#338, UX spec "Facade Canvas"): a tool palette (8 zone types) over a
/// unit-square drag-to-draw surface, reusing FacadeCanvasView's unit-square-UV drag/clamp
/// convention (see ZoneDrawingViewModel — same math as PlacedImageView, generalized from
/// move-only to draw-a-new-rect). Rect shapes only for now — polygon is a documented follow-up
/// (issue: "rect first, polygon later"). Also deliberately NOT PencilKit-based, unlike
/// FacadeCanvasView's freehand paint layer: a plain drag defines an exact rect directly, where
/// ink capture would need a separate fit-to-rect step for no added precision at this shape kind;
/// PencilKit-driven freeform capture is deferred to the polygon follow-up.
@available(iOS 17, *)
struct ZoneDrawingCanvasView: View {
    @ObservedObject var vm: ZoneDrawingViewModel
    @Binding var selectedZoneId: String?

    @State private var dragStart: CGPoint?
    @State private var dragCurrent: CGPoint?

    var body: some View {
        VStack(spacing: 0) {
            toolPalette
            GeometryReader { geo in
                ZStack {
                    Rectangle()
                        .fill(Color(white: 0.9))
                        .gesture(drawGesture(canvasSize: geo.size))
                    ForEach(vm.zones.filter { !$0.isHidden }) { zone in
                        ZoneOverlayView(zone: zone, canvasSize: geo.size,
                                       isSelected: zone.id == selectedZoneId, vm: vm)
                    }
                    if let start = dragStart, let current = dragCurrent {
                        drawingPreview(from: start, to: current)
                    }
                }
                .coordinateSpace(name: zoneCanvasSpace)
            }
            .aspectRatio(1, contentMode: .fit)   // the facade unit square
            .border(Color.secondary)
            .padding()
            statusBar
        }
    }

    private var toolPalette: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(ZoneType.allCases) { type in
                    Button {
                        vm.activeTool = (vm.activeTool == type) ? nil : type
                    } label: {
                        Text(type.rawValue)
                            .font(.caption)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(vm.activeTool == type ? Color.accentColor : Color(.secondarySystemBackground))
                            .foregroundColor(vm.activeTool == type ? .white : .primary)
                            .cornerRadius(8)
                    }
                }
            }
            .padding(.horizontal)
        }
        .padding(.vertical, 8)
    }

    private var statusBar: some View {
        HStack {
            if let tool = vm.activeTool {
                Text("Drag on the canvas to draw a \(tool.rawValue) zone").foregroundColor(.secondary)
            } else {
                Text("Pick a zone tool, or drag an existing zone to move it").foregroundColor(.secondary)
            }
            Spacer()
            Text("\(vm.zones.count) zone\(vm.zones.count == 1 ? "" : "s")").foregroundColor(.secondary)
        }
        .font(.caption)
        .padding(.horizontal)
        .padding(.bottom, 8)
    }

    private func drawGesture(canvasSize: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 4, coordinateSpace: .named(zoneCanvasSpace))
            .onChanged { g in
                guard vm.activeTool != nil else { return }
                // Unconditional, not "if dragStart == nil" — g.startLocation is stable for the
                // duration of one recognized gesture, but a PREVIOUS gesture that was cancelled
                // rather than cleanly ended (system interruption mid-drag) would leave dragStart
                // stale otherwise, since onEnded's reset never ran. Always taking this gesture's
                // own startLocation keeps drawing correct regardless of how the prior one exited.
                dragStart = g.startLocation
                dragCurrent = g.location
            }
            .onEnded { g in
                defer { dragStart = nil; dragCurrent = nil }
                guard let start = dragStart, let current = dragCurrent else { return }
                let id = vm.commitNewZone(
                    from: (x: Double(start.x), y: Double(start.y)),
                    to: (x: Double(current.x), y: Double(current.y)),
                    canvasWidth: Double(canvasSize.width), canvasHeight: Double(canvasSize.height))
                if id != nil { selectedZoneId = id }
            }
    }

    private func drawingPreview(from start: CGPoint, to current: CGPoint) -> some View {
        let x0 = min(start.x, current.x), x1 = max(start.x, current.x)
        let y0 = min(start.y, current.y), y1 = max(start.y, current.y)
        return Rectangle()
            .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [4]))
            .frame(width: max(x1 - x0, 1), height: max(y1 - y0, 1))
            .position(x: (x0 + x1) / 2, y: (y0 + y1) / 2)
            .allowsHitTesting(false)
    }
}

/// One drawn zone, rendered as a rect over the canvas — mirrors FacadeCanvasView's
/// PlacedImageView exactly (unit-square UV -> screen rect, bottom-origin Y-flip), but reads/
/// writes through ZoneDrawingViewModel's testable geometry instead of local math. Drag always
/// attaches; ZoneDrawingViewModel.moveZone itself no-ops for a locked zone (kept in the testable
/// business-logic layer rather than as conditional gesture-attachment in the view).
@available(iOS 17, *)
private struct ZoneOverlayView: View {
    let zone: Zone
    let canvasSize: CGSize
    let isSelected: Bool
    @ObservedObject var vm: ZoneDrawingViewModel

    var body: some View {
        let rect = ZoneDrawingViewModel.rect(of: zone)
        let w = CGFloat(rect[2] - rect[0]) * canvasSize.width
        let h = CGFloat(rect[3] - rect[1]) * canvasSize.height
        let cx = CGFloat((rect[0] + rect[2]) / 2) * canvasSize.width
        let cy = (1 - CGFloat((rect[1] + rect[3]) / 2)) * canvasSize.height

        return RoundedRectangle(cornerRadius: 4)
            .strokeBorder(isSelected ? Color.orange : Color.blue, lineWidth: isSelected ? 3 : 2)
            .background(Color.blue.opacity(0.08))
            .overlay(
                VStack(spacing: 2) {
                    if zone.isLocked { Image(systemName: "lock.fill").font(.caption2) }
                    Text(zone.id).font(.caption2).lineLimit(1)
                }
                .foregroundColor(.primary)
                .padding(2)
            )
            .frame(width: max(w, 24), height: max(h, 24))
            .position(x: cx, y: cy)
            // highPriorityGesture: without this, the background's draw-a-new-zone DragGesture
            // (attached to the Rectangle beneath these overlays) can win the gesture race for a
            // drag that starts on top of an existing zone — this forces the overlay's own drag
            // (move) to take priority over the background's (draw-new) whenever they overlap.
            // coordinateSpace: .named(zoneCanvasSpace) — NOT the default .local, which would
            // report g.location relative to this small zone rect's own frame rather than the
            // canvas, breaking the canvasWidth/canvasHeight normalization moveZone expects
            // (see the file-level comment on zoneCanvasSpace).
            .highPriorityGesture(
                DragGesture(coordinateSpace: .named(zoneCanvasSpace)).onChanged { g in
                    vm.moveZone(id: zone.id,
                               to: (x: Double(g.location.x), y: Double(g.location.y)),
                               canvasWidth: Double(canvasSize.width), canvasHeight: Double(canvasSize.height))
                }
            )
    }
}
#endif

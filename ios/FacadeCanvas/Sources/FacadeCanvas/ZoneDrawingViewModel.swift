import Combine
import Foundation

/// State + geometry for the zone drawing canvas + layers pane (#338, UX spec "Facade Canvas" /
/// "Layers Panel"). Deliberately UIKit- and SwiftUI-free (matching FacadeCanvasTests.swift's
/// stated "no PencilKit/SwiftUI" testing goal — same convention as AssetsGridViewModel) so the
/// drag-to-rect geometry and zone-list editing are testable via `swift test` without an iOS
/// Simulator. The view converts CGPoint/CGSize to plain Doubles at the boundary.
@MainActor
public final class ZoneDrawingViewModel: ObservableObject {
    @Published public var zones: [Zone] = []
    /// nil = pointer/select mode; a ZoneType = the tool that's active for the next drag-to-draw.
    @Published public var activeTool: ZoneType?

    public init(zones: [Zone] = []) {
        self.zones = zones
    }

    // MARK: - Drag-to-draw geometry (reuses PlacedImageView's unit-square UV + drag/clamp
    // convention from FacadeCanvasView.swift, generalized from move-only to draw-a-new-rect).

    /// Minimum zone size in normalized facade UV — guards against a tap/near-zero drag creating
    /// a degenerate, effectively-invisible zone.
    public static let minZoneSize = 0.02

    /// Two arbitrary drag points in an arbitrary "screen" space of the given size -> a
    /// normalized, clamped, minimum-sized facade-UV rect `[x0, y0, x1, y1]` (bottom-origin,
    /// matching ExactPlacement/PlacedImage's convention: screen is top-down, facade UV is
    /// bottom-up, hence the y-flip). Point order doesn't matter — the result is always
    /// min/max-sorted.
    nonisolated static func normalizedRect(
        from p1: (x: Double, y: Double), to p2: (x: Double, y: Double),
        canvasWidth: Double, canvasHeight: Double, minSize: Double = minZoneSize
    ) -> [Double] {
        guard canvasWidth > 0, canvasHeight > 0 else { return [0, 0, minSize, minSize] }
        let nx1 = p1.x / canvasWidth, ny1 = 1 - p1.y / canvasHeight
        let nx2 = p2.x / canvasWidth, ny2 = 1 - p2.y / canvasHeight
        var x0 = min(nx1, nx2), x1 = max(nx1, nx2)
        var y0 = min(ny1, ny2), y1 = max(ny1, ny2)
        if x1 - x0 < minSize {
            x1 = min(x0 + minSize, 1); x0 = max(x1 - minSize, 0)
        }
        if y1 - y0 < minSize {
            y1 = min(y0 + minSize, 1); y0 = max(y1 - minSize, 0)
        }
        x0 = min(max(x0, 0), 1); x1 = min(max(x1, 0), 1)
        y0 = min(max(y0, 0), 1); y1 = min(max(y1, 0), 1)
        return [x0, y0, x1, y1]
    }

    /// Reposition an existing rect by its new center point, keeping its size — same clamp-the-
    /// centre logic as `PlacedImageView.move(to:)` in FacadeCanvasView.swift.
    nonisolated static func movedRect(
        _ rect: [Double], centerTo point: (x: Double, y: Double),
        canvasWidth: Double, canvasHeight: Double
    ) -> [Double] {
        guard canvasWidth > 0, canvasHeight > 0, rect.count == 4 else { return rect }
        let halfW = (rect[2] - rect[0]) / 2
        let halfH = (rect[3] - rect[1]) / 2
        let nx = point.x / canvasWidth
        let ny = 1 - point.y / canvasHeight
        let cx = min(max(nx, halfW), 1 - halfW)
        let cy = min(max(ny, halfH), 1 - halfH)
        return [cx - halfW, cy - halfH, cx + halfW, cy + halfH]
    }

    // MARK: - Zone list editing

    /// Commit a new rect zone drawn with `activeTool`, if one is set. Returns the created zone's
    /// id, or nil if no tool was active (drag on an empty canvas with the pointer tool is a no-op).
    @discardableResult
    public func commitNewZone(from p1: (x: Double, y: Double), to p2: (x: Double, y: Double),
                               canvasWidth: Double, canvasHeight: Double) -> String? {
        guard let tool = activeTool else { return nil }
        let rect = Self.normalizedRect(from: p1, to: p2, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
        let id = Self.uniqueId(for: tool, existing: zones)
        let zone = Zone(id: id, type: tool.rawValue,
                        shape: ZoneShape(kind: "rect", points: Self.corners(of: rect)))
        zones.append(zone)
        return id
    }

    public func moveZone(id: String, to point: (x: Double, y: Double),
                          canvasWidth: Double, canvasHeight: Double) {
        guard let i = zones.firstIndex(where: { $0.id == id }), !zones[i].isLocked else { return }
        let newRect = Self.movedRect(Self.rect(of: zones[i]), centerTo: point,
                                     canvasWidth: canvasWidth, canvasHeight: canvasHeight)
        zones[i].shape.points = Self.corners(of: newRect)
    }

    public func toggleHidden(id: String) {
        guard let i = zones.firstIndex(where: { $0.id == id }) else { return }
        zones[i].isHidden.toggle()
    }

    public func toggleLocked(id: String) {
        guard let i = zones.firstIndex(where: { $0.id == id }) else { return }
        zones[i].isLocked.toggle()
    }

    public func rename(id: String, to newId: String) {
        let trimmed = newId.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed != id, !zones.contains(where: { $0.id == trimmed }),
              let i = zones.firstIndex(where: { $0.id == id }) else { return }
        zones[i].id = trimmed
    }

    public func duplicate(id: String) {
        guard let zone = zones.first(where: { $0.id == id }) else { return }
        var copy = zone
        copy.id = Self.uniqueId(base: id, existing: zones)
        zones.append(copy)
    }

    public func delete(id: String) {
        zones.removeAll { $0.id == id }
    }

    // MARK: - Helpers

    nonisolated static func rect(of zone: Zone) -> [Double] {
        let xs = zone.shape.points.map { $0[0] }
        let ys = zone.shape.points.map { $0.count > 1 ? $0[1] : 0 }
        guard let x0 = xs.min(), let x1 = xs.max(), let y0 = ys.min(), let y1 = ys.max() else {
            return [0, 0, minZoneSize, minZoneSize]
        }
        return [x0, y0, x1, y1]
    }

    nonisolated static func corners(of rect: [Double]) -> [[Double]] {
        guard rect.count == 4 else { return [] }
        let (x0, y0, x1, y1) = (rect[0], rect[1], rect[2], rect[3])
        return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
    }

    nonisolated static func uniqueId(for tool: ZoneType, existing: [Zone]) -> String {
        uniqueId(base: tool.rawValue.lowercased(), existing: existing)
    }

    nonisolated static func uniqueId(base: String, existing: [Zone]) -> String {
        let ids = Set(existing.map(\.id))
        if !ids.contains(base) { return base }
        var n = 2
        while ids.contains("\(base)_\(n)") { n += 1 }
        return "\(base)_\(n)"
    }
}

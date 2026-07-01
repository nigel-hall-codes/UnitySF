#if canImport(PencilKit)
import PencilKit
import UIKit

/// Convert PencilKit ink into the server's normalized-facade-UV stroke list (and back the colour).
/// The facade UV frame (design #276/#278): x left→right, y **bottom→top**; UIKit/PencilKit y is
/// top→down, so y is flipped here. Coordinates are normalized over the on-screen canvas size, so
/// the same drawing maps onto any real facade width/height in Unity (#280).
public enum StrokeConversion {

    /// Sample distance (points) along each PencilKit stroke path when discretising to a polyline.
    public static let sampleDistance: CGFloat = 4

    public static func strokes(from drawing: PKDrawing, canvasSize: CGSize) -> [Stroke] {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return [] }
        var out: [Stroke] = []
        for pk in drawing.strokes {
            var pts: [[Double]] = []
            for p in pk.path.interpolatedPoints(by: .distance(sampleDistance)) {
                let loc = p.location.applying(pk.transform)   // stroke transform → canvas space
                let x = Double(loc.x / canvasSize.width)
                let y = 1.0 - Double(loc.y / canvasSize.height)   // top-down → facade bottom-up
                pts.append([clamp01(x), clamp01(y)])
            }
            if pts.isEmpty { continue }
            let repWidth = Double(pk.path.first?.size.width ?? 6)
            out.append(Stroke(points: pts,
                              color: hex(pk.ink.color),
                              width: repWidth / Double(canvasSize.width)))
        }
        return out
    }

    public static func hex(_ color: UIColor) -> String {
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        color.getRed(&r, green: &g, blue: &b, alpha: &a)
        return String(format: "#%02X%02X%02X",
                      Int((r * 255).rounded()), Int((g * 255).rounded()), Int((b * 255).rounded()))
    }

    private static func clamp01(_ v: Double) -> Double { min(max(v, 0), 1) }
}
#endif

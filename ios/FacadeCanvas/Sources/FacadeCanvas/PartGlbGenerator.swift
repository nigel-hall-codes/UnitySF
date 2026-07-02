import Foundation

/// A 2D point in the trace's local space (mesh XY plane — X right, Y up), before the
/// bounds-normalization `PartGlbGenerator` applies to derive UVs and real-world scale.
public struct TracePoint: Equatable {
    public var x: Float
    public var y: Float
    public init(_ x: Float, _ y: Float) { self.x = x; self.y = y }
}

/// Generates a minimal binary glTF (GLB) for a part: the traced outline as a flat
/// polygon when one was drawn, or a flat placeholder quad otherwise.
///
/// The mesh sits in the XY plane, facing +Z (Unity's default forward direction), X
/// extending from the entered width and Y from the entered height (bottom-anchored).
/// The Unity importer reads material roles from PartDef.roleSubmeshes, not from the
/// GLB material — so an unlit white mesh is correct either way (D5 design: roles only,
/// never colors).
///
/// Traced-outline path (#327, closing a #266 headline gap — the pencil trace previously
/// only guided the author's eye and never became geometry):
///   1. Simplify the raw stroke (Ramer–Douglas–Peucker) to drop redundant near-collinear
///      points, treating the point sequence as a closed loop (last point implicitly
///      connects back to the first).
///   2. Cap the vertex count as a guard against pathological input (indices are UINT16).
///   3. Normalize winding to CCW (X-right/Y-up) so the mesh's hardcoded +Z normal is
///      correct regardless of which direction the part was traced.
///   4. Fan-triangulate from vertex 0 (design #266/#297 non-goal: flat polygon only — no
///      shallow-relief extrusion; see sdlc/#297/prior-art.md).
///   5. UVs come from the outline's own bounding box; positions are the same bounding
///      box rescaled to the entered width × height in metres.
/// Fewer than 3 points (no trace, or too degenerate to simplify to a polygon) falls back
/// to the original flat quad, unit-square UVs scaled the same way.
public enum PartGlbGenerator {

    /// Number of submeshes (glTF primitives) the generated GLB contains — always a
    /// single primitive, whether it's the traced polygon or the quad fallback. The
    /// part-authoring UI (#328) bounds its role-picker rows by this so a role can never
    /// be assigned to a submesh the GLB doesn't have.
    public static let submeshCount = 1

    public static func generate(width: Float, height: Float, outline: [TracePoint] = []) -> Data {
        let polygon = preparePolygon(outline) ?? [TracePoint(0, 0), TracePoint(1, 0), TracePoint(1, 1), TracePoint(0, 1)]
        return buildGlb(positions: scaledPositions(polygon, width: width, height: height),
                         uvs: uvsFromBounds(polygon),
                         indices: fanTriangleIndices(count: polygon.count))
    }

    // MARK: - Outline preparation

    private static func preparePolygon(_ outline: [TracePoint]) -> [TracePoint]? {
        guard outline.count >= 3 else { return nil }

        let bounds = boundingBox(outline)
        let span = max(bounds.maxX - bounds.minX, bounds.maxY - bounds.minY)
        guard span > 0 else { return nil }

        // Epsilon scaled to the trace's own extent so simplification behaves the same
        // regardless of the canvas's coordinate units.
        var simplified = douglasPeucker(outline, epsilon: span * 0.01)

        // Douglas-Peucker keeps both endpoints even when a closed trace ends where it
        // started — drop the duplicate so the fan-triangulation doesn't get a
        // degenerate final wedge.
        if simplified.count > 2, distance(simplified[0], simplified[simplified.count - 1]) < span * 0.01 {
            simplified.removeLast()
        }
        guard simplified.count >= 3 else { return nil }

        simplified = capVertexCount(simplified, limit: 255)

        if signedArea(simplified) < 0 { simplified.reverse() }

        return simplified
    }

    // MARK: - Ramer–Douglas–Peucker simplification

    private static func douglasPeucker(_ points: [TracePoint], epsilon: Float) -> [TracePoint] {
        guard points.count > 2 else { return points }

        var dmax: Float = 0
        var index = 0
        let end = points.count - 1
        for i in 1..<end {
            let d = perpendicularDistance(points[i], lineStart: points[0], lineEnd: points[end])
            if d > dmax { dmax = d; index = i }
        }

        if dmax > epsilon {
            let left = douglasPeucker(Array(points[0...index]), epsilon: epsilon)
            let right = douglasPeucker(Array(points[index...end]), epsilon: epsilon)
            return Array(left.dropLast()) + right
        }
        return [points[0], points[end]]
    }

    private static func perpendicularDistance(_ point: TracePoint, lineStart: TracePoint, lineEnd: TracePoint) -> Float {
        let dx = lineEnd.x - lineStart.x
        let dy = lineEnd.y - lineStart.y
        let lenSq = dx * dx + dy * dy
        if lenSq < 1e-12 { return distance(point, lineStart) }
        let num = abs(dy * point.x - dx * point.y + lineEnd.x * lineStart.y - lineEnd.y * lineStart.x)
        return num / lenSq.squareRoot()
    }

    private static func distance(_ a: TracePoint, _ b: TracePoint) -> Float {
        let dx = a.x - b.x, dy = a.y - b.y
        return (dx * dx + dy * dy).squareRoot()
    }

    // MARK: - Vertex-count guard

    private static func capVertexCount(_ points: [TracePoint], limit: Int) -> [TracePoint] {
        guard points.count > limit else { return points }
        var out: [TracePoint] = []
        out.reserveCapacity(limit)
        for i in 0..<limit {
            out.append(points[i * points.count / limit])
        }
        return out
    }

    // MARK: - Winding

    // Shoelace formula: positive for a CCW loop in an X-right/Y-up frame.
    private static func signedArea(_ points: [TracePoint]) -> Float {
        var sum: Float = 0
        let n = points.count
        for i in 0..<n {
            let a = points[i], b = points[(i + 1) % n]
            sum += a.x * b.y - b.x * a.y
        }
        return sum * 0.5
    }

    // MARK: - Bounds → UVs / real-world positions

    private static func boundingBox(_ points: [TracePoint]) -> (minX: Float, minY: Float, maxX: Float, maxY: Float) {
        var minX = points[0].x, maxX = points[0].x
        var minY = points[0].y, maxY = points[0].y
        for p in points {
            minX = min(minX, p.x); maxX = max(maxX, p.x)
            minY = min(minY, p.y); maxY = max(maxY, p.y)
        }
        return (minX, minY, maxX, maxY)
    }

    private static func uvsFromBounds(_ points: [TracePoint]) -> [(u: Float, v: Float)] {
        let b = boundingBox(points)
        let w = max(b.maxX - b.minX, 1e-6), h = max(b.maxY - b.minY, 1e-6)
        return points.map { p in ((p.x - b.minX) / w, (p.y - b.minY) / h) }
    }

    // Centred on X (matches the quad fallback's -hw...hw), bottom-anchored on Y (0...height).
    private static func scaledPositions(_ points: [TracePoint], width: Float, height: Float) -> [(x: Float, y: Float, z: Float)] {
        let b = boundingBox(points)
        let w = max(b.maxX - b.minX, 1e-6), h = max(b.maxY - b.minY, 1e-6)
        return points.map { p in
            let u = (p.x - b.minX) / w
            let v = (p.y - b.minY) / h
            return ((u - 0.5) * width, v * height, Float(0))
        }
    }

    // MARK: - Triangulation

    // Fan from vertex 0 — same convention the original quad used ([0,1,2, 0,2,3]).
    private static func fanTriangleIndices(count: Int) -> [UInt16] {
        guard count >= 3 else { return [] }
        var idx: [UInt16] = []
        idx.reserveCapacity((count - 2) * 3)
        for i in 1..<(count - 1) {
            idx.append(0); idx.append(UInt16(i)); idx.append(UInt16(i + 1))
        }
        return idx
    }

    // MARK: - GLB binary assembly

    private static func buildGlb(positions: [(x: Float, y: Float, z: Float)],
                                  uvs: [(u: Float, v: Float)],
                                  indices: [UInt16]) -> Data {
        let count = positions.count
        var bin = Data(capacity: count * 32 + indices.count * 2 + 16)

        func f(_ v: Float) {
            var bits = v.bitPattern.littleEndian
            withUnsafeBytes(of: &bits) { bin.append(contentsOf: $0) }
        }
        func u16(_ v: UInt16) {
            var bits = v.littleEndian
            withUnsafeBytes(of: &bits) { bin.append(contentsOf: $0) }
        }

        var minX = positions[0].x, maxX = positions[0].x
        var minY = positions[0].y, maxY = positions[0].y
        var minZ = positions[0].z, maxZ = positions[0].z

        // POSITION
        for p in positions {
            f(p.x); f(p.y); f(p.z)
            minX = min(minX, p.x); maxX = max(maxX, p.x)
            minY = min(minY, p.y); maxY = max(maxY, p.y)
            minZ = min(minZ, p.z); maxZ = max(maxZ, p.z)
        }
        let positionByteLength = count * 12

        // NORMAL: all +Z (flat mesh in the XY plane)
        let normalByteOffset = bin.count
        for _ in 0..<count { f(0); f(0); f(1) }
        let normalByteLength = count * 12

        // TEXCOORD_0
        let texByteOffset = bin.count
        for uv in uvs { f(uv.u); f(uv.v) }
        let texByteLength = count * 8

        // INDICES
        let idxByteOffset = bin.count
        for i in indices { u16(i) }
        let idxByteLength = indices.count * 2
        while bin.count % 4 != 0 { bin.append(0) }   // 4-byte alignment

        let binLen = bin.count

        let json = """
        {"asset":{"version":"2.0","generator":"FacadeCanvas/PartGlbGenerator"},\
        "scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],\
        "meshes":[{"name":"Part","primitives":[{"attributes":\
        {"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"mode":4}]}],\
        "accessors":[\
        {"bufferView":0,"byteOffset":0,"componentType":5126,"count":\(count),"type":"VEC3",\
        "min":[\(minX),\(minY),\(minZ)],"max":[\(maxX),\(maxY),\(maxZ)]},\
        {"bufferView":1,"byteOffset":0,"componentType":5126,"count":\(count),"type":"VEC3"},\
        {"bufferView":2,"byteOffset":0,"componentType":5126,"count":\(count),"type":"VEC2"},\
        {"bufferView":3,"byteOffset":0,"componentType":5123,"count":\(indices.count),"type":"SCALAR"}],\
        "bufferViews":[\
        {"buffer":0,"byteOffset":0,"byteLength":\(positionByteLength),"target":34962},\
        {"buffer":0,"byteOffset":\(normalByteOffset),"byteLength":\(normalByteLength),"target":34962},\
        {"buffer":0,"byteOffset":\(texByteOffset),"byteLength":\(texByteLength),"target":34962},\
        {"buffer":0,"byteOffset":\(idxByteOffset),"byteLength":\(idxByteLength),"target":34963}],\
        "buffers":[{"byteLength":\(binLen)}]}
        """

        guard var jsonData = json.data(using: .utf8) else { return Data() }
        while jsonData.count % 4 != 0 { jsonData.append(0x20) }  // pad with spaces

        let totalLen = 12 + 8 + jsonData.count + 8 + binLen
        var glb = Data(capacity: totalLen)

        func u32(_ v: UInt32) {
            var bits = v.littleEndian
            withUnsafeBytes(of: &bits) { glb.append(contentsOf: $0) }
        }

        u32(0x46546C67)             // "glTF" magic
        u32(2)                      // glTF version
        u32(UInt32(totalLen))

        u32(UInt32(jsonData.count)) // JSON chunk
        u32(0x4E4F534A)             // "JSON"
        glb.append(jsonData)

        u32(UInt32(binLen))         // BIN chunk
        u32(0x004E4942)             // "BIN\0"
        glb.append(bin)

        return glb
    }
}

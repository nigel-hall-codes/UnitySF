import Foundation

/// Generates a minimal binary glTF (GLB) containing a single flat quad mesh.
///
/// The quad faces +Z (Unity's default forward direction) and is centred at the origin
/// on the XY plane. Width extends along X, height along Y. The Unity importer reads
/// material roles from PartDef.roleSubmeshes, not from the GLB material — so a plain
/// unlit white quad is the correct placeholder (D5 design: roles only, never colors).
///
/// Binary layout (all little-endian):
///   [0..47]    POSITION  4 × VEC3<f32>
///   [48..95]   NORMAL    4 × VEC3<f32>
///   [96..127]  TEXCOORD  4 × VEC2<f32>
///   [128..139] INDICES   6 × UINT16
///   [140..143] padding   (4-byte alignment)
public enum PartGlbGenerator {

    public static func generate(width: Float, height: Float) -> Data {
        let hw = width / 2
        let hh = height

        var bin = Data(capacity: 144)

        func f(_ v: Float) {
            var bits = v.bitPattern.littleEndian
            withUnsafeBytes(of: &bits) { bin.append(contentsOf: $0) }
        }
        func u16(_ v: UInt16) {
            var bits = v.littleEndian
            withUnsafeBytes(of: &bits) { bin.append(contentsOf: $0) }
        }

        // POSITION: BL, BR, TR, TL (bottom-left origin, Y-up)
        f(-hw); f(0); f(0)
        f( hw); f(0); f(0)
        f( hw); f(hh); f(0)
        f(-hw); f(hh); f(0)

        // NORMAL: all +Z
        for _ in 0..<4 { f(0); f(0); f(1) }

        // TEXCOORD_0: (0,0) (1,0) (1,1) (0,1)
        f(0); f(0);  f(1); f(0);  f(1); f(1);  f(0); f(1)

        let idxByteOffset = bin.count   // 128
        for i: UInt16 in [0, 1, 2, 0, 2, 3] { u16(i) }   // 12 bytes
        while bin.count % 4 != 0 { bin.append(0) }         // pad to 144

        let binLen = bin.count

        let json = """
        {"asset":{"version":"2.0","generator":"FacadeCanvas/PartGlbGenerator"},\
        "scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],\
        "meshes":[{"name":"Part","primitives":[{"attributes":\
        {"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"mode":4}]}],\
        "accessors":[\
        {"bufferView":0,"byteOffset":0,"componentType":5126,"count":4,"type":"VEC3",\
        "min":[\(-hw),0.0,0.0],"max":[\(hw),\(hh),0.0]},\
        {"bufferView":1,"byteOffset":0,"componentType":5126,"count":4,"type":"VEC3"},\
        {"bufferView":2,"byteOffset":0,"componentType":5126,"count":4,"type":"VEC2"},\
        {"bufferView":3,"byteOffset":0,"componentType":5123,"count":6,"type":"SCALAR"}],\
        "bufferViews":[\
        {"buffer":0,"byteOffset":0,"byteLength":48,"target":34962},\
        {"buffer":0,"byteOffset":48,"byteLength":48,"target":34962},\
        {"buffer":0,"byteOffset":96,"byteLength":32,"target":34962},\
        {"buffer":0,"byteOffset":\(idxByteOffset),"byteLength":12,"target":34963}],\
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

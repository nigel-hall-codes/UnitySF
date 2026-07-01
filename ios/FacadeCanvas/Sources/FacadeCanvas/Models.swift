import Foundation

// Wire models mirroring the Home PC Server's shapes (server/app/models.py) so the canvas
// round-trips through POST/GET /canvas and POST /ai/signs/generate. Property names match the
// server JSON keys exactly (osm_id, footprint_hash, mountDepth_m, …) so no CodingKeys are needed
// and a drift can't silently break the Unity import (#280/#281 consume these on export).

public struct Stroke: Codable, Equatable {
    public var points: [[Double]]     // [[x, y], …] normalized facade UV (x L→R, y bottom→top)
    public var color: String          // "#RRGGBB"
    public var width: Double          // normalized stroke width

    public init(points: [[Double]] = [], color: String = "#000000", width: Double = 0.01) {
        self.points = points; self.color = color; self.width = width
    }
}

public struct CanvasLayer: Codable, Equatable {
    public var kind: String           // "paint" | "image"
    public var layer: Int             // z-order within the facade (paint usually lowest)
    public var mountDepth_m: Double
    public var strokes: [Stroke]      // paint layers
    public var rect: [Double]         // image layers: normalized [x0, y0, x1, y1]
    public var texture: String        // image layers: existing PNG ref, e.g. Signs/<id>.png
    public var signAsset: String      // image layers: optional link to a #275 sign asset

    public init(kind: String = "paint", layer: Int = 0, mountDepth_m: Double = 0.02,
                strokes: [Stroke] = [], rect: [Double] = [0, 0, 1, 1],
                texture: String = "", signAsset: String = "") {
        self.kind = kind; self.layer = layer; self.mountDepth_m = mountDepth_m
        self.strokes = strokes; self.rect = rect; self.texture = texture; self.signAsset = signAsset
    }

    public static func paint(_ strokes: [Stroke], layer: Int = 0, mountDepth_m: Double = 0.02) -> CanvasLayer {
        CanvasLayer(kind: "paint", layer: layer, mountDepth_m: mountDepth_m, strokes: strokes)
    }

    public static func image(rect: [Double], texture: String, signAsset: String = "",
                             layer: Int = 1, mountDepth_m: Double = 0.03) -> CanvasLayer {
        CanvasLayer(kind: "image", layer: layer, mountDepth_m: mountDepth_m,
                    rect: rect, texture: texture, signAsset: signAsset)
    }
}

public struct FacadeCanvas: Codable, Equatable {
    public var osm_id: Int
    public var facade: String
    public var footprint_hash: String
    public var layers: [CanvasLayer]
    public var version: Int

    public init(osm_id: Int, facade: String = "Front", footprint_hash: String = "",
                layers: [CanvasLayer] = [], version: Int = 1) {
        self.osm_id = osm_id; self.facade = facade; self.footprint_hash = footprint_hash
        self.layers = layers; self.version = version
    }
}

// --- AI signs (server-mediated; the iPad never calls a provider directly) ---

public struct SignRequest: Codable {
    public var businessType: String
    public var neighborhood: String
    public var text: String
    public var aspectRatio: String
    public var stylePreset: String
    public var provider: String

    public init(businessType: String = "", neighborhood: String = "", text: String = "",
                aspectRatio: String = "1:1", stylePreset: String = "", provider: String = "") {
        self.businessType = businessType; self.neighborhood = neighborhood; self.text = text
        self.aspectRatio = aspectRatio; self.stylePreset = stylePreset; self.provider = provider
    }
}

public struct SignDef: Codable, Equatable {
    public var signId: String
    public var png: String
    public var thumb: String
    public var provider: String
    public var version: Int
    public var businessType: String
    public var neighborhood: String
    public var text: String
    public var aspectRatio: String
    public var stylePreset: String
}

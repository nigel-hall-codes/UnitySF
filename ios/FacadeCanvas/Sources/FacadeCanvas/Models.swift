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

// --- Building browser (#301 — GET /buildings, GET /buildings/{osm_id}) ---

/// A ranked street facade edge from the bake sidecar; sorted by score descending.
public struct StreetFacade: Codable, Equatable, Hashable {
    public var edge_index: Int
    public var bearing_deg: Double
    public var street_osm_id: Int
    public var score: Double
    public var edge: [Double]   // [x0, z0, x1, z1] world-XZ

    public init(edge_index: Int = 0, bearing_deg: Double = 0, street_osm_id: Int = 0,
                score: Double = 0, edge: [Double] = []) {
        self.edge_index = edge_index; self.bearing_deg = bearing_deg
        self.street_osm_id = street_osm_id; self.score = score; self.edge = edge
    }

    /// Human-readable cardinal label derived from bearing (N/NE/E/SE/S/SW/W/NW).
    public var cardinalLabel: String {
        let dirs = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"]
        let idx = Int((bearing_deg + 22.5) / 45) % 8
        return dirs[idx]
    }
}

/// Full facts for one building — the server's GET /buildings/{osm_id} shape.
public struct BuildingFacts: Codable, Equatable, Identifiable, Hashable {
    public var osm_id: Int
    public var neighborhood: String
    public var building_type: String
    public var footprint_shape: String
    public var width_m: Double
    public var depth_m: Double
    public var height_m: Double
    public var floor_count: Int
    public var base_y: Double
    public var facade_height_m: Double
    public var street_facades: [StreetFacade]
    public var footprint_hash: String

    public var id: Int { osm_id }

    public init(osm_id: Int, neighborhood: String = "", building_type: String = "",
                footprint_shape: String = "", width_m: Double = 0, depth_m: Double = 0,
                height_m: Double = 0, floor_count: Int = 0, base_y: Double = 0,
                facade_height_m: Double = 0, street_facades: [StreetFacade] = [],
                footprint_hash: String = "") {
        self.osm_id = osm_id; self.neighborhood = neighborhood
        self.building_type = building_type; self.footprint_shape = footprint_shape
        self.width_m = width_m; self.depth_m = depth_m; self.height_m = height_m
        self.floor_count = floor_count; self.base_y = base_y
        self.facade_height_m = facade_height_m; self.street_facades = street_facades
        self.footprint_hash = footprint_hash
    }

    /// All addressable facade names in display order: street facades (ranked) then cardinal faces.
    public var allFacadeNames: [String] {
        let streetNames = street_facades.enumerated().map { i, sf in
            "Street \(i + 1) (\(sf.cardinalLabel))"
        }
        return streetNames + ["Front", "Back", "Left", "Right"]
    }
}

/// Paginated response from GET /buildings.
public struct BuildingPage: Codable {
    public var buildings: [BuildingFacts]
    public var total: Int
    public var limit: Int
    public var offset: Int

    public init(buildings: [BuildingFacts] = [], total: Int = 0, limit: Int = 50, offset: Int = 0) {
        self.buildings = buildings; self.total = total; self.limit = limit; self.offset = offset
    }
}

// --- Unity export / publish (#304 — POST /export/unity, design D4) ---

/// Body for POST /export/unity — outDir defaults to the server's env-configured target.
public struct ExportRequest: Codable {
    public var outDir: String
    public init(outDir: String = "") { self.outDir = outDir }
}

/// Result returned by POST /export/unity summarising what was materialised.
public struct ExportResult: Codable, Equatable {
    public var outDir: String
    public var version: Int
    public var parts: Int
    public var templates: Int
    public var palettes: Int
    public var overrides: Int
    public var glbsCopied: Int
    public var signs: Int
    public var facadeDecals: Int
    public var paintTextures: Int

    public init(outDir: String = "", version: Int = 0, parts: Int = 0, templates: Int = 0,
                palettes: Int = 0, overrides: Int = 0, glbsCopied: Int = 0,
                signs: Int = 0, facadeDecals: Int = 0, paintTextures: Int = 0) {
        self.outDir = outDir; self.version = version; self.parts = parts
        self.templates = templates; self.palettes = palettes; self.overrides = overrides
        self.glbsCopied = glbsCopied; self.signs = signs; self.facadeDecals = facadeDecals
        self.paintTextures = paintTextures
    }
}

// --- Template & rule authoring (#303 — GET/POST /templates) ---

public struct FloatRange: Codable, Equatable {
    public var min: Double; public var max: Double
    public init(min: Double = 0, max: Double = 0) { self.min = min; self.max = max }
}

public struct IntRange: Codable, Equatable {
    public var min: Int; public var max: Int
    public init(min: Int = 0, max: Int = 0) { self.min = min; self.max = max }
}

public struct Compatibility: Codable, Equatable {
    public var neighborhoods: [String]
    public var building_types: [String]
    public var footprint_shapes: [String]
    public var width_m: FloatRange
    public var depth_m: FloatRange
    public var floor_count: IntRange
    public init(neighborhoods: [String] = [], building_types: [String] = [],
                footprint_shapes: [String] = [],
                width_m: FloatRange = FloatRange(min: 0, max: 1000),
                depth_m: FloatRange = FloatRange(min: 0, max: 1000),
                floor_count: IntRange = IntRange(min: 1, max: 100)) {
        self.neighborhoods = neighborhoods; self.building_types = building_types
        self.footprint_shapes = footprint_shapes; self.width_m = width_m
        self.depth_m = depth_m; self.floor_count = floor_count
    }
}

/// A submesh material-role remap inside a placement. JSON key "from" maps directly
/// to Swift property `from` (not a keyword in property position).
public struct RolePair: Codable, Equatable {
    public var from: String
    public var to: String
    public init(from: String = "", to: String = "") { self.from = from; self.to = to }
}

public struct ExactPlacement: Codable, Equatable {
    public var part: String
    public var facade: String
    public var floor: Int
    public var x: Double; public var y: Double
    public var scale: Double; public var rotation: Double
    public var roles: [RolePair]
    public init(part: String = "", facade: String = "Front", floor: Int = 0,
                x: Double = 0.5, y: Double = 0.5, scale: Double = 1, rotation: Double = 0,
                roles: [RolePair] = []) {
        self.part = part; self.facade = facade; self.floor = floor
        self.x = x; self.y = y; self.scale = scale; self.rotation = rotation; self.roles = roles
    }
}

public struct Repeat: Codable, Equatable {
    public var spacingMeters: Double; public var countMin: Int; public var countMax: Int
    public init(spacingMeters: Double = 2, countMin: Int = 1, countMax: Int = 3) {
        self.spacingMeters = spacingMeters; self.countMin = countMin; self.countMax = countMax
    }
}

public struct Constraints: Codable, Equatable {
    public var minSpacingMeters: Double; public var edgeMargin: Double
    public var alignToFloorLine: Bool; public var avoidExact: Bool
    public init(minSpacingMeters: Double = 0, edgeMargin: Double = 0,
                alignToFloorLine: Bool = false, avoidExact: Bool = false) {
        self.minSpacingMeters = minSpacingMeters; self.edgeMargin = edgeMargin
        self.alignToFloorLine = alignToFloorLine; self.avoidExact = avoidExact
    }
}

public struct Jitter: Codable, Equatable {
    public var x: Double; public var scale: [Double]; public var rotation: Double
    public init(x: Double = 0, scale: [Double] = [], rotation: Double = 0) {
        self.x = x; self.scale = scale; self.rotation = rotation
    }
}

public struct ProceduralRule: Codable, Equatable {
    public var part: String
    public var facade: String
    public var floorRange: IntRange
    public var span: [Double]
    public var repeatRule: Repeat     // "repeat" is a Swift keyword; CodingKeys maps to JSON "repeat"
    public var probability: Double
    public var constraints: Constraints
    public var jitter: Jitter
    public var variants: [String]

    enum CodingKeys: String, CodingKey {
        case part, facade, floorRange, span, probability, constraints, jitter, variants
        case repeatRule = "repeat"
    }

    public init(part: String = "", facade: String = "Front",
                floorRange: IntRange = IntRange(min: 0, max: 10), span: [Double] = [],
                repeatRule: Repeat = Repeat(), probability: Double = 1,
                constraints: Constraints = Constraints(), jitter: Jitter = Jitter(),
                variants: [String] = []) {
        self.part = part; self.facade = facade; self.floorRange = floorRange; self.span = span
        self.repeatRule = repeatRule; self.probability = probability
        self.constraints = constraints; self.jitter = jitter; self.variants = variants
    }
}

public struct TemplateDef: Codable, Equatable, Identifiable {
    public var id: String
    public var displayName: String
    public var compatibility: Compatibility
    public var exact: [ExactPlacement]
    public var rules: [ProceduralRule]
    public var roofParts: [String]
    public var version: Int

    public init(id: String, displayName: String = "",
                compatibility: Compatibility = Compatibility(),
                exact: [ExactPlacement] = [], rules: [ProceduralRule] = [],
                roofParts: [String] = [], version: Int = 1) {
        self.id = id; self.displayName = displayName; self.compatibility = compatibility
        self.exact = exact; self.rules = rules; self.roofParts = roofParts; self.version = version
    }
}

// --- Part authoring (#302 — GET/POST /parts, PUT /parts/{id}/glb) ---

public struct SizeM: Codable, Equatable {
    public var w: Double; public var h: Double; public var d: Double
    public init(w: Double = 0, h: Double = 0, d: Double = 0) {
        self.w = w; self.h = h; self.d = d
    }
}

public struct RoleSubmesh: Codable, Equatable {
    public var submesh: Int; public var role: String
    public init(submesh: Int = 0, role: String = "Base") {
        self.submesh = submesh; self.role = role
    }
}

public struct PartDef: Codable, Equatable, Identifiable {
    public var id: String
    public var category: String
    public var glb: String
    public var size_m: SizeM
    public var roleSubmeshes: [RoleSubmesh]
    public var anchor: String
    public var mountDepth_m: Double
    public var version: Int

    public init(id: String, category: String = "", glb: String = "",
                size_m: SizeM = SizeM(), roleSubmeshes: [RoleSubmesh] = [],
                anchor: String = "BottomCenter", mountDepth_m: Double = 0.08,
                version: Int = 1) {
        self.id = id; self.category = category; self.glb = glb; self.size_m = size_m
        self.roleSubmeshes = roleSubmeshes; self.anchor = anchor
        self.mountDepth_m = mountDepth_m; self.version = version
    }
}

// --- Palettes (#298 plan — POST /palettes) ---

/// One material-role slot in a palette (e.g. wall, trim, window).
public struct PaletteEntry: Codable, Equatable {
    public var role: String         // semantic slot, e.g. "wall", "trim", "window"
    public var color: String        // "#RRGGBB"
    public var metallic: Double
    public var roughness: Double

    public init(role: String = "", color: String = "#FFFFFF",
                metallic: Double = 0, roughness: Double = 0.8) {
        self.role = role; self.color = color; self.metallic = metallic; self.roughness = roughness
    }
}

/// A named palette of material-role color assignments for a building facade.
public struct Palette: Codable, Equatable {
    public var id: String?
    public var name: String
    public var entries: [PaletteEntry]

    public init(id: String? = nil, name: String = "", entries: [PaletteEntry] = []) {
        self.id = id; self.name = name; self.entries = entries
    }
}

// --- Districts (#341 — GET/POST /districts; config layer over the neighborhood key) ---

public struct TemplateWeight: Codable, Equatable {
    public var template: String
    public var weight: Double

    public init(template: String = "", weight: Double = 1) {
        self.template = template; self.weight = weight
    }
}

public struct DistrictDef: Codable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var neighborhoods: [String]
    public var templateWeights: [TemplateWeight]
    public var palette: String
    public var signStyle: String
    public var version: Int

    public init(id: String, name: String = "", neighborhoods: [String] = [],
                templateWeights: [TemplateWeight] = [], palette: String = "",
                signStyle: String = "Modern", version: Int = 1) {
        self.id = id; self.name = name; self.neighborhoods = neighborhoods
        self.templateWeights = templateWeights; self.palette = palette
        self.signStyle = signStyle; self.version = version
    }
}

# Data Model: SF Building Template Pipeline

**Issue:** #266
**Phase:** drafting
**Date:** 2026-06-30
**Companion:** `design.md` (architecture, boundaries, placement & assembly model)

Concrete schemas for every artifact that crosses a component boundary. Field names are
normative; types are C# (Unity) / Python (bake) / JSON (wire & sidecar).

---

## 1. Python → Unity: the classification sidecar  (NEW)

Mirrors the existing `chunk_CC_RR_names.json` / `chunk_CC_RR_parked.json` sidecar convention.
One file per chunk, written by `python/sfmap/serialize.py`. **Facts only — no template choice.**

`chunk_CC_RR_buildings.json`
```jsonc
{
  "version": 1,
  "buildings": [
    {
      "osm_id": 65307880,            // matches MeshType BUILDING entry in the .bin
      "neighborhood": "Mission",      // point-in-polygon(centroid, neighborhoods.geojson); "" if none
      "building_type": "retail",      // OSM building=* tag, passthrough; "" if absent
      "footprint_shape": "corner",    // rect | L | corner | irregular
      "width_m": 11.4,                // oriented-bbox long edge
      "depth_m": 18.2,                // oriented-bbox short edge
      "height_m": 12.0,               // from .bin / OSM height
      "floor_count": 4,               // round(height_m / 3.0); see design.md D5 (data-quality risk)
      "street_facades": [             // THE critical fact: ranked, primary first. Corner buildings
        { "edge_index": 2, "bearing_deg": 117.0, "street_osm_id": 8412731, "score": 0.94 },
        { "edge_index": 3, "bearing_deg": 207.0, "street_osm_id": 5512004, "score": 0.71 }
      ],                              // empty ⇒ no street-facing edge within threshold
      "footprint_hash": "a3f1c9d2"    // §6 normative algorithm — override match guard
    }
  ]
}
```

Python side (extends `BuildingWay` in `osm.py`):
```python
@dataclass
class BuildingWay:
    osm_id: int
    footprint: list[tuple[float, float]]   # (x, z) world meters — existing
    height: float                          # existing
    building_type: str | None              # existing (now propagated downstream)
    footprint_hash: str = ""               # NEW — set in chunk.py after projection
```

Classification record assembled in `chunk.py`:
```python
@dataclass
class ClassificationRecord:
    osm_id: int
    neighborhood: str
    building_type: str
    footprint_shape: str        # rect | L | corner | irregular
    width_m: float
    depth_m: float
    height_m: float
    floor_count: int
    street_facades: list[StreetFacade]   # ranked, primary first; [] if none
    footprint_hash: str

@dataclass
class StreetFacade:
    edge_index: int
    bearing_deg: float
    street_osm_id: int          # -1 if none
    score: float                # proximity × parallelism, for ranking
```

New bake inputs (`sfmap_bake.py`): `--neighborhoods <path.geojson>` (enables `neighborhood`),
`--templates` (gate that turns the sidecar emission on; off ⇒ no sidecar, pure legacy bake).

---

## 2. Server → Unity: the asset library  (NEW, on disk)

The *only* artifact shared between authoring and generation. Written by `POST /export/unity`,
consumed by a Unity import post-processor that converts JSON → ScriptableObjects.

```
Assets/SFBuildingTemplates/
  library.json                       # manifest: version, exportedAt, neighborhoods[], counts
  Parts/      <partId>.glb           # authored geometry; submeshes tagged by material role
              <partId>.part.json     # PartDef (below)
  Signs/      <signId>.png           # AI/authored signage texture
              <signId>.sign.json     # SignDef
  Templates/  <templateId>.template.json   # TemplateDef → BuildingTemplate SO
  Palettes/   <neighborhood>.palette.json  # PaletteDef → NeighborhoodPalette SO
  Overrides/  <osm_id>.override.json        # BuildingSpecificDef
```

`PartDef` (`<partId>.part.json`)
```jsonc
{
  "id": "window_sunset_2x3",
  "category": "Window",            // Roof|Door|Garage|Window|BayWindow|Storefront|Sign
  "glb": "Parts/window_sunset_2x3.glb",
  "size_m": { "w": 1.2, "h": 1.6, "d": 0.15 },   // authored real-world size (PDF: meters)
  "roleSubmeshes": {               // submesh index → material role
    "0": "Base", "1": "Glass", "2": "Accent1"
  },
  "anchor": "BottomCenter",        // how normalized placement maps to the mesh origin
  "mountDepth_m": -0.08,           // signed offset along the wall normal: <0 inset (window),
                                   // >0 protrude (bay window). Prevents z-fighting (design.md D4)
  "version": 3
}
```

`TemplateDef` (`<templateId>.template.json`) — the recipe; see `design.md` §Placement Model.
```jsonc
{
  "id": "sunset_row_house",
  "displayName": "Sunset Row House",
  "compatibility": {
    "neighborhoods": ["Sunset"],
    "building_types": ["residential", "house", ""],
    "footprint_shapes": ["rect"],
    "width_m":  { "min": 6.0,  "max": 12.0 },
    "depth_m":  { "min": 10.0, "max": 30.0 },
    "floor_count": { "min": 1, "max": 3 }
  },
  "exact": [                       // Exact Layout placements
    // facade ∈ Front|Back|Left|Right|Roof|Street  (Street = expand to all ranked street_facades)
    { "part": "door_sunset_a", "facade": "Front", "floor": 0,
      "x": 0.5, "y": 0.0, "scale": 1.0, "rotation": 0.0,
      "roles": { "Base": "Base", "Accent1": "Accent1" } }
  ],
  "rules": [                       // Procedural Rule placements
    { "part": "window_sunset_2x3", "facade": "Front",
      "floorRange": { "min": 1, "max": 3 },
      "span": [0.1, 0.9],
      "repeat": { "spacingMeters": 3.0, "countMin": 1, "countMax": 6 },
      "probability": 1.0,
      "constraints": { "minSpacingMeters": 1.5, "edgeMargin": 0.05,
                       "alignToFloorLine": true, "avoidExact": true },
      "jitter": { "x": 0.0, "scale": [0.95, 1.05], "rotation": 0.0 },
      "variants": ["window_sunset_2x3", "window_sunset_2x2"] }
  ],
  "roofParts": ["cornice_sunset"],   // applied in Roof stage
  "version": 5
}
```

`PaletteDef` (`<neighborhood>.palette.json`)
```jsonc
{
  "neighborhood": "Sunset",
  "roles": {
    "Base":    { "colors": ["#E9DfC6","#D8C9A8","#EFE7D2"], "mode": "pick" },
    "Accent1": { "colors": ["#B6A06A","#9C8550"],            "mode": "pick" },
    "Accent2": { "ramp": ["#C9B98F","#8A7440"],              "mode": "lerp" },
    "Glass":   { "colors": ["#3A4A55"], "mode": "constant" },
    "Metal":   { "colors": ["#6B6B6B"], "mode": "constant" }
  },
  "version": 2
}
```

`BuildingSpecificDef` (`Overrides/<osm_id>.override.json`)
```jsonc
{
  "osm_id": 65307880,
  "footprint_hash": "a3f1c9d2",    // MUST match the bake's hash or the override is skipped (warned)
  "baseTemplate": "mission_corner_store",   // optional starting point
  "placements": [                  // exact, win over template on conflict
    { "part": "sign_lucca_deli", "facade": "Front", "floor": 0, "x": 0.5, "y": 0.85,
      "scale": 1.0, "rotation": 0.0, "signAsset": "sign_lucca_deli" }
  ],
  "suppress": [ { "part": "window_mission_std", "facade": "Front", "floor": 0 } ],
  "version": 1
}
```

---

## 3. Unity ScriptableObjects  (NEW — generated from §2 at import)

In `Assets/Scripts/Pipeline/Buildings/` (namespace `SFMap.Pipeline.Buildings`).

```csharp
public enum MaterialRole { Base, Accent1, Accent2, Glass, Metal, Sign }
public enum PartCategory { Roof, Door, Garage, Window, BayWindow, Storefront, Sign }
public enum Facade       { Front, Back, Left, Right, Roof, Street }  // Street → all ranked street_facades
public enum PlacementMode{ Exact, Procedural, BuildingSpecific }

[CreateAssetMenu(menuName = "SFMap/Building Part")]
public sealed class BuildingPart : ScriptableObject {
    public string   id;
    public PartCategory category;
    public GameObject prefab;            // imported from the GLB
    public Vector3   sizeMeters;
    public MaterialRole[] submeshRoles;  // index-aligned to prefab submeshes
    public bool      isSign;
}

[CreateAssetMenu(menuName = "SFMap/Building Template")]
public sealed class BuildingTemplate : ScriptableObject {
    public string id, displayName;
    public Compatibility compatibility;  // neighborhoods, types, shapes, dim bands
    public ExactPlacement[]     exact;
    public ProceduralRule[]     rules;
    public BuildingPart[]       roofParts;
}

[CreateAssetMenu(menuName = "SFMap/Neighborhood Palette")]
public sealed class NeighborhoodPalette : ScriptableObject {
    public string neighborhood;
    public RolePalette[] roles;          // role → colors/ramp + mode (pick|lerp|constant)
    public Color Resolve(MaterialRole role, uint seed);   // deterministic
}
```

`PlacementRecord` — the resolved, per-instance metadata from the PDF (in-memory during assembly,
not serialized separately; the assembled prefab IS its serialization):
```csharp
public struct PlacementRecord {
    public BuildingPart part;
    public PartCategory category;
    public Facade   facade;
    public int      floor;          // -1 == "all floors" expanded before this point
    public float    x, y;           // normalized [0,1] on the facade
    public float    scale, rotationDeg;
    public PlacementMode mode;
    public Dictionary<MaterialRole, MaterialRole> roleMap;   // part role → building role
    public string   signAssetId;    // non-null only for Sign parts
}
```

Override store (loaded from `Overrides/`): `Dictionary<long, BuildingSpecificDef>` keyed by
`osm_id`, matched only when `footprint_hash` also equals the bake's.

---

## 4. Asset paths (extend `GeneratedAssets` in `PipelineTypes.cs`)

```csharp
// existing: ChunkDir, BuildingsCombinedMesh, BuildingMaterial, ManifestPath ...
public static string BuildingsSidecar(ChunkCoord c)        // chunk_CC_RR_buildings.json
public static string BuildingPrefab(ChunkCoord c, long id) // {ChunkDir}/Buildings/building_{id}.prefab
public static string TemplateLibraryRoot => "Assets/SFBuildingTemplates";
```

Chunk prefab hierarchy gains templated buildings as nested instances (parked-car precedent):
```
chunk_CC_RR [Prefab]
  ├── Buildings chunk_CC_RR
  │     ├── buildings_combined  (MeshFilter/Renderer)   ← un-templated FALLBACK (as today)
  │     ├── building_65307880   [nested prefab]         ← templated (NEW)
  │     │     ├── Mass (MeshFilter/Renderer)
  │     │     ├── Door / Window×N / Storefront / Sign …
  │     └── …
  └── … (Terrain, Roads, Intersections, Sidewalks, ParkedCars as today)
```

---

## 5. Home PC Server API  (authoring side — contract only)

FastAPI. JSON bodies; GLB/PNG via multipart. SQLite first (→ Postgres later).

| Method & path | Purpose |
|---|---|
| `GET  /neighborhoods` | list neighborhoods + palette refs |
| `GET  /building-types` | classification vocabulary |
| `GET  /templates` · `POST /templates` | list / author templates |
| `GET  /parts` · `POST /parts` | list / upload parts (GLB + PartDef) |
| `POST /building-specific` | upload an override (keyed osm_id + footprint_hash) |
| `POST /ai/signs/generate` | server-mediated AI sign gen → PNG + metadata + thumbnail |
| `POST /export/unity` | materialize `Assets/SFBuildingTemplates/` asset drop (§2) |

`POST /ai/signs/generate` body / response:
```jsonc
// request
{ "businessType": "deli", "neighborhood": "Mission", "text": "LUCCA",
  "aspectRatio": "3:1", "stylePreset": "vintage_handpainted" }
// response
{ "signId": "sign_lucca_deli", "png": "Signs/sign_lucca_deli.png",
  "thumb": "Signs/sign_lucca_deli.thumb.png", "provider": "nano-banana", "version": 1 }
```

Provider abstraction: server picks a backend (ChatGPT image gen / Nano Banana / future) behind a
single interface; the iPad and the export format are provider-agnostic. **iPad never calls a
provider directly** (PDF constraint).

---

## 6. Determinism contract

Every nondeterministic choice is seeded from a stable key so re-bake + re-import are byte-stable:

| Choice | Seed |
|---|---|
| template tie-break among matches | `hash(osm_id)` |
| procedural slot count / probability / jitter / variant | `hash(osm_id, ruleIndex, slotIndex)` |
| palette role resolution | `hash(osm_id, role)` |
| fallback vertex-color palette (legacy) | `hash(osm_id)` (unchanged) |

`footprint_hash` is **not** a seed — it is a *match guard* for building-specific overrides only.

### 6.1 `footprint_hash` — normative algorithm (shared by Python bake AND server)

Building-specific overrides are authored on the server but matched at Unity import against the
bake's hash. Python and the server therefore compute this identically, from the same OSM source
(design.md D3). The algorithm:

1. Take the footprint ring in **world meters** (post-projection), drop a closing duplicate vertex.
2. **Quantize** each `(x, z)` to a `0.25 m` grid: `q = round(v / 0.25) * 0.25` (caps how much a
   footprint edit invalidates an override; tunable, but the value is part of the contract).
3. **Canonicalize ordering:** rotate the ring to start at the lexicographically smallest
   `(x, z)` vertex; force counter-clockwise winding. (Removes start-vertex / direction ambiguity.)
4. Serialize as `"x.xx,z.zz;"` per vertex, concatenated.
5. `footprint_hash = first 8 hex chars of SHA-256(serialized)`.

Any change to grid size, winding rule, or serialization format is a breaking contract change and
must bump the sidecar `version`. Mismatch ⇒ the importer **skips the override and logs a warning**
(never dresses the wrong building).
```

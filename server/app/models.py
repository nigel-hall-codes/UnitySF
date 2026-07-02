"""Pydantic models for the authored library (design #266 data-model.md §2).

These are the *wire* shapes the server stores and exports. They match the Unity
importer's DTOs (`Assets/Scripts/Pipeline/Buildings/LibraryJson.cs`, #269) field-for-
field — including the JsonUtility-friendly **array-of-pairs** form for the fields §2
draws as JSON object maps (Unity's JsonUtility cannot read string-keyed maps):

    roleSubmeshes:  [{ "submesh": 0, "role": "Base" }]
    exact[].roles:  [{ "from": "Base", "to": "Accent1" }]
    palette roles:  [{ "role": "Base", "colors": [...], "mode": "pick" }]

The export writes these with ``by_alias=True`` so ``RolePair.from_`` serialises as
``"from"`` (a Python keyword, hence the alias).
"""
from __future__ import annotations

from typing import List

from pydantic import BaseModel, ConfigDict, Field


# --- Parts -----------------------------------------------------------------

class SizeM(BaseModel):
    w: float = 0.0
    h: float = 0.0
    d: float = 0.0


class RoleSubmesh(BaseModel):
    submesh: int
    role: str


class PartDef(BaseModel):
    id: str
    category: str
    glb: str = ""
    size_m: SizeM = Field(default_factory=SizeM)
    roleSubmeshes: List[RoleSubmesh] = Field(default_factory=list)
    anchor: str = ""
    mountDepth_m: float = 0.0
    version: int = 1


# --- Templates -------------------------------------------------------------

class FloatRange(BaseModel):
    min: float = 0.0
    max: float = 0.0


class IntRange(BaseModel):
    min: int = 0
    max: int = 0


class Compatibility(BaseModel):
    neighborhoods: List[str] = Field(default_factory=list)
    building_types: List[str] = Field(default_factory=list)
    footprint_shapes: List[str] = Field(default_factory=list)
    width_m: FloatRange = Field(default_factory=FloatRange)
    depth_m: FloatRange = Field(default_factory=FloatRange)
    floor_count: IntRange = Field(default_factory=IntRange)


class RolePair(BaseModel):
    # 'from' is a Python keyword → store as from_, serialise/parse as "from".
    model_config = ConfigDict(populate_by_name=True)
    from_: str = Field(alias="from")
    to: str


class ExactPlacement(BaseModel):
    part: str
    facade: str
    floor: int = 0
    x: float = 0.0
    y: float = 0.0
    scale: float = 1.0
    rotation: float = 0.0
    roles: List[RolePair] = Field(default_factory=list)


class Repeat(BaseModel):
    spacingMeters: float = 0.0
    countMin: int = 0
    countMax: int = 0


class Constraints(BaseModel):
    minSpacingMeters: float = 0.0
    edgeMargin: float = 0.0
    alignToFloorLine: bool = False
    avoidExact: bool = False


class Jitter(BaseModel):
    x: float = 0.0
    scale: List[float] = Field(default_factory=list)
    rotation: float = 0.0


class ProceduralRule(BaseModel):
    part: str
    facade: str
    floorRange: IntRange = Field(default_factory=IntRange)
    span: List[float] = Field(default_factory=list)
    repeat: Repeat = Field(default_factory=Repeat)
    probability: float = 1.0
    constraints: Constraints = Field(default_factory=Constraints)
    jitter: Jitter = Field(default_factory=Jitter)
    variants: List[str] = Field(default_factory=list)


# --- Zones (design #326 D1; authoring format, compiled to exact/rules at export) --

class WeightedPart(BaseModel):
    part: str
    weight: float = 1.0


class ZoneShape(BaseModel):
    kind: str = "rect"                                        # "rect" | "polygon"
    points: List[List[float]] = Field(default_factory=list)   # facade UV, bottom-origin


class ZoneRules(BaseModel):
    allowedParts: List[WeightedPart] = Field(default_factory=list)
    countRange: IntRange = Field(default_factory=IntRange)
    spacingMeters: FloatRange = Field(default_factory=FloatRange)
    randomOffset: float = 0.0
    alignment: str = "Grid"                                    # "Grid" | "FloorLine" | "Free"


class Zone(BaseModel):
    id: str
    type: str      # Window|Door|Storefront|Sign|Balcony|Roof|Decoration|Utility
    facade: str = "Front"
    shape: ZoneShape = Field(default_factory=ZoneShape)
    floorRange: IntRange = Field(default_factory=IntRange)
    rules: ZoneRules = Field(default_factory=ZoneRules)


class TemplateDef(BaseModel):
    id: str
    displayName: str = ""
    compatibility: Compatibility = Field(default_factory=Compatibility)
    exact: List[ExactPlacement] = Field(default_factory=list)
    rules: List[ProceduralRule] = Field(default_factory=list)
    zones: List[Zone] = Field(default_factory=list)
    roofParts: List[str] = Field(default_factory=list)
    version: int = 1


# --- Palettes --------------------------------------------------------------

class RoleDef(BaseModel):
    role: str
    colors: List[str] = Field(default_factory=list)
    ramp: List[str] = Field(default_factory=list)
    mode: str = "pick"


class PaletteDef(BaseModel):
    neighborhood: str
    roles: List[RoleDef] = Field(default_factory=list)
    version: int = 1


# --- Districts (design #326 D4; config layer over the existing neighborhood key) --
# `neighborhood` stays the geographic key the bake/palettes use; DistrictDef is an
# authoring layer above it (template mix + sign style), consumed by export-time
# template selection (#343) and the district preview (#342). No bake/sidecar changes.

class TemplateWeight(BaseModel):
    template: str
    weight: float = 1.0


class DistrictDef(BaseModel):
    id: str
    name: str = ""
    neighborhoods: List[str] = Field(default_factory=list)
    templateWeights: List[TemplateWeight] = Field(default_factory=list)
    palette: str = ""          # PaletteDef.neighborhood ref
    signStyle: str = "Modern"  # Modern|Vintage|Bilingual|Tourist|Mixed
    version: int = 1


# --- Building-specific overrides -------------------------------------------

class Placement(BaseModel):
    part: str
    facade: str = "Front"
    floor: int = 0
    x: float = 0.0
    y: float = 0.0
    scale: float = 1.0
    rotation: float = 0.0
    signAsset: str = ""


class BuildingSpecificDef(BaseModel):
    osm_id: int
    footprint_hash: str = ""
    baseTemplate: str = ""
    placements: List[Placement] = Field(default_factory=list)
    suppress: List[Placement] = Field(default_factory=list)
    version: int = 1


# --- AI signs (data-model.md §5 POST /ai/signs/generate) -------------------

class SignRequest(BaseModel):
    businessType: str = ""
    neighborhood: str = ""
    text: str = ""
    aspectRatio: str = "1:1"
    stylePreset: str = ""
    provider: str = ""        # optional override; "" → server default


class SignDef(BaseModel):
    """The stored, reusable sign record (and the exported <signId>.sign.json)."""
    signId: str
    png: str                  # library-relative, e.g. Signs/<id>.png
    thumb: str                # library-relative, e.g. Signs/<id>.thumb.png
    provider: str
    version: int = 1
    # the request that produced it, retained so a sign is reproducible / searchable
    businessType: str = ""
    neighborhood: str = ""
    text: str = ""
    aspectRatio: str = "1:1"
    stylePreset: str = ""


# --- Facade canvas (design #278; stored server-side, flattened on export) --

class Stroke(BaseModel):
    points: List[List[float]] = Field(default_factory=list)  # [[x,y],...] normalized facade UV
    color: str = "#000000"
    width: float = 0.01                                      # normalized stroke width


class CanvasLayer(BaseModel):
    kind: str = "paint"              # "paint" (freehand strokes) | "image" (placed sign/AI image)
    layer: int = 0                   # z-order within the facade (paint usually lowest)
    mountDepth_m: float = 0.02
    # paint layers:
    strokes: List[Stroke] = Field(default_factory=list)
    # image layers (a placed image / AI sign — stays a discrete decal):
    rect: List[float] = Field(default_factory=lambda: [0.0, 0.0, 1.0, 1.0])
    texture: str = ""                # existing PNG ref, e.g. Signs/<id>.png
    signAsset: str = ""              # optional link to a #275 sign asset


class FacadeCanvas(BaseModel):
    """The layered canvas document for one building facade — the authoring source of truth."""
    osm_id: int
    facade: str = "Front"
    footprint_hash: str = ""         # carried onto the override's guard at export
    layers: List[CanvasLayer] = Field(default_factory=list)
    version: int = 1


# --- Building facts (the bake's classification sidecar; #299) ---------------
# Mirrors python/sfmap/serialize.py:write_buildings — the v2 chunk_CC_RR_buildings.json
# schema (data-model.md §1). The server ingests these facts so the iPad can browse
# buildings and read per-building facts without re-baking. Facts only — Unity/the iPad
# choose templates; the server just stores and serves.

class StreetFacade(BaseModel):
    edge_index: int = 0
    bearing_deg: float = 0.0
    street_osm_id: int = 0
    score: float = 0.0
    edge: List[float] = Field(default_factory=list)   # [x0, z0, x1, z1] world-XZ


class BuildingFacts(BaseModel):
    osm_id: int
    neighborhood: str = ""
    building_type: str = ""
    footprint_shape: str = ""
    width_m: float = 0.0
    depth_m: float = 0.0
    height_m: float = 0.0
    floor_count: int = 0
    base_y: float = 0.0
    facade_height_m: float = 0.0
    street_facades: List[StreetFacade] = Field(default_factory=list)
    footprint_hash: str = ""


class SidecarDoc(BaseModel):
    """One chunk's building sidecar — the POST /buildings/import-sidecar body."""
    version: int = 2
    buildings: List[BuildingFacts] = Field(default_factory=list)


class BuildingPage(BaseModel):
    """A page of the GET /buildings list. ``total`` is the full filtered count so the
    iPad browser can paginate; ``buildings`` is this page only."""
    buildings: List[BuildingFacts] = Field(default_factory=list)
    total: int = 0
    limit: int = 0
    offset: int = 0


# --- Export request / response ---------------------------------------------

class ExportRequest(BaseModel):
    # Where to materialise the library drop. Defaults to the env-configured target.
    outDir: str = ""


class ExportResult(BaseModel):
    outDir: str
    version: int
    parts: int
    templates: int
    palettes: int
    overrides: int
    glbsCopied: int
    signs: int = 0
    facadeDecals: int = 0
    paintTextures: int = 0

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


class TemplateDef(BaseModel):
    id: str
    displayName: str = ""
    compatibility: Compatibility = Field(default_factory=Compatibility)
    exact: List[ExactPlacement] = Field(default_factory=list)
    rules: List[ProceduralRule] = Field(default_factory=list)
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

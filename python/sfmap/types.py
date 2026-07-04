"""Neutral data types for the bake pipeline.

The chunk/mesh data structures live here — independent of how they are produced
(``sfmap.chunk`` builds them) or consumed (``sfmap.serialize`` writes them) — so
neither the geometry side nor the serializer has to import the other. Extracted
from ``serialize.py`` (#424); definitions are unchanged.
"""
from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
from typing import List, Tuple

from .elevation import HeightmapData


class MeshType(IntEnum):
    ROAD         = 0
    INTERSECTION = 1
    SIDEWALK     = 2
    BUILDING     = 3


@dataclass
class MeshEntry:
    """One mesh record inside a chunk .bin file."""
    mesh_type: MeshType
    osm_id: int                                  # raw OSM node/way ID as int64
    vertices: List[Tuple[float, float, float]]   # (x, y, z) Unity left-handed
    normals: List[Tuple[float, float, float]]    # all-zero → C# calls RecalculateNormals
    uvs: List[Tuple[float, float]]
    indices: List[int]                           # CW winding, multiple of 3


@dataclass
class ChunkData:
    """All data for one chunk_CC_RR.bin file."""
    col: int
    row: int
    world_x: float
    world_z: float
    chunk_size_m: float
    heightmap: HeightmapData
    meshes: List[MeshEntry]
    # Named road segments: list of (name, centerline_xz_points, width_m, is_one_way).
    # Unnamed roads omitted.
    road_names: List[Tuple[str, List[Tuple[float, float]], float, bool]] = None
    # Parked-car placements (sfmap.geometry.parking.ParkedCar), or [] when no
    # parking source was supplied. Serialised to a JSON sidecar, not the .bin.
    parked_cars: List = None
    # Building classification records (sfmap.classify.ClassificationRecord), or []
    # unless the bake ran with --templates. Serialised to chunk_CC_RR_buildings.json.
    buildings: List = None

    def __post_init__(self):
        if self.road_names is None:
            self.road_names = []
        if self.parked_cars is None:
            self.parked_cars = []
        if self.buildings is None:
            self.buildings = []

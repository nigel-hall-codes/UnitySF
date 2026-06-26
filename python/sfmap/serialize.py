"""Binary .bin chunk writer and manifest.json writer."""
from __future__ import annotations

import json
import struct
from dataclasses import dataclass
from enum import IntEnum
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np

from .elevation import HeightmapData

# Matches C# importer — "CHNK" as little-endian u32
_CHUNK_MAGIC = 0x4B4E4843
_CHUNK_VERSION = 1

# Header format: magic(u32) version(u32) col(i32) row(i32) wx(f32) wz(f32)
#                chunk_size(f32) min_elev(f32) max_elev(f32) hmap_res(i32)
_HEADER_FMT = "<IIiifffffi"
_HEADER_SIZE = struct.calcsize(_HEADER_FMT)  # 40 bytes


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
    # Named road segments: list of (name, centerline_xz_points, width_m).
    # Unnamed roads omitted.
    road_names: List[Tuple[str, List[Tuple[float, float]], float]] = None

    def __post_init__(self):
        if self.road_names is None:
            self.road_names = []


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def write_chunk(chunk: ChunkData, out_dir: str) -> Path:
    """Serialise chunk data to ``<out_dir>/chunk_CC_RR.bin``.

    Binary layout (all little-endian):
      ChunkHeader (40 bytes):
        magic         u32   0x4B4E4843
        version       u32   1
        chunk_col     i32
        chunk_row     i32
        world_x       f32
        world_z       f32
        chunk_size_m  f32
        min_elev_m    f32
        max_elev_m    f32
        hmap_res      i32
      hmap_data       f32[hmap_res * hmap_res]  (row-major, normalised [0,1])
      mesh_count      i32
      MeshEntry × mesh_count (see _write_mesh_entry)
    """
    out_path = Path(out_dir) / f"chunk_{chunk.col:02d}_{chunk.row:02d}.bin"
    out_path.parent.mkdir(parents=True, exist_ok=True)

    hmap = chunk.heightmap
    hmap_res = hmap.resolution

    with open(out_path, "wb") as f:
        # Header
        f.write(struct.pack(
            _HEADER_FMT,
            _CHUNK_MAGIC,
            _CHUNK_VERSION,
            chunk.col,
            chunk.row,
            chunk.world_x,
            chunk.world_z,
            chunk.chunk_size_m,
            hmap.min_elevation_m,
            hmap.max_elevation_m,
            hmap_res,
        ))

        # Heightmap (row-major, normalised float32)
        hmap.values.astype(np.float32).tofile(f)

        # Mesh count + entries
        f.write(struct.pack("<i", len(chunk.meshes)))
        for mesh in chunk.meshes:
            _write_mesh_entry(f, mesh)

    return out_path


def write_road_names(chunk: "ChunkData", out_dir: str) -> Optional[Path]:
    """Write chunk_CC_RR_names.json alongside the .bin for named roads only.

    JSON schema (Unity reads this as a TextAsset):
      {"roads":[{"n":"Market Street","xz":[x1,z1,x2,z2,...],"w":7.0},...]}

    Centerline coords are Unity world-space XZ (same as the mesh vertices); "w" is
    the road width in metres, used by the traffic system for lane offsetting.
    Returns None and writes nothing if there are no named roads in this chunk.
    """
    if not chunk.road_names:
        return None

    roads = []
    for name, centerline, width in chunk.road_names:
        xz = []
        for x, z in centerline:
            xz.append(round(x, 3))
            xz.append(round(z, 3))
        roads.append({"n": name, "xz": xz, "w": round(width, 2)})

    out_path = Path(out_dir) / f"chunk_{chunk.col:02d}_{chunk.row:02d}_names.json"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps({"roads": roads}, ensure_ascii=False), encoding="utf-8")
    return out_path


def write_manifest(
    preset: str,
    chunk_size_m: float,
    chunks_x: int,
    chunks_z: int,
    osm_file: str,
    osm_bounds_dict: dict,
    hmap_resolution: int,
    min_elevation_m: float,
    max_elevation_m: float,
    chunk_origins: List[Tuple[int, int, float, float]],
    out_dir: str,
    generated_iso: str,
) -> Path:
    """Write manifest.json — schema unchanged from the C# pipeline."""
    manifest = {
        "preset": preset,
        "generated": generated_iso,
        "chunkSize": chunk_size_m,
        "chunksX": chunks_x,
        "chunksZ": chunks_z,
        "osmFile": osm_file,
        "osmBounds": osm_bounds_dict,
        "heightmapResolution": hmap_resolution,
        "minElevation": round(min_elevation_m, 4),
        "maxElevation": round(max_elevation_m, 4),
        "chunks": [
            {"col": col, "row": row, "worldX": round(wx, 3), "worldZ": round(wz, 3)}
            for col, row, wx, wz in chunk_origins
        ],
    }
    out_path = Path(out_dir) / "manifest.json"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return out_path


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _write_mesh_entry(f, mesh: MeshEntry) -> None:
    """Write one MeshEntry record.

    Layout:
      mesh_type     u8
      osm_id        i64   (7 bytes implicit gap after u8 — NOT padded; C# reads sequentially)
      vert_count    i32
      idx_count     i32
      vertices      f32[vert_count * 3]
      normals       f32[vert_count * 3]
      uvs           f32[vert_count * 2]
      indices       u32[idx_count]

    Note: the C# BinaryReader reads fields in order without alignment padding,
    so we write u8 immediately followed by i64 with no gap.
    """
    vert_count = len(mesh.vertices)
    idx_count  = len(mesh.indices)

    f.write(struct.pack("<B", int(mesh.mesh_type)))
    f.write(struct.pack("<q", mesh.osm_id))
    f.write(struct.pack("<ii", vert_count, idx_count))

    np.array(mesh.vertices, dtype=np.float32).flatten().tofile(f)

    if mesh.normals:
        np.array(mesh.normals, dtype=np.float32).flatten().tofile(f)
    else:
        np.zeros(vert_count * 3, dtype=np.float32).tofile(f)

    np.array(mesh.uvs, dtype=np.float32).flatten().tofile(f)
    np.array(mesh.indices, dtype=np.uint32).tofile(f)

"""CropToChunk + per-chunk pipeline orchestration.

Mirrors the per-chunk loop of the old C# SFMapPipelineWindow: the full graph and
full heightmap are built once by the caller, intersection polygons/boundaries are
computed once on the full graph, then each chunk is produced by cropping the graph,
resampling the heightmap into the chunk's world rect, stamping, and building meshes.
"""
from __future__ import annotations

from typing import Dict, Optional, Tuple

import numpy as np

from .elevation import HeightmapData
from .geometry.building import build_building_meshes
from .geometry.intersection import triangulate_fan
from .geometry.road import _sample_elevation, build_road_meshes
from .geometry.sidewalk import build_sidewalk_meshes
from .osm import StreetGraph
from .serialize import ChunkData, MeshEntry, MeshType
from .stamping import stamp_all

# Boundaries dict type: (way_id, from_node, to_node) -> (from_xz, to_xz)
_Boundaries = Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]


def _merge_by_id(
    arrays_by_key: Dict[Tuple[int, int, int], Tuple[list, list, list]],
) -> Dict[int, Tuple[list, list, list]]:
    """Merge mesh arrays sharing an osm way id into one (verts, uvs, indices).

    The OSM parser splits a way into several edges at intersections, so multiple
    edge meshes share ``way_id`` (= key[0]). The importer names every mesh asset
    ``<type>_<osm_id>``, so distinct entries with the same id would collide on
    one asset path. Merging them into a single mesh per way keeps each id unique.
    """
    merged: Dict[int, Tuple[list, list, list]] = {}
    for (way_id, _f, _t), (verts, uvs, indices) in arrays_by_key.items():
        if not verts or not indices:
            continue
        if way_id not in merged:
            merged[way_id] = ([], [], [])
        m_verts, m_uvs, m_indices = merged[way_id]
        offset = len(m_verts)
        m_verts.extend(verts)
        m_uvs.extend(uvs)
        m_indices.extend(i + offset for i in indices)
    return merged


def chunk_world_rect(
    col: int, row: int, chunk_size: float, base_x: float = 0.0, base_z: float = 0.0
) -> Tuple[float, float, float, float]:
    """Return (x_min, z_min, width, height) for chunk (col, row).

    The chunk's SW corner is (base_x + col*size, base_z + row*size). ``base_x``/
    ``base_z`` anchor the grid at the data's SW corner; they default to 0 so the
    legacy "grid starts at world origin" behaviour is preserved when unspecified.
    Note the projection centres world coordinates on the OSM bounds, so the data
    straddles the origin — the caller must pass the geometry's min corner here or
    the negative-XZ portion of the map falls outside the grid and is dropped.
    """
    return base_x + col * chunk_size, base_z + row * chunk_size, chunk_size, chunk_size


def resample_heightmap(
    full: HeightmapData,
    x_min: float,
    z_min: float,
    size: float,
    resolution: int,
) -> HeightmapData:
    """Bilinearly resample ``full`` into a new heightmap covering the chunk rect.

    The result keeps the source's min/max elevation so absolute elevations stay
    consistent across chunks (importer offsets each terrain by its min elevation).
    Equivalent to the old C# ``HeightmapData.CropToChunk``, vectorised over numpy.
    """
    src = full.values
    s = full.resolution
    dst_cell = size / (resolution - 1)
    src_cell_w = full.world_width / (s - 1)
    src_cell_h = full.world_height / (s - 1)

    cols = np.arange(resolution)
    rows = np.arange(resolution)
    # Fractional src indices for each dest column/row.
    nx = (x_min + cols * dst_cell - full.world_x_min) / src_cell_w
    nz = (z_min + rows * dst_cell - full.world_z_min) / src_cell_h

    col0 = np.clip(nx.astype(np.int64), 0, s - 2)
    row0 = np.clip(nz.astype(np.int64), 0, s - 2)
    tx = np.clip(nx - col0, 0.0, 1.0)[None, :]   # (1, res)
    tz = np.clip(nz - row0, 0.0, 1.0)[:, None]   # (res, 1)

    r0 = row0[:, None]
    c0 = col0[None, :]
    v00 = src[r0, c0]
    v10 = src[r0, c0 + 1]
    v01 = src[r0 + 1, c0]
    v11 = src[r0 + 1, c0 + 1]
    values = ((v00 * (1 - tx) + v10 * tx) * (1 - tz)
              + (v01 * (1 - tx) + v11 * tx) * tz).astype(np.float32)

    return HeightmapData(
        values=values,
        resolution=resolution,
        min_elevation_m=full.min_elevation_m,
        max_elevation_m=full.max_elevation_m,
        world_x_min=x_min,
        world_z_min=z_min,
        world_width=size,
        world_height=size,
    )


def bake_chunk(
    col: int,
    row: int,
    full_graph: StreetGraph,
    full_hmap: HeightmapData,
    polygons: Dict[int, "object"],
    boundaries: _Boundaries,
    chunk_size: float,
    hmap_res: int,
    include_sidewalks: bool = True,
    base_x: float = 0.0,
    base_z: float = 0.0,
) -> ChunkData:
    """Produce the ChunkData for one chunk (col, row).

    polygons/boundaries are computed once on the full graph and shared across chunks;
    they are keyed by osm id / edge key so they apply correctly to the cropped graph.
    base_x/base_z anchor the chunk grid at the data's SW corner (see chunk_world_rect).
    """
    x_min, z_min, size, _ = chunk_world_rect(col, row, chunk_size, base_x, base_z)

    graph = full_graph.crop_to_chunk(x_min, z_min, x_min + size, z_min + size)
    hmap = resample_heightmap(full_hmap, x_min, z_min, size, hmap_res)

    # Flatten the heightmap under intersections then roads (in place) before
    # sampling mesh elevations, so terrain and geometry agree.
    stamp_all(hmap, graph, polygons, boundaries)

    meshes: list[MeshEntry] = []

    # Roads — merge the split segments of each way into one mesh so osm_id (= way id)
    # is unique within the chunk (the importer keys assets by <type>_<osm_id>).
    for way_id, (verts, uvs, indices) in _merge_by_id(build_road_meshes(graph, hmap, boundaries)).items():
        meshes.append(MeshEntry(MeshType.ROAD, way_id, verts, [], uvs, indices))

    # Intersections — fan-triangulated per node from the shared polygons.
    for node in graph.intersection_nodes:
        poly = polygons.get(node.osm_id)
        if poly is None:
            continue
        center_y = _sample_elevation(hmap, node.world_x, node.world_z)
        verts, uvs, indices = triangulate_fan(node.world_x, node.world_z, center_y, poly)
        if verts and indices:
            meshes.append(MeshEntry(MeshType.INTERSECTION, node.osm_id, verts, [], uvs, indices))

    # Sidewalks (optional) — same per-way merge as roads.
    if include_sidewalks:
        for way_id, (verts, uvs, indices) in _merge_by_id(build_sidewalk_meshes(graph, hmap, boundaries)).items():
            meshes.append(MeshEntry(MeshType.SIDEWALK, way_id, verts, [], uvs, indices))

    # Buildings (keyed by building osm_id → arrays).
    for osm_id, (verts, uvs, indices) in build_building_meshes(graph, hmap).items():
        if verts and indices:
            meshes.append(MeshEntry(MeshType.BUILDING, osm_id, verts, [], uvs, indices))

    # Collect named road segments from the cropped graph for the street HUD.
    # Multiple StreetEdge objects can share the same way_id (split at intersections);
    # emit one entry per edge so the spatial query covers the full road geometry.
    road_names = [
        (e.name, e.centerline)
        for e in graph.edges
        if e.name
    ]

    return ChunkData(
        col=col,
        row=row,
        world_x=x_min,
        world_z=z_min,
        chunk_size_m=chunk_size,
        heightmap=hmap,
        meshes=meshes,
        road_names=road_names,
    )

"""CropToChunk + per-chunk pipeline orchestration.

Mirrors the per-chunk loop of the old C# SFMapPipelineWindow: the full graph and
full heightmap are built once by the caller, intersection polygons/boundaries are
computed once on the full graph, then each chunk is produced by cropping the graph,
resampling the heightmap into the chunk's world rect, stamping, and building meshes.
"""
from __future__ import annotations

import math
from typing import Dict, Optional, Tuple

import numpy as np

from .elevation import HeightmapData
from .geometry.building import build_building_meshes
from .geometry.intersection import build_sidewalk_corner_meshes, triangulate_fan
from .geometry.road import build_road_meshes
from .geometry.sidewalk import build_sidewalk_meshes
from .osm import StreetGraph
from .serialize import ChunkData, MeshEntry, MeshType
from .stamping import stamp_all

# Boundaries dict type: (way_id, from_node, to_node) -> (from_xz, to_xz)
_Boundaries = Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]

# Metres of valid heightmap kept beyond each chunk edge while baking, so road
# end-caps and intersection fans whose setback anchors fall just across a seam
# sample true neighbour elevation instead of a clamped chunk-edge value (#168).
# Must exceed the largest reach of a junction corner past the node centre,
# = max setback (<= BEVEL_THRESHOLD = 5 m) + max half-width (5 m) ≈ 7 m.
_SAMPLE_MARGIN_M = 10.0


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


def _merge_by_id_n(
    arrays_by_key: Dict[Tuple[int, int, int], Tuple[list, list, list, list]],
) -> Dict[int, Tuple[list, list, list, list]]:
    """Per-way merge for meshes that carry explicit normals (verts, normals, uvs, indices).

    Same id-collision handling as ``_merge_by_id``; roads supply normals so their
    welded end-caps shade continuously with the intersection fans.
    """
    merged: Dict[int, Tuple[list, list, list, list]] = {}
    for (way_id, _f, _t), (verts, normals, uvs, indices) in arrays_by_key.items():
        if not verts or not indices:
            continue
        if way_id not in merged:
            merged[way_id] = ([], [], [], [])
        m_verts, m_normals, m_uvs, m_indices = merged[way_id]
        offset = len(m_verts)
        m_verts.extend(verts)
        m_normals.extend(normals)
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

    # Resample over a rect expanded by a margin of valid terrain on all sides,
    # keeping the same cell pitch. Mesh sampling and stamping then see real
    # neighbour elevation a little past every seam, so a seam-adjacent fan corner
    # or road end-cap anchored just across the boundary lands on true terrain and
    # welds to the arm in the neighbour chunk instead of floating at a clamped
    # edge value (#168). The exported terrain is sliced back to the chunk rect
    # below, so this widening is invisible to the importer.
    cell = size / (hmap_res - 1)
    margin = math.ceil(_SAMPLE_MARGIN_M / cell)
    m_res = hmap_res + 2 * margin
    m_size = (m_res - 1) * cell
    hmap = resample_heightmap(full_hmap, x_min - margin * cell, z_min - margin * cell, m_size, m_res)

    # Flatten the heightmap under intersections then roads (in place) before
    # sampling mesh elevations, so terrain and geometry agree.
    #
    # Stamp from a graph cropped to the *margin-expanded* rect, not the chunk
    # rect. An intersection sitting in the margin band (just outside chunk bounds)
    # with no road crossing the seam is kept by only one of two adjacent chunks'
    # chunk-rect crops, so its stamp would reach the shared edge column in that one
    # chunk only — leaving the two chunks with different terrain heights at the
    # seam (#174). Cropping the stamp graph to the same margin band in both chunks
    # gives them the same intersection set near the seam, so the stamps are
    # symmetric. The chunk-bounded ``graph`` above still drives mesh generation
    # (and the per-node centre-in-rect guards below) so geometry is unaffected.
    margin_m = margin * cell
    stamp_graph = full_graph.crop_to_chunk(
        x_min - margin_m, z_min - margin_m, x_min + size + margin_m, z_min + size + margin_m
    )
    stamp_all(hmap, stamp_graph, polygons, boundaries)

    meshes: list[MeshEntry] = []

    # Roads — merge the split segments of each way into one mesh so osm_id (= way id)
    # is unique within the chunk (the importer keys assets by <type>_<osm_id>).
    for way_id, (verts, normals, uvs, indices) in _merge_by_id_n(build_road_meshes(graph, hmap, boundaries)).items():
        meshes.append(MeshEntry(MeshType.ROAD, way_id, verts, normals, uvs, indices))

    # Intersections — fan-triangulated per node from the shared polygons.
    # Only emit the polygon for nodes whose centre lies inside this chunk's rect;
    # seam nodes appear in the cropped graph as road endpoints but their polygon
    # belongs to the neighbour — emitting it here causes z-fighting duplicates.
    for node in graph.intersection_nodes:
        if not (x_min <= node.world_x <= x_min + size and z_min <= node.world_z <= z_min + size):
            continue
        poly = polygons.get(node.osm_id)
        if poly is None:
            continue
        verts, normals, uvs, indices = triangulate_fan(node.world_x, node.world_z, hmap, poly)
        if verts and indices:
            meshes.append(MeshEntry(MeshType.INTERSECTION, node.osm_id, verts, normals, uvs, indices))

    # Sidewalks (optional) — same per-way merge as roads.
    if include_sidewalks:
        for way_id, (verts, uvs, indices) in _merge_by_id(build_sidewalk_meshes(graph, hmap, boundaries)).items():
            meshes.append(MeshEntry(MeshType.SIDEWALK, way_id, verts, [], uvs, indices))

        # Sidewalk corner fills — one quad per adjacent arm pair at each intersection.
        # Only emit for nodes whose centre lies inside this chunk (same guard as fan).
        for node_id, (verts, uvs, indices) in build_sidewalk_corner_meshes(
            graph, polygons, hmap
        ).items():
            node = graph.nodes.get(node_id)
            if node is None:
                continue
            if not (x_min <= node.world_x <= x_min + size and z_min <= node.world_z <= z_min + size):
                continue
            meshes.append(MeshEntry(MeshType.SIDEWALK, node_id, verts, [], uvs, indices))

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

    # Slice the sampling margin back off: the serialized terrain covers exactly
    # [x_min, x_min+size] at hmap_res. Because the margin is a whole number of
    # cells at the chunk's own cell pitch, the inner block aligns cell-for-cell
    # with an un-margined resample — only seam-adjacent stamping differs (now
    # informed by real cross-seam road continuations rather than clipped stubs).
    export_hmap = HeightmapData(
        values=hmap.values[margin:margin + hmap_res, margin:margin + hmap_res].copy(),
        resolution=hmap_res,
        min_elevation_m=hmap.min_elevation_m,
        max_elevation_m=hmap.max_elevation_m,
        world_x_min=x_min,
        world_z_min=z_min,
        world_width=size,
        world_height=size,
    )

    return ChunkData(
        col=col,
        row=row,
        world_x=x_min,
        world_z=z_min,
        chunk_size_m=chunk_size,
        heightmap=export_hmap,
        meshes=meshes,
        road_names=road_names,
    )

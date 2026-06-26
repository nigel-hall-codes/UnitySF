"""numpy heightmap stamping for roads and intersections."""
from __future__ import annotations

import copy
import math
from typing import Dict, Optional, Tuple

import numpy as np
from shapely.geometry import Polygon

from .elevation import HeightmapData
from .geometry.road import _clip_polyline_to_rect
from .osm import StreetEdge, StreetGraph

_SIDEWALK_WIDTH = 1.5  # metres — must match road.py to stamp the full footprint


def stamp_intersections(
    hmap: HeightmapData,
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
) -> None:
    """Flatten heightmap cells under each intersection polygon (in-place).

    Stamps a circular region whose radius equals the polygon's furthest vertex.
    The target elevation is sampled from the pre-stamp heightmap at the node centre.
    Call this BEFORE stamp_roads — roads may partially overlap intersection zones.
    """
    res = hmap.resolution
    cell_w = hmap.world_width / (res - 1)
    cell_h = hmap.world_height / (res - 1)
    pad = math.hypot(cell_w, cell_h) * 0.5
    elev_range = max(hmap.max_elevation_m - hmap.min_elevation_m, 0.001)

    # Snapshot: each intersection samples its centre grade from pre-stamp
    # terrain so processing order (set iteration) cannot corrupt the lookup.
    hmap_pre = copy.copy(hmap)
    hmap_pre.values = hmap.values.copy()

    for node in graph.intersection_nodes:
        poly = polygons.get(node.osm_id)
        if poly is None:
            continue

        cx, cz = node.world_x, node.world_z

        # Max radius of the polygon offsets from node centre.
        coords = list(poly.exterior.coords[:-1])
        max_r = max(math.hypot(ox, oz) for ox, oz in coords)
        r = max_r + pad
        r_sq = r * r

        # Stamp to the elevation sampled at the node centre before any changes.
        elevation = hmap_pre.sample_bilinear(cx, cz) * (hmap.max_elevation_m - hmap.min_elevation_m) + hmap.min_elevation_m
        normalized = (elevation - hmap.min_elevation_m) / elev_range

        clamp_r = r + pad * 2
        clamp_r_sq = clamp_r * clamp_r

        col_min = max(0,       int((cx - clamp_r - hmap.world_x_min) / cell_w))
        col_max = min(res - 1, int((cx + clamp_r - hmap.world_x_min) / cell_w) + 1)
        row_min = max(0,       int((cz - clamp_r - hmap.world_z_min) / cell_h))
        row_max = min(res - 1, int((cz + clamp_r - hmap.world_z_min) / cell_h) + 1)

        for row in range(row_min, row_max + 1):
            wz = hmap.world_z_min + row * cell_h
            dz = wz - cz
            for col in range(col_min, col_max + 1):
                wx = hmap.world_x_min + col * cell_w
                dx = wx - cx
                dist_sq = dx * dx + dz * dz
                if dist_sq <= r_sq:
                    hmap.values[row, col] = normalized
                elif dist_sq <= clamp_r_sq and hmap.values[row, col] > normalized:
                    hmap.values[row, col] = normalized


def _iter_road_footprint(hmap, cl, sampled_y, half_w, pad, elev_range):
    """Yield (row, col, lateral, along, seg_len, normalized_elev) for every cell
    within clamp_w of any segment in the clipped centerline cl."""
    res = hmap.resolution
    cell_w = hmap.world_width / (res - 1)
    cell_h = hmap.world_height / (res - 1)
    stamp_w = half_w + _SIDEWALK_WIDTH + pad * 2
    clamp_w = stamp_w + pad * 2
    x_min = hmap.world_x_min
    z_min = hmap.world_z_min
    e_min = hmap.min_elevation_m

    for seg in range(len(cl) - 1):
        p0x, p0z = cl[seg]
        p1x, p1z = cl[seg + 1]
        p0y = sampled_y[seg]
        p1y = sampled_y[seg + 1]
        dx = p1x - p0x
        dz = p1z - p0z
        seg_len = math.hypot(dx, dz)
        if seg_len < 0.001:
            continue
        nx, nz = dx / seg_len, dz / seg_len
        perpx, perpz = -nz, nx

        col_min = max(0,       int((min(p0x, p1x) - clamp_w - x_min) / cell_w))
        col_max = min(res - 1, int((max(p0x, p1x) + clamp_w - x_min) / cell_w) + 1)
        row_min = max(0,       int((min(p0z, p1z) - clamp_w - z_min) / cell_h))
        row_max = min(res - 1, int((max(p0z, p1z) + clamp_w - z_min) / cell_h) + 1)

        for row in range(row_min, row_max + 1):
            wz = z_min + row * cell_h
            for col in range(col_min, col_max + 1):
                wx = x_min + col * cell_w
                to_px = wx - p0x
                to_pz = wz - p0z
                along   = to_px * nx    + to_pz * nz
                lateral = abs(to_px * perpx + to_pz * perpz)
                if along < -pad or along > seg_len + pad or lateral > clamp_w:
                    continue
                t = max(0.0, min(1.0, along / seg_len))
                elev = p0y + t * (p1y - p0y)
                normalized_elev = (elev - e_min) / elev_range
                yield row, col, lateral, along, seg_len, normalized_elev


def stamp_roads(
    hmap: HeightmapData,
    graph: StreetGraph,
    boundaries: Optional[Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> None:
    """Flatten heightmap cells under each road segment (in-place).

    Three-pass ownership stamp so the closest road to each cell always wins
    regardless of processing order, while still allowing non-owner roads to
    only lower (never raise) cells they don't own:

      Pass 1  — compute per-cell minimum lateral distance across all roads.
      Pass 2A — owner roads SET cells (raise or lower to road grade).
      Pass 2B — non-owner roads MIN cells (only lower; no contamination).

    The end-cap overshoot (±pad beyond segment endpoints) and the clamp-zone
    ring (stamp_w..clamp_w) are handled in 2B only, since they should never
    raise terrain.
    """
    res = hmap.resolution
    cell_w = hmap.world_width / (res - 1)
    cell_h = hmap.world_height / (res - 1)
    pad = math.hypot(cell_w, cell_h) * 0.5
    elev_range = max(hmap.max_elevation_m - hmap.min_elevation_m, 0.001)

    # Snapshot: each road samples its grade from pre-road-stamp terrain so
    # road processing order cannot corrupt elevation lookups.
    hmap_snap = copy.copy(hmap)
    hmap_snap.values = hmap.values.copy()

    # Pre-compute clipped centerlines and snap-sampled grades for all edges.
    edge_data = []
    for edge in graph.edges:
        if edge.width <= 0.0:
            continue
        cl = _clip_polyline_to_rect(
            edge.centerline,
            hmap.world_x_min, hmap.world_z_min,
            hmap.world_x_min + hmap.world_width,
            hmap.world_z_min + hmap.world_height,
        )
        if len(cl) < 2:
            continue
        sampled_y = [
            hmap_snap.sample_bilinear(x, z) * (hmap.max_elevation_m - hmap.min_elevation_m) + hmap.min_elevation_m
            for x, z in cl
        ]
        edge_data.append((edge.osm_way_id, edge.width * 0.5, cl, sampled_y))

    # Pass 1: determine which road's centerline is closest to each cell.
    # Also track the owner's half_w and way_id so Pass 2B can protect the
    # bilinear-influence zone around each road's edge from being lowered.
    min_lateral = np.full((res, res), np.inf, dtype=np.float32)
    owner_half_w_arr = np.zeros((res, res), dtype=np.float32)
    owner_way_arr = np.full((res, res), -1, dtype=np.int64)
    for way_id, half_w, cl, sampled_y in edge_data:
        stamp_w = half_w + _SIDEWALK_WIDTH + pad * 2
        for row, col, lateral, along, seg_len, _ in _iter_road_footprint(
                hmap, cl, sampled_y, half_w, pad, elev_range):
            if lateral <= stamp_w and lateral < min_lateral[row, col]:
                min_lateral[row, col] = lateral
                owner_half_w_arr[row, col] = half_w
                owner_way_arr[row, col] = way_id

    # Pass 2A: owner SETs cells — closest road raises or lowers to its grade.
    for way_id, half_w, cl, sampled_y in edge_data:
        stamp_w = half_w + _SIDEWALK_WIDTH + pad * 2
        for row, col, lateral, along, seg_len, normalized_elev in _iter_road_footprint(
                hmap, cl, sampled_y, half_w, pad, elev_range):
            if lateral <= stamp_w and lateral <= min_lateral[row, col] + 1e-5:
                hmap.values[row, col] = normalized_elev

    # Pass 2B: non-owners MIN — can only lower, never raise.
    # Two protections:
    #   (a) Same-way skip: in the bilinear margin (lateral > half_w) different
    #       StreetEdge objects from the same OSM way should not fight each other
    #       at segment boundaries.  Inside the road surface (lateral <= half_w)
    #       a descending segment must be able to lower cells stamped by the
    #       end-cap of the adjacent same-way segment — otherwise terrain can
    #       sit above the road plane at steep intersection entries.
    #   (b) Edge bilinear protection: cells within owner_half_w + pad of the owner
    #       centerline are protected from being lowered by non-owner roads that do
    #       NOT geometrically cover the cell (lateral > half_w).  Roads that DO
    #       cover the cell (lateral ≤ half_w) can still lower, since they share the
    #       same road-surface footprint and the cell determines both roads' vertices.
    for way_id, half_w, cl, sampled_y in edge_data:
        stamp_w = half_w + _SIDEWALK_WIDTH + pad * 2
        for row, col, lateral, along, seg_len, normalized_elev in _iter_road_footprint(
                hmap, cl, sampled_y, half_w, pad, elev_range):
            is_owner = lateral <= stamp_w and lateral <= min_lateral[row, col] + 1e-5
            if is_owner:
                continue
            if way_id == owner_way_arr[row, col] and lateral > half_w:
                continue  # (a) same OSM way, outside road surface: skip bilinear-margin grade fights
            if lateral > half_w and min_lateral[row, col] <= owner_half_w_arr[row, col] + pad:
                continue  # (b) outside this road's surface and in owner's bilinear zone
            if hmap.values[row, col] > normalized_elev:
                hmap.values[row, col] = normalized_elev


def stamp_all(
    hmap: HeightmapData,
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
    boundaries: Optional[Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> None:
    """Apply the full stamp sequence: intersections first, then roads.

    This is the canonical entry point for the per-chunk stamping stage.
    Modifies hmap.values in place.
    """
    stamp_intersections(hmap, graph, polygons)
    stamp_roads(hmap, graph, boundaries)

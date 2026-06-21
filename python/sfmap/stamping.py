"""numpy heightmap stamping for roads and intersections."""
from __future__ import annotations

import math
from typing import Dict, Optional, Tuple

import numpy as np
from shapely.geometry import Polygon

from .elevation import HeightmapData
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
        elevation = hmap.sample_bilinear(cx, cz) * (hmap.max_elevation_m - hmap.min_elevation_m) + hmap.min_elevation_m
        normalized = (elevation - hmap.min_elevation_m) / elev_range

        col_min = max(0,       int((cx - r - hmap.world_x_min) / cell_w))
        col_max = min(res - 1, int((cx + r - hmap.world_x_min) / cell_w) + 1)
        row_min = max(0,       int((cz - r - hmap.world_z_min) / cell_h))
        row_max = min(res - 1, int((cz + r - hmap.world_z_min) / cell_h) + 1)

        for row in range(row_min, row_max + 1):
            wz = hmap.world_z_min + row * cell_h
            dz = wz - cz
            for col in range(col_min, col_max + 1):
                wx = hmap.world_x_min + col * cell_w
                dx = wx - cx
                if dx * dx + dz * dz <= r_sq:
                    hmap.values[row, col] = normalized


def stamp_roads(
    hmap: HeightmapData,
    graph: StreetGraph,
    boundaries: Optional[Dict[int, Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> None:
    """Flatten heightmap cells under each road segment (in-place).

    Stamps a rectangular footprint along each edge's centerline. The stamp
    width covers the road width + sidewalk width + half-cell diagonal padding.
    Call this AFTER stamp_intersections.
    """
    res = hmap.resolution
    cell_w = hmap.world_width / (res - 1)
    cell_h = hmap.world_height / (res - 1)
    pad = math.hypot(cell_w, cell_h) * 0.5
    elev_range = max(hmap.max_elevation_m - hmap.min_elevation_m, 0.001)

    for edge in graph.edges:
        if edge.width <= 0.0:
            continue

        half_w = edge.width * 0.5
        stamp_w = half_w + _SIDEWALK_WIDTH + pad

        # Build the sampled centerline (Y = terrain elevation at each point).
        cl = edge.centerline
        sampled_y = [
            hmap.sample_bilinear(x, z) * (hmap.max_elevation_m - hmap.min_elevation_m) + hmap.min_elevation_m
            for x, z in cl
        ]

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
            nx, nz = dx / seg_len, dz / seg_len  # forward unit vector
            px, pz = -nz, nx                       # left perpendicular (XZ)

            col_min = max(0,       int((min(p0x, p1x) - stamp_w - hmap.world_x_min) / cell_w))
            col_max = min(res - 1, int((max(p0x, p1x) + stamp_w - hmap.world_x_min) / cell_w) + 1)
            row_min = max(0,       int((min(p0z, p1z) - stamp_w - hmap.world_z_min) / cell_h))
            row_max = min(res - 1, int((max(p0z, p1z) + stamp_w - hmap.world_z_min) / cell_h) + 1)

            for row in range(row_min, row_max + 1):
                wz = hmap.world_z_min + row * cell_h
                for col in range(col_min, col_max + 1):
                    wx = hmap.world_x_min + col * cell_w
                    to_px = wx - p0x
                    to_pz = wz - p0z
                    along = to_px * nx + to_pz * nz
                    lateral = abs(to_px * px + to_pz * pz)
                    if along < -pad or along > seg_len + pad or lateral > stamp_w:
                        continue
                    t = max(0.0, min(1.0, along / seg_len))
                    elev = p0y + t * (p1y - p0y)
                    hmap.values[row, col] = (elev - hmap.min_elevation_m) / elev_range


def stamp_all(
    hmap: HeightmapData,
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
    boundaries: Optional[Dict[int, Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> None:
    """Apply the full stamp sequence: intersections first, then roads.

    This is the canonical entry point for the per-chunk stamping stage.
    Modifies hmap.values in place.
    """
    stamp_intersections(hmap, graph, polygons)
    stamp_roads(hmap, graph, boundaries)

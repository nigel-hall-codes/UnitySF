"""Diagnostic for #168 — measure intersection-fan ↔ road-arm weld rate.

Bakes the full map in memory (no serialization), then for every INTERSECTION
fan rim corner checks whether a ROAD vertex coincides with it in XZ *and* Y
(across all chunks). Reports the overall weld rate and isolates seam-adjacent
corners (those within `SEAM_BAND` m of a chunk boundary), which #168 says fail.

Run from python/ dir:
    ../../../python/.venv/Scripts/python.exe diag_168.py
"""
from __future__ import annotations

import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sfmap import osm, elevation
from sfmap.chunk import bake_chunk, chunk_world_rect
from sfmap.geometry import intersection
from sfmap.serialize import MeshType

REPO_ROOT = Path(__file__).resolve().parents[3]   # …/UnitySF (main checkout)
OSM_FILE = REPO_ROOT / "My project (2)" / "Assets" / "SFMapData" / "map.osm"
ELEV_FILE = REPO_ROOT / "My project (2)" / "Assets" / "SFMapData" / "Elevation_Contours_20260619.csv"

CHUNK_SIZE = 300.0
HMAP_RES = 129
VEXAG = 1.3

EPS_XZ = 0.10   # m — corners closer than this in XZ are "the same point"
EPS_Y = 0.10    # m — and must agree in elevation to count as welded
SEAM_BAND = 4.0  # m — a corner within this of a chunk edge is "seam-adjacent"

# Known seam-adjacent failing nodes from the issue.
WATCH = {261482484, 4045035893, 65316388, 12084192107, 65316450}


def main() -> int:
    print(f"[diag168] parsing {OSM_FILE.name}")
    graph = osm.parse(str(OSM_FILE))
    hmap = elevation.parse(str(ELEV_FILE), graph.source_bounds, graph.origin, HMAP_RES)
    elevation.apply_vertical_exaggeration(hmap, VEXAG)
    polygons = intersection.compute_polygons(graph)
    boundaries = intersection.compute_boundaries(graph, polygons)

    xs = [n.world_x for n in graph.nodes.values()]
    zs = [n.world_z for n in graph.nodes.values()]
    for e in graph.edges:
        for x, z in e.centerline:
            xs.append(x); zs.append(z)
    base_x, base_z = min(xs), min(zs)
    chunks_x = max(1, math.ceil((max(xs) - base_x) / CHUNK_SIZE))
    chunks_z = max(1, math.ceil((max(zs) - base_z) / CHUNK_SIZE))
    print(f"[diag168] grid {chunks_x}x{chunks_z}, baking in memory…")

    # Collect every ROAD vertex (XZ-hashed) and every INTERSECTION rim corner.
    road_cells: dict = defaultdict(list)   # (cx,cz) -> [(x,z,y)]
    corners = []   # (node_id, x, z, y, dist_to_seam)

    def hkey(x, z):
        return (int(math.floor(x / EPS_XZ)), int(math.floor(z / EPS_XZ)))

    for row in range(chunks_z):
        for col in range(chunks_x):
            chunk = bake_chunk(col, row, graph, hmap, polygons, boundaries,
                               CHUNK_SIZE, HMAP_RES, True, base_x=base_x, base_z=base_z)
            cxmin, czmin, csize, _ = chunk_world_rect(col, row, CHUNK_SIZE, base_x, base_z)
            for m in chunk.meshes:
                if m.mesh_type == MeshType.ROAD:
                    for (x, y, z) in m.vertices:
                        road_cells[hkey(x, z)].append((x, z, y))
                elif m.mesh_type == MeshType.INTERSECTION:
                    for (x, y, z) in m.vertices[1:]:   # skip apex
                        d = min(x - cxmin, cxmin + csize - x, z - czmin, czmin + csize - z)
                        corners.append((m.osm_id, x, z, y, d))

    def welded(x, z, y):
        bx, bz = hkey(x, z)
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for (rx, rz, ry) in road_cells.get((bx + dx, bz + dz), ()):
                    if abs(rx - x) <= EPS_XZ and abs(rz - z) <= EPS_XZ and abs(ry - y) <= EPS_Y:
                        return True
        return False

    total = seam = total_w = seam_w = 0
    watch_stats = defaultdict(lambda: [0, 0])  # node -> [welded, total]
    for (nid, x, z, y, d) in corners:
        w = welded(x, z, y)
        total += 1; total_w += w
        if d <= SEAM_BAND:
            seam += 1; seam_w += w
        if nid in WATCH:
            watch_stats[nid][0] += w; watch_stats[nid][1] += 1

    print(f"\n[diag168] EPS_XZ={EPS_XZ} EPS_Y={EPS_Y} SEAM_BAND={SEAM_BAND}")
    print(f"  all corners:          {total_w}/{total}  = {100*total_w/max(1,total):.2f}% welded")
    print(f"  seam-adjacent (<={SEAM_BAND}m): {seam_w}/{seam}  = {100*seam_w/max(1,seam):.2f}% welded")
    print(f"  interior corners:     {total_w-seam_w}/{total-seam}  = {100*(total_w-seam_w)/max(1,total-seam):.2f}% welded")
    print(f"  watched nodes:")
    for nid in sorted(watch_stats):
        w, t = watch_stats[nid]
        print(f"    {nid}: {w}/{t} welded")
    return 0


if __name__ == "__main__":
    sys.exit(main())

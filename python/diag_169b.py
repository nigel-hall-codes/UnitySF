"""Verify corner fill geometry for intersection_65280473.

Run from worktree root:
    python python/diag_169b.py
"""
from __future__ import annotations
import math, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sfmap import osm as sfosm
from sfmap.geometry import intersection as sfi
from sfmap import elevation as sfelev

TARGET_NODE = 65280473
OSM_FILE = "C:/Users/nigel/GameDev/UnitySF/My project (2)/Assets/SFMapData/map.osm"
ELEV_FILE = "C:/Users/nigel/GameDev/UnitySF/My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv"


def main():
    graph = sfosm.parse(OSM_FILE)
    print(f"parsed: {len(graph.nodes)} nodes, {len(graph.edges)} edges")

    polygons = sfi.compute_polygons(graph)

    # Use a dummy heightmap centred on node 65280473 for elevation sampling
    node = graph.nodes[TARGET_NODE]
    hmap = sfelev.parse(ELEV_FILE, graph.source_bounds, graph.origin, resolution=129)

    corners = sfi.build_sidewalk_corner_meshes(graph, polygons, hmap)

    total_nodes = len(corners)
    print(f"corner fills generated for {total_nodes} intersection nodes")

    node_corners = corners.get(TARGET_NODE)
    if node_corners is None:
        print(f"No corners for node {TARGET_NODE}")
        return 1

    verts, uvs, indices = node_corners
    n_quads = len(indices) // 6
    print(f"\nNode {TARGET_NODE}: {n_quads} corner-fill quads, {len(verts)} vertices")

    for q in range(n_quads):
        base = q * 4
        pts = verts[base:base+4]
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        zs = [p[2] for p in pts]
        # Compute signed area (using first 3 vertices, XZ)
        ax, az = pts[0][0]-pts[2][0], pts[0][2]-pts[2][2]
        bx, bz = pts[1][0]-pts[2][0], pts[1][2]-pts[2][2]
        # 2D cross product
        cross = ax*bz - az*bx
        print(f"  quad[{q}]: {n_quads} quads total, cross={cross:+.3f} (neg=front-facing)"
              f"  x[{min(xs):.1f},{max(xs):.1f}] z[{min(zs):.1f},{max(zs):.1f}]"
              f"  y[{min(ys):.2f},{max(ys):.2f}]")

    print("\nTriangle winding check (all should be negative = front-facing):")
    any_bad = False
    for t in range(0, len(indices), 3):
        a, b, c = indices[t], indices[t+1], indices[t+2]
        ax = verts[b][0]-verts[a][0]; az = verts[b][2]-verts[a][2]
        bx = verts[c][0]-verts[a][0]; bz = verts[c][2]-verts[a][2]
        cross = ax*bz - az*bx
        if cross >= 0:
            print(f"  WARN: triangle {t//3} has cross={cross:+.4f} (should be negative)")
            any_bad = True
    if not any_bad:
        print("  All triangles are front-facing.")

    # Show arm-gap mapping to see which gaps were skipped
    print("\nArm pair gaps at this node:")
    edge_arms, setbacks = sfi._arms_and_setbacks(node, graph)
    n = len(edge_arms)
    for i in range(n):
        j = (i+1) % n
        a, b = edge_arms[i].arm, edge_arms[j].arm
        gap = (math.degrees(b.angle - a.angle)) % 360.0
        skipped = gap < sfi._MIN_CORNER_GAP_DEG
        print(f"  arm[{i}]->arm[{j}]  gap={gap:.1f}  {'SKIPPED (too tight)' if skipped else 'filled'}")

    return 0

if __name__ == "__main__":
    sys.exit(main())

"""Diagnostic for #169 — inspect intersection_65280473 geometry in detail.

Run from the worktree root:
    python python/diag_169.py
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sfmap import osm as sfosm
from sfmap.geometry import intersection as sfi

TARGET_NODE = 65280473
OSM_FILE = Path("C:/Users/nigel/GameDev/UnitySF/My project (2)/Assets/SFMapData/map.osm")


def fmt_angle(a: float) -> str:
    return f"{math.degrees(a) % 360:.1f}deg"


def main() -> int:
    print(f"[diag_169] parsing {OSM_FILE}")
    graph = sfosm.parse(str(OSM_FILE))
    print(f"[diag_169] {len(graph.nodes)} nodes, {len(graph.edges)} edges, "
          f"{len(graph.intersection_nodes)} intersection nodes\n")

    node = graph.nodes.get(TARGET_NODE)
    if node is None:
        print(f"ERROR: node {TARGET_NODE} not found in graph")
        return 1

    print(f"=== Node {TARGET_NODE} ===")
    print(f"  world pos: ({node.world_x:.3f}, {node.world_z:.3f})")
    print(f"  is_intersection: {node.is_intersection}")
    print()

    # --- Arms ---------------------------------------------------------------
    edge_arms = sfi._collect_edge_arms(node, graph)
    edge_arms.sort(key=lambda ea: ea.arm.angle)
    print(f"  Arms ({len(edge_arms)} total):")
    for i, ea in enumerate(edge_arms):
        a = ea.arm
        print(f"    [{i}] angle={fmt_angle(a.angle):>8}  hw={a.half_width:.1f}m  "
              f"dir=({a.dir_x:+.3f}, {a.dir_z:+.3f})  "
              f"is_from={ea.is_from}  "
              f"edge={ea.edge.osm_way_id}  name={ea.edge.name!r}")
    print()

    # --- Setbacks -----------------------------------------------------------
    edge_arms_s, setbacks = sfi._arms_and_setbacks(node, graph)
    print(f"  Setbacks:")
    for i, (ea, t) in enumerate(zip(edge_arms_s, setbacks)):
        bx = node.world_x + ea.arm.dir_x * t
        bz = node.world_z + ea.arm.dir_z * t
        print(f"    [{i}] t={t:.3f}m  boundary=({bx:.3f}, {bz:.3f})")
    print()

    # --- Join pairs ---------------------------------------------------------
    n = len(edge_arms_s)
    print(f"  Join setbacks (per adjacent pair):")
    for i in range(n):
        j = (i + 1) % n
        a, b = edge_arms_s[i].arm, edge_arms_s[j].arm
        ta, tb = sfi._join_setbacks(a, b)
        gap_deg = (math.degrees(b.angle - a.angle)) % 360
        print(f"    arm[{i}]->arm[{j}]  gap={gap_deg:.1f}deg  "
              f"t_a={ta:.3f}m  t_b={tb:.3f}m")
    print()

    # --- Polygon -----------------------------------------------------------
    polygons = sfi.compute_polygons(graph)
    poly = polygons.get(TARGET_NODE)
    if poly is None:
        print("  ERROR: compute_polygons returned no polygon for this node")
        return 1

    coords = list(poly.exterior.coords[:-1])
    print(f"  Polygon: {len(coords)} vertices  area={poly.area:.2f}m²  "
          f"valid={poly.is_valid}  simple={poly.is_simple}")
    for i, (x, z) in enumerate(coords):
        r = math.hypot(x, z)
        print(f"    [{i:2}] xz=({x:+7.3f}, {z:+7.3f})  r={r:.3f}m")
    print()

    # --- Boundary setback positions ----------------------------------------
    boundaries = sfi.compute_boundaries(graph, polygons)
    print(f"  Boundary endpoints for edges touching this node:")
    for ea in edge_arms_s:
        key = (ea.edge.osm_way_id, ea.edge.from_node.osm_id, ea.edge.to_node.osm_id)
        bd = boundaries.get(key, (None, None))
        from_bd, to_bd = bd
        which = "from" if ea.is_from else "to"
        pt = from_bd if ea.is_from else to_bd
        print(f"    edge {ea.edge.osm_way_id}  ({which} end)  boundary={pt}")
    print()

    # --- ASCII plan view (scaled) ------------------------------------------
    print("  ASCII plan view (●=node centre, numbers=arm directions):")
    scale = 3.0  # pixels per metre
    grid_r = 10
    grid = [['.' for _ in range(grid_r * 2 + 1)] for _ in range(grid_r * 2 + 1)]
    # Mark centre
    grid[grid_r][grid_r] = '●'
    # Mark arm directions
    for i, ea in enumerate(edge_arms_s):
        a = ea.arm
        for step in range(1, 8):
            gx = round(a.dir_x * step * scale) + grid_r
            gz = round(a.dir_z * step * scale) + grid_r
            if 0 <= gz < len(grid) and 0 <= gx < len(grid[0]):
                grid[gz][gx] = str(i)
    # Mark polygon vertices
    for x, z in coords:
        gx = round(x * scale) + grid_r
        gz = round(z * scale) + grid_r
        if 0 <= gz < len(grid) and 0 <= gx < len(grid[0]):
            if grid[gz][gx] == '.':
                grid[gz][gx] = '+'
    for row in reversed(grid):   # flip Z so +Z is "up" on screen
        print('    ' + ' '.join(row))
    print()

    # --- Sanity checks -------------------------------------------------------
    print("  Sanity checks:")
    max_hw = max((ea.arm.half_width for ea in edge_arms_s), default=0)
    max_r = max((math.hypot(x, z) for x, z in coords), default=0)
    print(f"    max half-width across arms : {max_hw:.2f}m")
    print(f"    max polygon vertex radius  : {max_r:.2f}m")
    print(f"    spike ratio (max_r / max_hw): {max_r/max_hw:.2f}  (>2.5 = problem)")
    # Check any arm has near-zero angular gap with its neighbour
    for i in range(n):
        j = (i + 1) % n
        gap = (math.degrees(edge_arms_s[j].arm.angle - edge_arms_s[i].arm.angle)) % 360
        if gap < 10.0:
            print(f"    WARNING: arms {i} and {j} are only {gap:.1f}° apart — near-parallel/degenerate")
    # Check setbacks that exceed road length
    for i, (ea, t) in enumerate(zip(edge_arms_s, setbacks)):
        cl = ea.edge.centerline
        road_len = sum(math.hypot(cl[k+1][0]-cl[k][0], cl[k+1][1]-cl[k][1])
                       for k in range(len(cl)-1))
        if t > road_len * 0.9:
            print(f"    WARNING: arm {i} setback {t:.2f}m is {t/road_len*100:.0f}% of road length {road_len:.2f}m")

    return 0


if __name__ == "__main__":
    sys.exit(main())

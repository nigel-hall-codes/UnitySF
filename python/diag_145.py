"""Diagnostic for #145 — classify intersection polygon failure modes.

Loads the real OSM graph, runs compute_polygons, and for every intersection
node reports:
  - arm count + sorted arm angles (deg) + half-widths
  - polygon validity (shapely is_valid / is_simple)
  - convexity (does the polygon equal its convex hull area?)
  - spikes: vertices whose distance from centre >> the road half-widths
  - self-intersection (the 'star' signature)

Run from repo root:
    python worktrees/sdlc-145-.../python/diag_145.py
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]   # …/UnitySF (worktree root)
# Prefer the worktree's own python package
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from sfmap import osm as sfosm                       # noqa: E402
from sfmap.geometry import intersection as sfi        # noqa: E402

OSM_FILE = REPO_ROOT.parents[0] / "UnitySF" / "My project (2)" / "Assets" / "SFMapData" / "map.osm"
# Fall back to the main checkout's map if the worktree doesn't carry SFMapData.
if not OSM_FILE.exists():
    OSM_FILE = Path("C:/Users/nigel/GameDev/UnitySF/My project (2)/Assets/SFMapData/map.osm")


def angle_deg(a: float) -> float:
    return math.degrees(a) % 360.0


def main() -> int:
    print(f"[diag] parsing {OSM_FILE}")
    graph = sfosm.parse(str(OSM_FILE))
    print(f"[diag] {len(graph.nodes)} nodes, {len(graph.edges)} edges, "
          f"{len(graph.intersection_nodes)} intersection nodes")

    polygons = sfi.compute_polygons(graph)
    print(f"[diag] compute_polygons returned {len(polygons)} polygons\n")

    n_invalid = 0
    n_selfint = 0
    n_nonconvex = 0
    n_spike = 0
    worst = []   # (spike_ratio, node_id, info)

    for node in graph.intersection_nodes:
        poly = polygons.get(node.osm_id)
        if poly is None:
            continue

        arms = sfi._collect_arms(node, graph)
        arms.sort(key=lambda a: a.angle)
        angles = [round(angle_deg(a.angle), 1) for a in arms]
        hws = [round(a.half_width, 1) for a in arms]
        max_hw = max((a.half_width for a in arms), default=1.0)

        coords = list(poly.exterior.coords[:-1])
        radii = [math.hypot(x, z) for x, z in coords]
        max_r = max(radii) if radii else 0.0
        spike_ratio = max_r / max_hw if max_hw > 0 else 0.0

        is_simple = poly.is_simple
        is_valid = poly.is_valid
        hull = poly.convex_hull
        # convex if polygon area ~= hull area (within 1%)
        convex = poly.area > 0 and abs(hull.area - poly.area) / hull.area < 0.01

        if not is_valid:
            n_invalid += 1
        if not is_simple:
            n_selfint += 1
        if not convex:
            n_nonconvex += 1
        if spike_ratio > 2.0:
            n_spike += 1

        flag = (not is_simple) or (not is_valid) or spike_ratio > 2.5
        if flag:
            worst.append((spike_ratio, node.osm_id, len(arms), angles, hws,
                          is_simple, is_valid, convex, round(max_r, 1)))

    print(f"[diag] invalid polygons       : {n_invalid}")
    print(f"[diag] self-intersecting (star): {n_selfint}")
    print(f"[diag] non-convex             : {n_nonconvex}")
    print(f"[diag] spike (max_r > 2x hw)  : {n_spike}")
    print()

    worst.sort(reverse=True)
    print("=== worst 25 (spike_ratio, node, arms, angles°, halfwidths, simple, valid, convex, max_r) ===")
    for w in worst[:25]:
        sr, nid, na, angs, hws, simp, val, conv, mr = w
        print(f"  ratio={sr:5.2f} node={nid:>12} arms={na} simple={simp!s:5} "
              f"valid={val!s:5} convex={conv!s:5} max_r={mr:6.1f}  angles={angs} hw={hws}")

    return 0


if __name__ == "__main__":
    sys.exit(main())

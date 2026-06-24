"""Spike #141: Identify intersection failure mode at chunk_05_02.

Run from the repo root:
    python worktrees/sdlc-141-spike-confirm-failure-mode/python/spike_141.py

Checks:
  1. INTERSECTION mesh count in chunk_05_02 and its 8 neighbors.
  2. For intersection nodes near 05_02: world position vs chunk bounds,
     is_intersection flag, and which chunk they land in.
  3. Highway-way cross-reference: do visually-crossing ways near the seam
     share any node ID?
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# Paths (relative to repo root — run from there)
# ---------------------------------------------------------------------------
REPO_ROOT  = Path(__file__).resolve().parents[1]   # …/UnitySF
CHUNKS_DIR = REPO_ROOT / "chunks_out"
OSM_FILE   = REPO_ROOT / "My project (2)" / "Assets" / "SFMapData" / "map.osm"

sys.path.insert(0, str(REPO_ROOT / "python"))
from sfmap import osm as sfosm   # noqa: E402  (import after path setup)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
MESH_TYPE_NAMES = {0: "ROAD", 1: "INTERSECTION", 2: "SIDEWALK", 3: "BUILDING"}


def parse_bin(path: Path) -> list[tuple[str, int]]:
    """Return list of (mesh_type_name, osm_id) for every mesh in a .bin file."""
    with open(path, "rb") as f:
        hdr = struct.unpack("<IIiifffffi", f.read(40))
        hmap_res = hdr[9]
        f.read(hmap_res * hmap_res * 4)           # skip heightmap
        (mesh_count,) = struct.unpack("<i", f.read(4))
        meshes: list[tuple[str, int]] = []
        for _ in range(mesh_count):
            (mt,)           = struct.unpack("<B",  f.read(1))
            (osm_id,)       = struct.unpack("<q",  f.read(8))
            vert_c, idx_c   = struct.unpack("<ii", f.read(8))
            f.read(vert_c * 3 * 4)   # verts
            f.read(vert_c * 3 * 4)   # normals
            f.read(vert_c * 2 * 4)   # uvs
            f.read(idx_c * 4)        # indices
            meshes.append((MESH_TYPE_NAMES.get(mt, str(mt)), osm_id))
    return meshes


def in_rect(x: float, z: float, x0: float, z0: float, x1: float, z1: float) -> bool:
    return x0 <= x <= x1 and z0 <= z <= z1


# ---------------------------------------------------------------------------
# Step 1 — INTERSECTION mesh scan
# ---------------------------------------------------------------------------
print("=" * 60)
print("Step 2: INTERSECTION meshes in chunk_05_02 and neighbors")
print("=" * 60)

TARGET    = (5, 2)
NEIGHBORS = [(c, r) for c in range(4, 7) for r in range(1, 4) if (c, r) != TARGET]

isect_ids_by_chunk: dict[tuple[int, int], list[int]] = {}

for col, row in [TARGET] + NEIGHBORS:
    path = CHUNKS_DIR / f"chunk_{col:02d}_{row:02d}.bin"
    label = " <-- TARGET" if (col, row) == TARGET else ""
    if not path.exists():
        print(f"  chunk_{col:02d}_{row:02d}: MISSING{label}")
        continue
    meshes  = parse_bin(path)
    isects  = [oid for tname, oid in meshes if tname == "INTERSECTION"]
    isect_ids_by_chunk[(col, row)] = isects
    print(f"  chunk_{col:02d}_{row:02d}: {len(isects):2d} INTERSECTION mesh(es){label}")
    for oid in isects:
        print(f"    osm_id={oid}")

target_isects = isect_ids_by_chunk.get(TARGET, [])
neighbor_isects_flat = [
    (oid, chunk)
    for chunk, ids in isect_ids_by_chunk.items()
    if chunk != TARGET
    for oid in ids
]

# ---------------------------------------------------------------------------
# Step 2 — OSM graph analysis
# ---------------------------------------------------------------------------
print()
print("=" * 60)
print("Step 3: OSM topology near chunk_05_02")
print("=" * 60)

# Load manifest for chunk world rects
with open(CHUNKS_DIR / "manifest.json") as f:
    manifest = json.load(f)
chunk_size = manifest["chunkSize"]

def chunk_rect(col: int, row: int) -> tuple[float, float, float, float]:
    """(x0, z0, x1, z1) from manifest."""
    entry = next((c for c in manifest["chunks"] if c["col"] == col and c["row"] == row), None)
    if entry is None:
        raise ValueError(f"chunk {col},{row} not in manifest")
    wx, wz = entry["worldX"], entry["worldZ"]
    return wx, wz, wx + chunk_size, wz + chunk_size

x0, z0, x1, z1 = chunk_rect(*TARGET)
print(f"  chunk_05_02 world rect: x[{x0:.1f}, {x1:.1f}]  z[{z0:.1f}, {z1:.1f}]")

print()
print("  Parsing OSM graph (this may take a few seconds)…")
graph = sfosm.parse(str(OSM_FILE))
print(f"  Graph: {len(graph.nodes)} nodes, {len(graph.edges)} edges")

# --- 3a. Intersection nodes near 05_02 (within 1 chunk of its bounds) -------
margin = chunk_size
print()
print(f"  Intersection nodes within {margin:.0f}m margin of chunk_05_02:")

nearby_isect_nodes = [
    n for n in graph.intersection_nodes
    if in_rect(n.world_x, n.world_z,
               x0 - margin, z0 - margin, x1 + margin, z1 + margin)
]

if not nearby_isect_nodes:
    print("    NONE — no intersection nodes near this chunk at all")
else:
    for n in nearby_isect_nodes:
        inside = in_rect(n.world_x, n.world_z, x0, z0, x1, z1)
        tag = "IN  05_02" if inside else "OUT 05_02"
        print(f"    [{tag}] osm_id={n.osm_id:12d}  world=({n.world_x:8.1f}, {n.world_z:8.1f})")

# --- 3b. Check which INTERSECTION mesh IDs from .bin are in the graph -------
all_binfile_isect_ids = set(target_isects) | {oid for oid, _ in neighbor_isects_flat}
if all_binfile_isect_ids:
    print()
    print("  Cross-checking .bin INTERSECTION osm_ids against graph:")
    for oid in sorted(all_binfile_isect_ids):
        chunks_with_oid = [c for c, ids in isect_ids_by_chunk.items() if oid in ids]
        node = graph.nodes.get(oid)
        if node is None:
            print(f"    osm_id={oid}: found in {chunks_with_oid} — NOT IN GRAPH (stale?)")
            continue
        inside = in_rect(node.world_x, node.world_z, x0, z0, x1, z1)
        tag = "IN  05_02" if inside else "OUT 05_02"
        print(f"    [{tag}] osm_id={oid} found_in_chunks={chunks_with_oid}  world=({node.world_x:.1f},{node.world_z:.1f})")

# --- 3c. Highway ways near 05_02: do crossing ways share a node? ------------
print()
print("  Highway way node-sharing near chunk_05_02 seam:")

# Collect edges whose centerline centroid is near 05_02
seam_margin = 50.0   # metres from chunk boundary
near_edges = [
    e for e in graph.edges
    if (x0 - seam_margin <= sum(p[0] for p in e.centerline) / len(e.centerline) <= x1 + seam_margin and
        z0 - seam_margin <= sum(p[1] for p in e.centerline) / len(e.centerline) <= z1 + seam_margin)
]

# Build node → way_ids map for these edges
node_to_ways: dict[int, set[int]] = {}
for e in near_edges:
    for nid in [e.from_node.osm_id, e.to_node.osm_id]:
        node_to_ways.setdefault(nid, set()).add(e.osm_way_id)

# Report nodes shared by 2+ ways (real intersections) vs. none (Mode A suspect)
shared = {nid: ways for nid, ways in node_to_ways.items() if len(ways) >= 2}
print(f"  Edges near 05_02 seam: {len(near_edges)}")
print(f"  Nodes shared by 2+ ways (= is_intersection): {len(shared)}")
for nid, ways in list(shared.items())[:20]:
    n = graph.nodes[nid]
    inside = in_rect(n.world_x, n.world_z, x0, z0, x1, z1)
    tag = "IN  05_02" if inside else "OUT 05_02"
    print(f"    [{tag}] node={nid} ways={sorted(ways)}  world=({n.world_x:.1f},{n.world_z:.1f})")

# ---------------------------------------------------------------------------
# Summary / diagnosis
# ---------------------------------------------------------------------------
print()
print("=" * 60)
print("Diagnosis")
print("=" * 60)
if not target_isects and not neighbor_isects_flat:
    print("  Candidate: MODE A — no INTERSECTION polygon anywhere near 05_02.")
    print("  Check: do the visually-crossing ways above share any node ID?")
    if not shared:
        print("  CONFIRMED MODE A: no shared nodes near the seam.")
    else:
        print("  Shared nodes exist — inspect whether the visual junction is")
        print("  among them; shared-node at seam but no polygon → check compute_polygons.")
elif not target_isects and neighbor_isects_flat:
    ids_outside = [oid for oid, chunk in neighbor_isects_flat]
    print(f"  Candidate: MODE B — INTERSECTION polygon exists in neighbor(s),")
    print(f"  NOT in chunk_05_02.  Neighbor osm_ids: {ids_outside}")
    print("  The junction node centre is outside 05_02 → crop_to_chunk excludes it,")
    print("  road.py:70-73 rejects the setback anchor → gap at seam.")
elif target_isects:
    target_set = set(target_isects)
    duped = [(oid, chunk) for oid, chunk in neighbor_isects_flat if oid in target_set]
    if duped:
        print(f"  CONFIRMED MODE C — seam duplication.")
        print(f"  chunk_05_02 has {len(target_isects)} INTERSECTION mesh(es), but")
        print(f"  {len(duped)} node(s) also generate a mesh in a neighbor chunk:")
        for oid, chunk in duped:
            print(f"    osm_id={oid} also in chunk_{chunk[0]:02d}_{chunk[1]:02d}")
        print("  Root cause: chunk.py places an INTERSECTION mesh for every node in")
        print("  the cropped graph without checking that the node centre is inside the")
        print("  chunk's own world rect.  Neighbour chunk emits the same mesh for the")
        print("  same node → z-fighting polygons + unanchored road ribbons at the seam.")
        print("  Fix: guard at chunk.py:153 — skip if node outside chunk's own rect.")
    else:
        print(f"  INTERSECTION polygon IS present in 05_02 (osm_ids={target_isects}).")
        print("  No seam duplication detected.  Investigate road mesh boundary anchors.")

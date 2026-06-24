"""Static spike for #141 — stdlib only (no shapely/numpy/osmium, no bake).

Replicates osm.parse intersection detection + crop_to_chunk seam logic for
chunk_05_02 (cs300 grid) to decide Mode A (topology) vs Mode B (seam dropout).
"""
import math
import xml.etree.ElementTree as ET

OSM = "My project (2)/Assets/SFMapData/map.osm"
CHUNK_SIZE = 300.0
COL, ROW = 5, 2
# from chunks_cs300/manifest.json
BOUNDS = dict(minLat=37.73364, maxLat=37.74669, minLon=-122.45273, maxLon=-122.43042)
CHUNK_ORIGIN = (-101.984, -604.282)  # worldX, worldZ for chunk 05_02

# ---- projection (mirrors projection.py) ----
center_lat = (BOUNDS["minLat"] + BOUNDS["maxLat"]) / 2
center_lon = (BOUNDS["minLon"] + BOUNDS["maxLon"]) / 2
mpd_lon = math.cos(math.radians(center_lat)) * 111320.0
mpd_lat = 111320.0
def to_xz(lon, lat):
    return ((lon - center_lon) * mpd_lon, (lat - center_lat) * mpd_lat)

# ---- parse OSM (stdlib) ----
root = ET.parse(OSM).getroot()
raw_nodes = {}   # id -> (lat, lon, tags)
for n in root.findall("node"):
    nid = int(n.get("id"))
    tags = {t.get("k"): t.get("v") for t in n.findall("tag")}
    raw_nodes[nid] = (float(n.get("lat")), float(n.get("lon")), tags)

highway_ways = []  # (way_id, [node_refs])
for w in root.findall("way"):
    tags = {t.get("k"): t.get("v") for t in w.findall("tag")}
    if "highway" not in tags:
        continue
    refs = [int(nd.get("ref")) for nd in w.findall("nd")]
    highway_ways.append((int(w.get("id")), refs, tags))

# ---- intersection detection (osm.py:310-326): count DISTINCT ways per node ----
ref_counts = {}
for wid, refs, _ in highway_ways:
    for nid in set(refs):
        ref_counts[nid] = ref_counts.get(nid, 0) + 1

node_world = {}   # nid -> (x,z) for nodes present in highway ways
is_inter = {}
for nid, cnt in ref_counts.items():
    if nid not in raw_nodes:
        continue
    lat, lon, _ = raw_nodes[nid]
    node_world[nid] = to_xz(lon, lat)
    is_inter[nid] = cnt >= 2

# ---- base_x/base_z = geometry extent min (sfmap_bake._geometry_extent) ----
xs = [p[0] for p in node_world.values()]
zs = [p[1] for p in node_world.values()]
# (buildings omitted — nodes+centerlines share the same coords; close enough for base check)
base_x, base_z = min(xs), min(zs)
exp_x = base_x + COL * CHUNK_SIZE
exp_z = base_z + ROW * CHUNK_SIZE
print(f"computed base=({base_x:.1f},{base_z:.1f}) -> chunk05_02 origin=({exp_x:.1f},{exp_z:.1f})")
print(f"manifest chunk05_02 origin={CHUNK_ORIGIN}  (delta x={exp_x-CHUNK_ORIGIN[0]:.1f}, z={exp_z-CHUNK_ORIGIN[1]:.1f})")

# Use the manifest origin as ground truth for the rect (exact, includes buildings in base).
x0, z0 = CHUNK_ORIGIN
x1, z1 = x0 + CHUNK_SIZE, z0 + CHUNK_SIZE
def in_rect(x, z):
    return x0 <= x <= x1 and z0 <= z <= z1

# ---- split ways at intersections -> edges (osm.py:_split_at_intersections) ----
def split(refs):
    segs, cur = [], [refs[0]]
    for i in range(1, len(refs)):
        nid = refs[i]; cur.append(nid)
        last = i == len(refs) - 1
        if not last and is_inter.get(nid):
            segs.append(cur); cur = [nid]
    segs.append(cur)
    return segs

edges = []  # (way_id, from_nid, to_nid, [world pts])
for wid, refs, _ in highway_ways:
    for seg in split(refs):
        if len(seg) < 2:
            continue
        pts = [node_world[n] for n in seg if n in node_world]
        if len(pts) < 2:
            continue
        edges.append((wid, seg[0], seg[-1], pts))

def centroid(pts):
    return (sum(p[0] for p in pts)/len(pts), sum(p[1] for p in pts)/len(pts))

# ---- crop_to_chunk (osm.py:113) ----
kept_edges = [e for e in edges if in_rect(*centroid(e[3]))]
inter_nodes_in_rect = [nid for nid, v in is_inter.items() if v and in_rect(*node_world[nid])]

print(f"\n=== chunk_05_02 rect x[{x0:.1f},{x1:.1f}] z[{z0:.1f},{z1:.1f}] ===")
print(f"kept road edges (centroid in rect): {len(kept_edges)}")
print(f"intersection nodes with CENTER in rect (polygon meshes here, roads trim): {len(inter_nodes_in_rect)}")

# ---- Mode B victims: edge in this chunk, but its intersection endpoint sits OUTSIDE ----
victims = {}
for wid, fn, tn, pts in kept_edges:
    for endpoint in (fn, tn):
        if is_inter.get(endpoint) and not in_rect(*node_world[endpoint]):
            victims.setdefault(endpoint, set()).add(wid)
print(f"\nMODE B victims — intersection nodes whose center is OUTSIDE the rect")
print(f"  but which have road edges kept INSIDE this chunk: {len(victims)}")
for nid, ways in list(victims.items())[:8]:
    wx, wz = node_world[nid]
    side = []
    if wx < x0: side.append("W");
    if wx > x1: side.append("E")
    if wz < z0: side.append("S")
    if wz > z1: side.append("N")
    print(f"    node {nid} at ({wx:.1f},{wz:.1f}) [{'/'.join(side) or 'in?'}] "
          f"degree={ref_counts[nid]} ways_into_chunk={sorted(ways)}")

# ---- Mode A scan: highway segments crossing geometrically inside rect w/o shared node ----
def seg_int(p1, p2, p3, p4):
    def ccw(a, b, c):
        return (c[1]-a[1])*(b[0]-a[0]) - (b[1]-a[1])*(c[0]-a[0])
    d1, d2 = ccw(p3, p4, p1), ccw(p3, p4, p2)
    d3, d4 = ccw(p1, p2, p3), ccw(p1, p2, p4)
    return (d1*d2 < 0) and (d3*d4 < 0)

# collect highway segments (consecutive node pairs) with endpoints, near rect
segs = []
for wid, refs, _ in highway_ways:
    for a, b in zip(refs, refs[1:]):
        if a in node_world and b in node_world:
            pa, pb = node_world[a], node_world[b]
            # keep if either endpoint within an expanded rect
            if (x0-50 <= pa[0] <= x1+50 and z0-50 <= pa[1] <= z1+50) or \
               (x0-50 <= pb[0] <= x1+50 and z0-50 <= pb[1] <= z1+50):
                segs.append((wid, a, b, pa, pb))
modeA = []
for i in range(len(segs)):
    w1, a1, b1, p1, p2 = segs[i]
    for j in range(i+1, len(segs)):
        w2, a2, b2, p3, p4 = segs[j]
        if w1 == w2:
            continue
        if {a1, b1} & {a2, b2}:
            continue  # share a node already -> proper junction
        if seg_int(p1, p2, p3, p4):
            modeA.append((w1, w2))
print(f"\nMODE A candidates — geometric crossings near rect with NO shared node: {len(modeA)}")
for w1, w2 in modeA[:8]:
    print(f"    ways {w1} x {w2}")

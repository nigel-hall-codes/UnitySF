# #141 — Spike findings

**Verdict: Mode B (chunk-seam dropout). Mode A (topology) ruled out.**

## Method (static, no bake)

Stdlib-only inspector (`scripts/inspect_141.py`, xml.etree + math — no shapely/numpy/osmium, no bake) replicating the real pipeline:
- intersection detection `is_intersection = (distinct highway ways at node) >= 2` (`osm.py:310-326`)
- way splitting at intersections (`osm.py:_split_at_intersections`)
- `crop_to_chunk` keep rules: edge kept if centerline **centroid** in rect; intersection node kept if its **center** in rect (`osm.py:113-150`)

Grid: cs300 / in-game `default` Resources (300 m chunks, `chunk_<col>_<row>`). `chunk_05_02` = col 5, row 2, rect **x[−102.0, 198.0] × z[−604.3, −304.3]**, origin from `chunks_cs300/manifest.json`. Validation: base recomputed from OSM geometry → chunk origin matches the manifest to **0.0 m**, confirming the projection replication is exact.

## Evidence

| Metric (chunk_05_02) | Count |
|---|---|
| Road edges kept (centroid in rect) | 110 |
| Intersection nodes resolving in-rect (polygon meshes, roads trim) | 70 |
| **Mode B victims** (intersection center *outside* rect, but road edges kept *inside*) | **6** |
| **Mode A candidates** (geometric crossing near rect, no shared node) | **0** |

The 6 victims sit just across a seam and have road edges pulled into `05_02`:

```
node 3283287022  (33.2, -295.3)  [N, 9m past top seam]  degree=3  way 1208185050
node 9251127782  (38.4, -300.7)  [N]                    degree=2  way 1002384538
node 4580647731  (77.2, -275.4)  [N]                    degree=2  way 936551747
node 11787245919 (-40.8, -632.4) [S]                    degree=2  way 159150002
node 65336780    (237.1, -335.7) [E]                    degree=2  way 148315666
node 11783617107 (229.1, -487.9) [E]                    degree=2  way 1268736305
```

`3283287022` (degree-3, multi-arm, 9 m north of the top seam) is the most likely match for the screenshot's star-shaped junction: its node center lives in the chunk to the north, so the polygon meshes there and the roads trim there — while in `05_02` the southward arms run untrimmed with no polygon, producing the overlapping ribbons.

## Why this is Mode B, not Mode A

- **Mode A ruled out:** 0 geometric crossings without a shared node near the rect. The OSM data is well-noded — every junction *is* a shared node, so detection (`count >= 2`) is firing correctly. The problem is not topology.
- **Mode B confirmed:** 6 detected intersections are dropped purely by the chunk crop. Their polygons mesh in the neighbor chunk (which owns the node center) and their road setbacks are rejected in `05_02` by the bounds guard at `road.py:70-73`. Net: untrimmed overlap + missing polygon on the `05_02` side of the seam — exactly the reported symptom, and exactly the structural bug predicted in `#140` prior-art.

## Consequence for the fix (unblocks plan item C2, cancels C3)

Proceed with **C2 — seam-aware intersection emission** (`fix`). Two cooperating changes:
1. Emit a junction polygon in every chunk its geometry overlaps, not only the chunk owning the node center (`chunk.py:153` iterates the cropped graph; `crop_to_chunk` drops cross-seam nodes).
2. Allow a setback anchor that lies just across the seam to still trim the road, instead of the wholesale rejection at `road.py:70-73` (handle the out-of-bounds height-sample hazard that guard exists for, rather than skipping the anchor).

The per-edge `boundaries` already exist on the full graph, so the data is in hand — this is plumbing, not new geometry. **6 victims in this one chunk** implies a sizeable map-wide class, so C2 should fix the category, not just `3283287022`. **C3 (synthesize intersections) is not needed** — close/abandon it.

# Spike Finding — Intersection failure mode at chunk_05_02

**Result: Neither Mode A nor Mode B. Actual failure is seam duplication (call it Mode C).**

chunk_05_02 contains **14 INTERSECTION meshes** — the polygon geometry is not missing.
However, 5 of those nodes also generate INTERSECTION meshes in an adjacent chunk:
`2320368479` and `65293884` appear in both (5,2) and (5,1); `65336780`, `2320396530`,
and `1614176501` appear in both (5,2) and (6,2). In each case the node's
world position is at or just outside the chunk_05_02 boundary (e.g. `2320368479` sits at
z=-642.5, which is 38m south of 05_02's z_min=-604.3; `65336780` sits at x=237.1, which
is 39m east of 05_02's x_max=198). The duplication arises because `crop_to_chunk`
correctly includes out-of-bounds nodes as edge endpoints (rule 1), but `bake_chunk`
(chunk.py:153-160) then places an INTERSECTION mesh for **every** intersection node in
the cropped graph with no check that the node centre falls inside the chunk's own world
rect. The neighbour chunk produces the same mesh for the same node (its centre is inside
the neighbour). The result is two overlapping, z-fighting intersection polygons at each
seam node. Compounding this, the road setback anchor for these nodes is correctly
rejected by road.py:70-73 (anchor outside chunk bounds), so the road ribbon in 05_02
extends past its proper stopping point into the intersection area, producing the visible
overlapping road strips.

## Evidence

| node osm_id | world (x, z) | in 05_02 bounds? | also in chunk |
|---|---|---|---|
| 2320368479 | (66.3, -642.5) | No (z < -604.3) | 5,1 |
| 65293884 | (-40.6, -640.0) | No (z < -604.3) | 4,1 |
| 65280473 | (139.2, -588.9) | Yes (inside) | 5,1 |
| 65336780 | (237.1, -335.7) | No (x > 198) | 6,2 |
| 2320396530 | (233.5, -496.5) | No (x > 198) | 6,2 |
| 1614176501 | (196.3, -599.9) | Yes (inside) | 6,2 |

Chunk_05_02 world rect: x[-102, 198] z[-604.3, -304.3]; chunk_size=300m.

## Follow-up

**Mode C -> open plan item C2 (seam-aware fix).**

The fix is a one-line guard in `chunk.py:153` — before placing an INTERSECTION mesh,
check that `x_min <= node.world_x <= x_min + size and z_min <= node.world_z <= z_min + size`.
Nodes outside the chunk's own bounds are edge endpoints and must stay in the cropped graph
for road geometry, but their INTERSECTION polygon belongs to the neighbour chunk only.

## Diagnostics

Script: `worktrees/sdlc-141-spike-confirm-failure-mode/python/spike_141.py`
Run from repo root: `python worktrees/sdlc-141-spike-confirm-failure-mode/python/spike_141.py`

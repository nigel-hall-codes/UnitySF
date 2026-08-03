"""Commercial POI → building association (#486).

San Francisco's ground-floor retail is mapped as a ``shop``/``amenity``/``office``
**node dropped inside the building footprint**, not as a tag on the building way.
Measured over ``full_sf_map`` (159,313 buildings; 8,996 commercial POI nodes land
inside one):

==========================================  =========  ======
signal                                      buildings   share
==========================================  =========  ======
way tags only                                   1,706   1.07%
…plus POI nodes contained in the footprint      7,011   4.40%
==========================================  =========  ======

Of the 7,011 buildings with any commercial evidence, **5,305 (75.7%) have it only
from a contained node** — invisible to a tag read. Hence this module: a
point-in-footprint pass that counts, per building, how many commercial POI nodes
sit inside it.

Pure function of its inputs (world-XZ rings and world-XZ points), so a re-bake of
the same OSM yields identical counts — the determinism contract (design #266
``data-model.md`` §6). The uniform-grid index is an accelerator only; it never
changes the answer.
"""
from __future__ import annotations

from typing import Dict, List, Sequence, Tuple

Point = Tuple[float, float]

# Index cell size, metres. A city building's bbox spans one or two cells at this
# pitch, so the grid stays small while each POI tests only a handful of candidates.
_CELL_M = 50.0


def _point_in_ring(x: float, z: float, ring: Sequence[Point]) -> bool:
    """Crossing-number point-in-polygon test (same ray cast as ``geometry.neighborhood``).

    A POI landing exactly on an edge may fall either side; immaterial here, since a
    shop node is placed inside the shop, never on the wall line to float precision.
    """
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, zi = ring[i]
        xj, zj = ring[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


def commercial_poi_counts(
    footprints: Sequence[Sequence[Point]], pois: Sequence[Point]
) -> List[int]:
    """Count the commercial POI points inside each footprint.

    ``footprints`` are world-XZ rings (closing vertex optional); ``pois`` are
    world-XZ points already filtered to commercial premises by
    ``tags.is_commercial_poi``. Returns one count per footprint, in input order.

    A POI inside two overlapping footprints (a building mapped twice, or a
    courtyard ring inside a block) is credited to the **lowest-indexed** containing
    footprint only, so the total never exceeds ``len(pois)`` and the result does not
    depend on iteration order.
    """
    counts = [0] * len(footprints)
    if not footprints or not pois:
        return counts

    # Bucket each footprint into every grid cell its bbox touches.
    grid: Dict[Tuple[int, int], List[int]] = {}
    bboxes: List[Tuple[float, float, float, float]] = []
    for idx, ring in enumerate(footprints):
        if len(ring) < 3:
            bboxes.append((1.0, 1.0, -1.0, -1.0))   # empty bbox — never matches
            continue
        xs = [p[0] for p in ring]
        zs = [p[1] for p in ring]
        bb = (min(xs), min(zs), max(xs), max(zs))
        bboxes.append(bb)
        for cx in range(int(bb[0] // _CELL_M), int(bb[2] // _CELL_M) + 1):
            for cz in range(int(bb[1] // _CELL_M), int(bb[3] // _CELL_M) + 1):
                grid.setdefault((cx, cz), []).append(idx)

    for px, pz in pois:
        candidates = grid.get((int(px // _CELL_M), int(pz // _CELL_M)))
        if not candidates:
            continue
        # Candidates are appended in ascending footprint index, so the first hit is
        # the lowest-indexed containing footprint.
        for idx in candidates:
            min_x, min_z, max_x, max_z = bboxes[idx]
            if not (min_x <= px <= max_x and min_z <= pz <= max_z):
                continue
            if _point_in_ring(px, pz, footprints[idx]):
                counts[idx] += 1
                break

    return counts
